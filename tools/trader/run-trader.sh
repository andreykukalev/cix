#!/usr/bin/env bash
# CIX trader loop for the container (the Linux twin of run-trader.ps1). Each run is one headless Claude Code session that
# can use only the cix tools, with caps on turns and wall-clock time.
#
# Claude credentials come from a secret file (default /run/secrets/claude_token) holding either a setup token from
# `claude setup-token` (sk-ant-oat...) or an Anthropic API key (sk-ant-api...). The value is never printed or logged.
# CLAUDE_TOKEN_EXPIRES (YYYY-MM-DD, not a secret) records when a setup token expires: runs warn from 14 days before and
# stop once it has passed.
#
# Settings (environment): TRADER_INTERVAL_MINUTES (60), TRADER_MAX_TURNS (30), TRADER_TIMEOUT_MINUTES (10),
# TRADER_RUN_ONCE (0), CLAUDE_TOKEN_FILE, CLAUDE_TOKEN_EXPIRES.
set -uo pipefail

dir="${CLAUDE_PROJECT_DIR:-/cix}"
interval="${TRADER_INTERVAL_MINUTES:-60}"
max_turns="${TRADER_MAX_TURNS:-30}"
timeout_minutes="${TRADER_TIMEOUT_MINUTES:-10}"
token_file="${CLAUDE_TOKEN_FILE:-/run/secrets/claude_token}"
expires="${CLAUDE_TOKEN_EXPIRES:-}"
logs="$dir/logs/trader"
mkdir -p "$logs"

say() { printf '[%s] %s\n' "$(date -u +%H:%M)" "$*"; }

for n in "$interval" "$max_turns" "$timeout_minutes"; do
  [[ "$n" =~ ^[1-9][0-9]*$ ]] || { say "TRADER_INTERVAL_MINUTES, TRADER_MAX_TURNS and TRADER_TIMEOUT_MINUTES must be positive whole numbers."; exit 1; }
done

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
    say "The Claude setup token expired on $expires. Run 'claude setup-token', then tools/setup-secrets.ps1 -Claude, and restart the trader."
    return 1
  fi
  if (( days <= 14 )); then
    say "Warning: the Claude setup token expires in $days days ($expires). Renew it with 'claude setup-token'."
  fi
  return 0
}

# The engine shares this container's network namespace; give it time after a restart, then exit so Docker restarts
# the trader and it rejoins the engine's (possibly new) namespace.
wait_for_engine() {
  for _ in $(seq 1 30); do
    if (exec 3<>/dev/tcp/127.0.0.1/5077) 2>/dev/null; then return 0; fi
    sleep 10
  done
  say "The engine's tool server isn't reachable at 127.0.0.1:5077 after 5 minutes; exiting so Docker restarts the trader."
  exit 1
}

instructions="$(awk 'BEGIN{n=0} /^---[[:space:]]*$/{n++; next} n>=2' "$dir/.claude/agents/trader.md")"
tools="mcp__cix__get_strategy_spec,mcp__cix__get_portfolio,mcp__cix__get_position_plans,mcp__cix__get_open_orders,mcp__cix__get_market_view,mcp__cix__get_features,mcp__cix__scan_market,mcp__cix__place_order,mcp__cix__cancel_order"
denied="Bash,Read,Edit,Write,MultiEdit,NotebookEdit,Glob,Grep,LS,WebFetch,WebSearch,Task,TodoWrite"

run_once() {
  local run_id log
  run_id="run-$(date -u +%Y%m%d-%H%M%S)"
  log="$logs/$run_id.json"
  export CIX_RUN_ID="$run_id"

  load_credentials || return 0
  check_expiry || return 0
  wait_for_engine
  say "$run_id starting ($auth)"

  (cd "$dir" && timeout "${timeout_minutes}m" claude -p 'Run one trading cycle of the active strategy now.' \
      --append-system-prompt "$instructions" \
      --mcp-config "$dir/tools/trader/mcp.json" --strict-mcp-config \
      --allowedTools "$tools" --disallowedTools "$denied" \
      --max-turns "$max_turns" \
      --output-format json) > "$log" 2>&1
  local code=$?

  if (( code == 124 )); then
    say "$run_id stopped after $timeout_minutes minutes."
    return 0
  fi
  if ! jq -e . "$log" >/dev/null 2>&1; then
    if grep -qiE 'invalid api key|authentication|unauthori[sz]ed|401|oauth|expired' "$log"; then
      say "$run_id: Claude rejected the credentials ($auth). Renew with 'claude setup-token' (or a new API key), run tools/setup-secrets.ps1 -Claude, and restart the trader."
    else
      say "$run_id failed (exit $code); see logs/trader/$run_id.json."
    fi
    return 0
  fi

  if [[ "$(jq -r '.is_error // false' "$log")" == "true" ]]; then
    say "$run_id ended with an error: $(jq -r '.subtype // "unknown"' "$log")"
    if jq -r '.result // ""' "$log" | grep -qiE 'invalid api key|authentication|401|oauth'; then
      say "Claude rejected the credentials ($auth). Renew them and restart the trader."
    fi
  fi
  say "$run_id done. Cost: $(jq -r '.total_cost_usd // "?"' "$log") USD, turns: $(jq -r '.num_turns // "?"' "$log")"
  jq -r '.result // ""' "$log"
}

if [[ "${TRADER_RUN_ONCE:-0}" == "1" ]]; then
  run_once
  exit 0
fi

# Runs start on a fixed cadence, so a slow run doesn't push every later run back.
next=$(date +%s)
while true; do
  run_once
  next=$(( next + interval * 60 ))
  wait=$(( next - $(date +%s) ))
  (( wait > 0 )) && sleep "$wait"
done
