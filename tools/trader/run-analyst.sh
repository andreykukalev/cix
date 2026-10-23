#!/usr/bin/env bash
# CIX market analyst loop (#25). Every ANALYST_INTERVAL_HOURS, one headless Claude Code session reads the engine's brief and
# recent news, then records a market view (risk-on, neutral or risk-off, plus symbols to avoid). It can use only the two
# analyst tools and web search/fetch: no order tools, files or shell. Its view can only make trading more cautious, and it
# expires, so a failed or missing run never blocks trading.
#
# Settings (environment): ANALYST_INTERVAL_HOURS (6), ANALYST_MODEL (opus), ANALYST_MAX_TURNS (25),
# ANALYST_TIMEOUT_MINUTES (10), ANALYST_RUN_ONCE (0), CLAUDE_TOKEN_FILE, CLAUDE_TOKEN_EXPIRES.
set -uo pipefail

dir="${CLAUDE_PROJECT_DIR:-/cix}"
interval="${ANALYST_INTERVAL_HOURS:-6}"
model="${ANALYST_MODEL:-opus}"
max_turns="${ANALYST_MAX_TURNS:-25}"
timeout_minutes="${ANALYST_TIMEOUT_MINUTES:-10}"
token_file="${CLAUDE_TOKEN_FILE:-/run/secrets/claude_token}"
expires="${CLAUDE_TOKEN_EXPIRES:-}"
logs="$dir/logs/analyst"
mkdir -p "$logs"

agent_name="analyst"
# shellcheck source=tools/trader/agent-common.sh
source "$dir/tools/trader/agent-common.sh"

[[ "$model" =~ ^[a-z0-9][a-z0-9.-]{1,63}(\[1m\])?$ ]] || { say "ANALYST_MODEL must be a lowercase model alias or name, like opus or sonnet."; exit 1; }
for n in "$interval" "$max_turns" "$timeout_minutes"; do
  [[ "$n" =~ ^[1-9][0-9]*$ ]] || { say "ANALYST_* settings must be positive whole numbers."; exit 1; }
done
(( interval <= 12 )) || { say "ANALYST_INTERVAL_HOURS can be at most 12 (a view lasts at most 12 hours)."; exit 1; }

instructions="$(awk 'BEGIN{n=0} /^---[[:space:]]*$/{n++; next} n>=2' "$dir/.claude/agents/analyst.md")"
tools="mcp__cix__get_analyst_brief,mcp__cix__record_market_view,WebSearch,WebFetch"
# Trading tools denied explicitly as well (the engine refuses them for the analyst too).
denied="Bash,Read,Edit,Write,MultiEdit,NotebookEdit,Glob,Grep,LS,Task,TodoWrite,mcp__cix__place_order,mcp__cix__cancel_order,mcp__cix__get_portfolio,mcp__cix__get_position_plans,mcp__cix__get_open_orders,mcp__cix__get_market_view,mcp__cix__get_features,mcp__cix__scan_market,mcp__cix__get_strategy_spec"

run_once() {
  local run_id log
  run_id="analyst-$(date -u +%Y%m%d-%H%M%S)"
  log="$logs/$run_id.json"
  export CIX_RUN_ID="$run_id"

  load_credentials || return 1
  check_expiry || return 1
  wait_for_engine
  say "$run_id starting ($auth, model $model)"

  (cd "$dir" && timeout "${timeout_minutes}m" claude -p 'Assess the market now and record your view.' \
      --append-system-prompt "$instructions" \
      --mcp-config "$dir/tools/trader/analyst-mcp.json" --strict-mcp-config \
      --allowedTools "$tools" --disallowedTools "$denied" \
      --model "$model" \
      --max-turns "$max_turns" \
      --output-format json) > "$log" 2> "$logs/$run_id.err"
  local code=$?
  [[ -s "$logs/$run_id.err" ]] || rm -f "$logs/$run_id.err"

  if (( code == 124 )); then
    say "$run_id stopped after $timeout_minutes minutes; the previous view stays until it expires."
    return 1
  fi
  if ! jq -e . "$log" >/dev/null 2>&1; then
    say "$run_id failed (exit $code); see logs/analyst/$run_id.json and .err."
    return 1
  fi
  say "$run_id done. Models used: $(jq -r '(.modelUsage // {}) | keys | join(", ")' "$log"), cost: $(jq -r '.total_cost_usd // "?"' "$log") USD, turns: $(jq -r '.num_turns // "?"' "$log")"
  jq -r '.result // ""' "$log"
  [[ "$(jq -r '.is_error // false' "$log")" != "true" ]]
}

if [[ "${ANALYST_RUN_ONCE:-0}" == "1" ]]; then
  run_once
  exit $?
fi

# Fixed cadence from start; a run that overruns doesn't cause catch-up runs.
next=$(date +%s)
while true; do
  run_once || true
  next=$(( next + interval * 3600 ))
  now=$(date +%s)
  if (( next > now )); then sleep $(( next - now )); else next=$now; fi
done
