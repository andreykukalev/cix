#!/usr/bin/env bash
# CIX trader loop for the container (the Linux twin of run-trader.ps1). Each run is one headless Claude Code session that
# can use only the cix tools, with caps on turns and wall-clock time.
#
# Claude credentials come from a secret file (default /run/secrets/claude_token) holding either a setup token from
# `claude setup-token` (sk-ant-oat...) or an Anthropic API key (sk-ant-api...). The value is never printed or logged.
# CLAUDE_TOKEN_EXPIRES (YYYY-MM-DD, not a secret) records when a setup token expires: runs warn from 14 days before and
# stop once it has passed.
#
# When the active strategy uses the scanner (scan_market), runs are signal-driven: the loop polls the engine's /signals
# (no LLM involved) and starts a run only when the scanner's signals change to something new, plus a slow heartbeat
# that reviews positions. Otherwise it runs every TRADER_INTERVAL_MINUTES.
#
# Signal-triggered runs are rate-limited: at least TRADER_MIN_GAP_MINUTES apart, and at most TRADER_MAX_RUNS_PER_DAY runs
# of any kind per UTC day (then the loop waits for the next day and says so).
#
# Owner requests from the dashboard (ADR-0011) that ask for the trader's analysis start a run of their own as soon as the
# engine reports them (/signals manualKey), without waiting for the minimum gap. They have their own daily cap
# (TRADER_MAX_MANUAL_RUNS_PER_DAY, 20), so owner requests can't use up the strategy's runs, and a narrower tool list: the
# read tools and the two request tools, never place_order or cancel_order. A failed attempt is retried after 5 minutes; a
# request the trader doesn't answer in time expires in the engine.
#
# In Azure the engine decides when to run (ADR-0013): with CIX_JOB_MODE=1 this script runs one queued run and exits, as the
# cix-trader job (CIX_JOB=trader) or the cix-trader-requests job (CIX_JOB=trader-requests). See agent-job.sh.
#
# Settings (environment): TRADER_INTERVAL_MINUTES (60), TRADER_HEARTBEAT_MINUTES (240), TRADER_POLL_SECONDS (60),
# TRADER_MIN_GAP_MINUTES (15), TRADER_MAX_RUNS_PER_DAY (30), TRADER_MAX_MANUAL_RUNS_PER_DAY (20), TRADER_MAX_TURNS (30),
# TRADER_TIMEOUT_MINUTES (10),
# TRADER_MODEL (sonnet; an alias like opus, sonnet or haiku, or a full model name),
# TRADER_RUN_ONCE (0), CLAUDE_TOKEN_FILE, CLAUDE_TOKEN_EXPIRES.
set -uo pipefail

dir="${CLAUDE_PROJECT_DIR:-/cix}"
interval="${TRADER_INTERVAL_MINUTES:-60}"
heartbeat="${TRADER_HEARTBEAT_MINUTES:-240}"
poll="${TRADER_POLL_SECONDS:-60}"
min_gap="${TRADER_MIN_GAP_MINUTES:-15}"
max_runs="${TRADER_MAX_RUNS_PER_DAY:-30}"
max_manual="${TRADER_MAX_MANUAL_RUNS_PER_DAY:-20}"
max_turns="${TRADER_MAX_TURNS:-30}"
model="${TRADER_MODEL:-sonnet}"
timeout_minutes="${TRADER_TIMEOUT_MINUTES:-10}"
token_file="${CLAUDE_TOKEN_FILE:-/run/secrets/claude_token}"
expires="${CLAUDE_TOKEN_EXPIRES:-}"
logs="$dir/logs/trader"
mkdir -p "$logs"

agent_name="trader"
# shellcheck source=tools/trader/agent-common.sh
source "$dir/tools/trader/agent-common.sh"

[[ "$model" =~ ^[a-z0-9][a-z0-9.-]{1,63}(\[1m\])?$ ]] || { say "TRADER_MODEL must be a lowercase model alias or name, like sonnet, claude-sonnet-5-5 or sonnet[1m]."; exit 1; }
for n in "$interval" "$heartbeat" "$poll" "$min_gap" "$max_runs" "$max_manual" "$max_turns" "$timeout_minutes"; do
  [[ "$n" =~ ^[1-9][0-9]*$ ]] || { say "TRADER_* settings must be positive whole numbers."; exit 1; }
done

instructions="$(awk 'BEGIN{n=0} /^---[[:space:]]*$/{n++; next} n>=2' "$dir/.claude/agents/trader.md")"
read_tools="mcp__cix__get_strategy_spec,mcp__cix__get_portfolio,mcp__cix__get_position_plans,mcp__cix__get_open_orders,mcp__cix__get_market_view,mcp__cix__get_features,mcp__cix__scan_market"
tools="$read_tools,mcp__cix__place_order,mcp__cix__cancel_order"
denied="Bash,Read,Edit,Write,MultiEdit,NotebookEdit,Glob,Grep,LS,WebFetch,WebSearch,Task,TodoWrite,mcp__cix__get_analyst_brief,mcp__cix__record_market_view"
# Strategy runs don't answer owner requests; request runs can only read and answer (the owner's note can't steer them into
# placing or cancelling orders).
trading_denied="$denied,mcp__cix__get_manual_requests,mcp__cix__answer_manual_request"
manual_tools="$read_tools,mcp__cix__get_manual_requests,mcp__cix__answer_manual_request"
manual_denied="$denied,mcp__cix__place_order,mcp__cix__cancel_order"

trading_prompt='Run one trading cycle of the active strategy now.'
manual_prompt='The owner has sent requests from the dashboard. Read get_strategy_spec, then get_manual_requests, and answer every request with answer_manual_request as your instructions describe. Do not run the regular trading cycle in this run.'

# The engine's latest scan: prints "signalDriven scanned key manualKey" (scanned is "yes" once the engine has scanned;
# an empty key is "-", so the fields stay in place), or nothing if the engine can't be asked.
signals() {
  curl -fsS --max-time 10 "http://127.0.0.1:5077/signals" 2>/dev/null \
    | jq -r '"\(.signalDriven) \(if .asOf == null then "no" else "yes" end) \(if (.key // "") == "" then "-" else .key end) \(if (.manualKey // "") == "" then "-" else .manualKey end)"' 2>/dev/null
}

# Succeeds if $1 (a signal key like "buy:SOL-EUR|sell:BTC-EUR:stop") holds an item that $2 doesn't.
has_new_signal() {
  local item
  local -a items
  IFS='|' read -ra items <<<"$1"
  for item in "${items[@]}"; do
    [[ -n "$item" && "|$2|" != *"|$item|"* ]] && return 0
  done
  return 1
}

run_once() {
  local run_id log why="${1:-scheduled}" prompt="${2:-$trading_prompt}" allowed="${3:-$tools}" refused="${4:-$trading_denied}" mcp="${5:-mcp.json}"
  run_id="${job_run_id:-run-$(date -u +%Y%m%d-%H%M%S)}"   # job mode: the run the engine queued
  log="$logs/$run_id.json"
  export CIX_RUN_ID="$run_id"

  load_credentials || { not_run_reason=credentials; return "$NOT_RUN"; }
  check_expiry || { not_run_reason=credentials; return "$NOT_RUN"; }
  wait_for_engine
  say "$run_id starting: $why ($auth, model $model)"

  (cd "$dir" && timeout "${timeout_minutes}m" env -u IDENTITY_ENDPOINT -u IDENTITY_HEADER -u MSI_ENDPOINT -u MSI_SECRET claude -p "$prompt" \
      --append-system-prompt "$instructions" \
      --mcp-config "$dir/tools/trader/$mcp" --strict-mcp-config \
      --allowedTools "$allowed" --disallowedTools "$refused" \
      --model "$model" \
      --max-turns "$max_turns" \
      --output-format json) > "$log" 2> "$logs/$run_id.err"
  local code=$?
  [[ -s "$logs/$run_id.err" ]] || rm -f "$logs/$run_id.err"

  if (( code == 124 )); then
    say "$run_id stopped after $timeout_minutes minutes."
    return 1
  fi
  if hit_usage_limit "$log" "$logs/$run_id.err"; then
    say "$run_id: Claude usage or rate limit reached; the signal stays pending and is retried after the gap (this attempt doesn't count toward the daily cap)."
    not_run_reason=usage-limit
    return "$NOT_RUN"
  fi
  if ! jq -e . "$log" >/dev/null 2>&1; then
    if cat "$log" "$logs/$run_id.err" 2>/dev/null | grep -qiE 'invalid api key|authentication_error|unauthori[sz]ed|oauth token (has )?expired|401'; then
      say "$run_id: Claude rejected the credentials ($auth). $(renew_hint)"
    else
      say "$run_id failed (exit $code)."
      failure_details "$logs" "$run_id"
    fi
    return 1
  fi

  if [[ "$(jq -r '.is_error // false' "$log")" == "true" ]]; then
    say "$run_id ended with an error: $(jq -r '.subtype // "unknown"' "$log")"
    if jq -r '.result // ""' "$log" | grep -qiE 'invalid api key|authentication|401|oauth'; then
      say "Claude rejected the credentials ($auth). $(renew_hint)"
    fi
  fi
  say "$run_id done. Models used: $(jq -r '(.modelUsage // {}) | keys | join(", ")' "$log"), cost: $(jq -r '.total_cost_usd // "?"' "$log") USD, turns: $(jq -r '.num_turns // "?"' "$log")"
  jq -r '.result // ""' "$log"
  [[ "$(jq -r '.is_error // false' "$log")" != "true" ]]
}

# One queued run, as an Azure job: strategy runs or owner-request runs, by the job's identity.
job_run() {
  if [[ "$CIX_JOB" == "trader-requests" ]]; then
    run_once "owner requests" "$manual_prompt" "$manual_tools" "$manual_denied" "mcp-azure.json"
  else
    run_once "$1" "$trading_prompt" "$tools" "$trading_denied" "mcp-azure.json"
  fi
}

if [[ "${CIX_JOB_MODE:-0}" == "1" ]]; then
  # shellcheck source=tools/trader/agent-job.sh
  source "$dir/tools/trader/agent-job.sh"
  job_main "trader|trader-requests" job_run
  exit $?
fi

if [[ "${TRADER_RUN_ONCE:-0}" == "1" ]]; then
  run_once
  exit 0
fi

last_key=""
next_run=$(date +%s)        # interval mode: the next scheduled run
last_run=0
run_day=""
runs_today=0
refunds_today=0
max_refunds=96   # at most one refunded attempt per 15 minutes
capped_day=""

# Every run goes through here: the daily cap applies to signal, heartbeat and scheduled runs alike.
guarded_run() {
  local today
  today="$(date -u +%F)"
  if [[ "$today" != "$run_day" ]]; then run_day="$today"; runs_today=0; refunds_today=0; fi
  if (( runs_today >= max_runs )); then
    if [[ "$capped_day" != "$today" ]]; then
      say "Daily cap of $max_runs runs reached; no more runs until 00:00 UTC (TRADER_MAX_RUNS_PER_DAY)."
      capped_day="$today"
    fi
    return 1
  fi
  runs_today=$(( runs_today + 1 ))
  last_run=$(date +%s)
  run_once "$1"
  local rc=$?
  # Claude wasn't used: don't spend the daily cap, up to a ceiling so a misjudged failure can't run unbounded.
  if (( rc == NOT_RUN && refunds_today < max_refunds )); then
    runs_today=$(( runs_today - 1 ))
    refunds_today=$(( refunds_today + 1 ))
  fi
  return "$rc"
}

manual_day=""
manual_today=0
manual_capped_day=""

# Runs for owner requests: their own daily cap and tool list, and they don't move the strategy's minimum gap.
guarded_manual_run() {
  local today
  today="$(date -u +%F)"
  if [[ "$today" != "$manual_day" ]]; then manual_day="$today"; manual_today=0; fi
  if (( manual_today >= max_manual )); then
    if [[ "$manual_capped_day" != "$today" ]]; then
      say "Daily cap of $max_manual owner-request runs reached; waiting requests expire in the engine (TRADER_MAX_MANUAL_RUNS_PER_DAY)."
      manual_capped_day="$today"
    fi
    return 1
  fi
  manual_today=$(( manual_today + 1 ))
  run_once "$1" "$manual_prompt" "$manual_tools" "$manual_denied" "mcp-requests.json"   # the engine enforces the same split
  local rc=$?
  (( rc == NOT_RUN )) && manual_today=$(( manual_today - 1 ))   # Claude wasn't used; retried after 5 minutes at most
  return "$rc"
}

next_heartbeat=0   # set once the engine has scanned, so the first run sees real signals
last_manual=""
manual_retry_at=0
while true; do
  wait_for_engine
  now=$(date +%s)
  read -r driven scanned key manual <<<"$(signals)"
  [[ "${key:-}" == "-" ]] && key=""
  [[ "${manual:-}" == "-" ]] && manual=""

  # Owner requests first: the owner is waiting, and each one expires in the engine if it isn't answered in time.
  if [[ -n "${manual:-}" ]] && has_new_signal "$manual" "$last_manual"; then
    if (( now >= manual_retry_at )); then
      if guarded_manual_run "owner requests $manual"; then
        last_manual="$manual"
        manual_retry_at=0
      else
        manual_retry_at=$(( $(date +%s) + 300 ))
      fi
      now=$(date +%s)
    fi
  elif [[ -n "${driven:-}" ]]; then
    last_manual="${manual:-}"   # only from a real answer: an engine that can't be asked says nothing about requests
  fi

  if [[ "${driven:-}" == "true" ]]; then
    next_run=$(( now + interval * 60 ))
    if [[ "${scanned:-}" != "yes" ]]; then
      sleep "$poll"; continue   # the engine hasn't scanned yet
    fi
    (( next_heartbeat == 0 )) && next_heartbeat=$now
    if [[ -n "${key:-}" ]] && has_new_signal "$key" "$last_key"; then
      # Something new (not just one of the previous signals going away). Within the minimum gap, wait: it is still new
      # at the next poll after the gap. A failed run leaves it unhandled, so it is retried after the gap.
      if (( now - last_run >= min_gap * 60 )) && guarded_run "signals $key"; then
        last_key="$key"
        next_heartbeat=$(( $(date +%s) + heartbeat * 60 ))
      fi
    else
      last_key="${key:-}"   # only removals: remember the smaller set, so a removed signal coming back counts as new
      if (( now >= next_heartbeat )); then
        guarded_run "heartbeat" || true
        next_heartbeat=$(( $(date +%s) + heartbeat * 60 ))
      fi
    fi
  elif [[ "${driven:-}" == "false" ]]; then
    if (( now >= next_run )); then
      # Interval mode: the strategy doesn't use the scanner. Fixed cadence, no catch-up bursts.
      guarded_run "scheduled" || true
      next_run=$(( next_run + interval * 60 ))
      (( next_run <= $(date +%s) )) && next_run=$(( $(date +%s) + interval * 60 ))
    fi
  fi
  # No answer from /signals (engine restarting): neither mode runs; wait_for_engine handles long outages.

  sleep "$poll"
done
