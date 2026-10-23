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

# The engine shares this container's network namespace; give it time after a restart, then exit so Docker restarts
# this agent and it rejoins the engine's (possibly new) namespace.
wait_for_engine() {
  for _ in $(seq 1 30); do
    if (exec 3<>/dev/tcp/127.0.0.1/5077) 2>/dev/null; then return 0; fi
    sleep 10
  done
  say "The engine's tool server isn't reachable at 127.0.0.1:5077 after 5 minutes; exiting so Docker restarts the $agent_name."
  exit 1
}

# Exit code for a run that didn't get to use Claude (no or expired credentials, usage or rate limit): such attempts don't
# count toward run caps, and the loops retry them sooner.
# Used by the scripts that source this file:
# shellcheck disable=SC2034
NOT_RUN=3

# Succeeds if a failed run ($1: JSON log, $2: stderr) was refused for usage or rate limits (the plan's limit or API
# throttling). A run that completed is never treated as limited, whatever its text says (news can mention "rate limits").
hit_usage_limit() {
  if jq -e . "$1" >/dev/null 2>&1 && [[ "$(jq -r '.is_error // false' "$1")" != "true" ]]; then
    return 1
  fi
  cat "$1" "$2" 2>/dev/null | grep -qiE 'usage limit|limit reached|rate[ _-]?limit|too many requests|\b429\b|overloaded|resets? at'
}

