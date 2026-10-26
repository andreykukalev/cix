#!/usr/bin/env bash
# CIX market analyst loop (#25). Every ANALYST_INTERVAL_HOURS, one headless Claude Code session reads the engine's brief and
# recent news, then records a market view (risk-on, neutral or risk-off, plus symbols to avoid). It can use only the two
# analyst tools and web search/fetch: no order tools, files or shell. Its view can only make trading more cautious, and it
# expires, so a failed or missing run never blocks trading.
#
# Web pages can be fetched only from ANALYST_WEB_DOMAINS (comma-separated; subdomains included), enforced by Claude Code's
# permission rules. Search is open, but the analyst reads full articles only from those sites.
#
# A run that couldn't use Claude (usage or rate limit, missing or expired credentials) is retried after
# ANALYST_RETRY_MINUTES instead of waiting for the next slot.
#
# In Azure the engine keeps the cadence and retries (ADR-0013): with CIX_JOB_MODE=1 and CIX_JOB=analyst this script runs
# one queued run and exits, as the cix-analyst job. See agent-job.sh.
#
# Settings (environment): ANALYST_INTERVAL_HOURS (6), ANALYST_RETRY_MINUTES (30), ANALYST_MODEL (opus),
# ANALYST_MAX_TURNS (25), ANALYST_TIMEOUT_MINUTES (10), ANALYST_WEB_DOMAINS, ANALYST_RUN_ONCE (0), CLAUDE_TOKEN_FILE,
# CLAUDE_TOKEN_EXPIRES.
set -uo pipefail

dir="${CLAUDE_PROJECT_DIR:-/cix}"
interval="${ANALYST_INTERVAL_HOURS:-6}"
retry="${ANALYST_RETRY_MINUTES:-30}"
default_domains="federalreserve.gov,bls.gov,bea.gov,ecb.europa.eu,tradingeconomics.com,reuters.com,apnews.com,cnbc.com,coindesk.com,theblock.co,decrypt.co,rekt.news,defillama.com,tokenomist.ai,status.solana.com,revolut.com"
domains="${ANALYST_WEB_DOMAINS:-$default_domains}"
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
for n in "$interval" "$retry" "$max_turns" "$timeout_minutes"; do
  [[ "$n" =~ ^[1-9][0-9]*$ ]] || { say "ANALYST_* settings must be positive whole numbers."; exit 1; }
done
(( interval <= 12 )) || { say "ANALYST_INTERVAL_HOURS can be at most 12 (a view lasts at most 12 hours)."; exit 1; }

instructions="$(awk 'BEGIN{n=0} /^---[[:space:]]*$/{n++; next} n>=2' "$dir/.claude/agents/analyst.md")"
# WebFetch only for listed sites: one permission rule per domain and its subdomains (www., status. and so on).
fetch_rules=""
IFS=',' read -ra domain_list <<<"$domains"
for d in "${domain_list[@]}"; do
  d="${d// /}"
  [[ -z "$d" ]] && continue
  [[ "$d" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]] || { say "ANALYST_WEB_DOMAINS: '$d' isn't a plain domain like reuters.com."; exit 1; }
  fetch_rules+=",WebFetch(domain:$d),WebFetch(domain:*.$d)"
done
[[ -n "$fetch_rules" ]] || { say "ANALYST_WEB_DOMAINS is empty; the analyst needs at least one site to read."; exit 1; }
tools="mcp__cix__get_analyst_brief,mcp__cix__record_market_view,WebSearch${fetch_rules}"
site_note="You can open full pages only from these sites: ${domains//,/, }. Use search results to find articles there; a fetch elsewhere will be refused."
# Trading tools denied explicitly as well (the engine refuses them for the analyst too).
denied="Bash,Read,Edit,Write,MultiEdit,NotebookEdit,Glob,Grep,LS,Task,TodoWrite,mcp__cix__place_order,mcp__cix__cancel_order,mcp__cix__get_portfolio,mcp__cix__get_position_plans,mcp__cix__get_open_orders,mcp__cix__get_market_view,mcp__cix__get_features,mcp__cix__scan_market,mcp__cix__get_strategy_spec,mcp__cix__get_manual_requests,mcp__cix__answer_manual_request"

run_once() {
  local run_id log
  run_id="${job_run_id:-analyst-$(date -u +%Y%m%d-%H%M%S)}"   # job mode: the run the engine queued
  log="$logs/$run_id.json"
  export CIX_RUN_ID="$run_id"

  load_credentials || return "$NOT_RUN"
  check_expiry || return "$NOT_RUN"
  wait_for_engine
  say "$run_id starting ($auth, model $model)"

  (cd "$dir" && timeout "${timeout_minutes}m" env -u IDENTITY_ENDPOINT -u IDENTITY_HEADER -u MSI_ENDPOINT -u MSI_SECRET claude -p 'Assess the market now and record your view.' \
      --append-system-prompt "$instructions"$'\n\n'"$site_note" \
      --mcp-config "$dir/tools/trader/$mcp" --strict-mcp-config \
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
  if hit_usage_limit "$log" "$logs/$run_id.err"; then
    say "$run_id: Claude usage or rate limit reached; retrying in $retry minutes."
    return "$NOT_RUN"
  fi
  if ! jq -e . "$log" >/dev/null 2>&1; then
    say "$run_id failed (exit $code)."
    failure_details "$logs" "$run_id"
    return 1
  fi
  say "$run_id done. Models used: $(jq -r '(.modelUsage // {}) | keys | join(", ")' "$log"), cost: $(jq -r '.total_cost_usd // "?"' "$log") USD, turns: $(jq -r '.num_turns // "?"' "$log")"
  jq -r '.result // ""' "$log"
  [[ "$(jq -r '.is_error // false' "$log")" != "true" ]]
}

mcp="analyst-mcp.json"
if [[ "${CIX_JOB_MODE:-0}" == "1" ]]; then
  # shellcheck source=tools/trader/agent-job.sh
  source "$dir/tools/trader/agent-job.sh"
  mcp="mcp-azure.json"
  job_main "analyst" run_once
  exit $?
fi

if [[ "${ANALYST_RUN_ONCE:-0}" == "1" ]]; then
  run_once
  exit $?
fi

# Fixed cadence from start; a run that overruns doesn't cause catch-up runs. A run that couldn't use Claude is retried
# after ANALYST_RETRY_MINUTES (the slot it missed is not made up twice).
next=$(date +%s)
retries=0
max_retries=$(( interval * 60 / retry ))   # quick retries never exceed one interval's worth; then back to the normal slot
while true; do
  run_once
  rc=$?
  now=$(date +%s)
  if (( rc == NOT_RUN && retries < max_retries )); then
    retries=$(( retries + 1 ))
    next=$now   # the next completed run starts a fresh cadence, so a long outage isn't followed by a second run
    sleep $(( retry * 60 ))
    continue
  fi
  retries=0
  next=$(( next + interval * 3600 ))
  if (( next > now )); then sleep $(( next - now )); else next=$(( now + interval * 3600 )); sleep $(( interval * 3600 )); fi
done
