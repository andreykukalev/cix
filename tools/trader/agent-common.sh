#!/usr/bin/env bash
# Shared by the CIX agent loops (run-trader.sh, run-analyst.sh): logging, Claude credentials and the engine check.
# The caller sets: agent_name, token_file, expires. Credentials are read from a secret file, exported only to this process
# tree, and never printed.
# agent_name, token_file and expires come from the caller:
# shellcheck disable=SC2154

say() { printf '[%s] %s\n' "$(date -u +%H:%M)" "$*"; }

# Credentials: read once, exported only to this process tree; never echoed.
load_credentials() {
  if [[ ! -s "$token_file" ]]; then
    say "No Claude credentials at $token_file. Run tools/setup-secrets.ps1 (or put a setup token or API key in secrets/claude_token)."
    return 1
  fi
  local value
  value="$(tr -d '\r\n' < "$token_file")"
  unset CLAUDE_CODE_OAUTH_TOKEN ANTHROPIC_API_KEY
  case "$value" in
    sk-ant-api*) export ANTHROPIC_API_KEY="$value"; auth="API key" ;;
    sk-ant-*)    export CLAUDE_CODE_OAUTH_TOKEN="$value"; auth="setup token" ;;
    *) say "secrets/claude_token doesn't look like a Claude setup token or API key (expected sk-ant-...)."; return 1 ;;
  esac
  return 0
}

# Returns 1 when a setup token has expired; warns from 14 days before.
check_expiry() {
  [[ "$auth" == "setup token" ]] || return 0
  if [[ -z "$expires" ]]; then
    say "Warning: CLAUDE_TOKEN_EXPIRES isn't set, so token expiry can't be checked (setup tokens last a year)."
    return 0
  fi
  local end now days
  end="$(date -u -d "$expires" +%s 2>/dev/null)" || { say "CLAUDE_TOKEN_EXPIRES must be a date like 2027-10-23."; return 0; }
  now="$(date -u +%s)"
  days=$(( (end - now) / 86400 ))
  if (( end <= now )); then
    say "The Claude setup token expired on $expires. Run 'claude setup-token', then tools/setup-secrets.ps1 -Claude, and restart the $agent_name."
    return 1
  fi
  if (( days <= 14 )); then
    say "Warning: the Claude setup token expires in $days days ($expires). Renew it with 'claude setup-token'."
  fi
  return 0
}

# Succeeds if this container has a network interface besides loopback. It shares the engine's network; when the engine
# container is restarted, that network goes and only loopback is left, so the new engine can never be reached from here.
has_engine_network() {
  local nic
  for nic in /sys/class/net/*; do
    [[ "${nic##*/}" != lo ]] && return 0
  done
  return 1
}

# Waits until the engine says it is ready (GET /healthz). After a reboot the engine may itself be waiting for the database,
# so this logs once a minute instead of failing. Exits (and Docker restarts the container) only when waiting can't help:
# the engine's network is gone, or nothing has answered for 10 minutes.
# In Azure (CIX_ENGINE_URL) the engine is another app: nothing to restart here, so waiting just ends after 10 minutes.
wait_for_engine() {
  local waited=0
  until curl -fsS --max-time 5 -o /dev/null "${CIX_ENGINE_URL:-http://127.0.0.1:5077}/healthz" 2>/dev/null; do
    if [[ -z "${CIX_ENGINE_URL:-}" ]] && (( waited >= 30 )) && ! has_engine_network; then
      say "The engine was restarted, so this container lost its network; exiting so Docker restarts the $agent_name with the new one."
      exit 1
    fi
    if (( waited >= 600 )); then
      say "The engine hasn't been ready for 10 minutes; exiting so Docker restarts the $agent_name."
      exit 1
    fi
    if (( waited > 0 && waited % 60 == 0 )); then
      say "Waiting for the engine to be ready (${waited}s so far)."
    fi
    sleep 10
    waited=$(( waited + 10 ))
  done
}

# Exit code for a run that didn't get to use Claude (no or expired credentials, usage or rate limit): such attempts don't
# count toward run caps, and the loops retry them sooner.
# Used by the scripts that source this file:
# shellcheck disable=SC2034
NOT_RUN=3

# Where to read a failed run's output ($1: log directory, $2: run ID). In Compose, its files under logs/. A job's files go
# with its container, so job mode prints the end of the run's stderr instead, for the job's log; anything shaped like a
# Claude key or an access token is masked first.
failure_details() {
  if [[ "${CIX_JOB_MODE:-0}" != 1 ]]; then
    say "See logs/$agent_name/$2.json and .err."
    return 0
  fi
  [[ -s "$1/$2.err" ]] || { say "$2 wrote nothing to stderr."; return 0; }
  say "$2 stderr (last 30 lines):"
  tail -n 30 "$1/$2.err" | sed -E -e 's/sk-ant-[A-Za-z0-9_-]+/sk-ant-***/g' \
    -e 's/eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*/<token>/g' -e 's/^/  | /'
}

# Succeeds if a run ($1: JSON log, $2: stderr) was refused for usage or rate limits before doing any work. Judged from the
# structured result when there is one: an error that cost nothing and whose message names a limit. Text inside a run that
# did work (news about "rate limits", a turn count of 429) never counts, so such a run can't escape the run caps.
limit_pattern='usage limit|limit reached|rate[ _-]?limit|too many requests|api error: ?429|status:? ?429|overloaded'
hit_usage_limit() {
  if jq -e . "$1" >/dev/null 2>&1; then
    [[ "$(jq -r '.is_error // false' "$1")" == "true" ]] || return 1
    [[ "$(jq -r '(.total_cost_usd // 0) == 0' "$1")" == "true" ]] || return 1
    jq -r '.result // ""' "$1" | grep -qiE "$limit_pattern"
    return
  fi
  grep -qiE "$limit_pattern" "$1" "$2" 2>/dev/null   # no JSON: the CLI stopped before the run (stderr or a plain message)
}

