#!/usr/bin/env bash
# The stocks Claude analyst (ADR-0027), the analyst job's only container. The nightly job wrote tonight's brief to the
# advice container (brief-latest); this job reads it with its own identity and never has the EODHD key. The host writes
# the brief to /work/brief.json (--brief) only when it's for the latest advice and this pass hasn't analysed it yet;
# otherwise there is nothing to do. The host picks the pass by the clock: the night's, or on a weekday from 08:35 New York
# time to the 09:30 open the check before the open, whose brief carries the night's view and which looks only for news
# since then. One
# headless Claude Code session reads the brief on
# stdin, may search the web and open pages from ANALYST_WEB_DOMAINS only (no shell, files or MCP servers), and answers
# with one JSON object. The host checks that answer against the brief and saves it (--accept), or records why Claude
# didn't run (--not-run): usage-limit, credentials, timeout or failed. Its view can only advise less risk: the amounts and
# stops stay the rule advisor's.
#
# Settings (environment): ANALYST_MODEL (opus), ANALYST_MAX_TURNS (40), ANALYST_TIMEOUT_MINUTES (15), ANALYST_WEB_DOMAINS,
# CLAUDE_TOKEN_FILE (/run/secrets/claude_token), CLAUDE_TOKEN_EXPIRES, STOCKS_WORK_DIR (/work).
set -uo pipefail

app="${STOCKS_APP_DIR:-/app}"
work="${STOCKS_WORK_DIR:-/work}"
brief="$work/brief.json"
default_domains="sec.gov,nasdaq.com,nyse.com,reuters.com,apnews.com,cnbc.com,marketwatch.com,finance.yahoo.com,businesswire.com,prnewswire.com,globenewswire.com,investing.com,federalreserve.gov,bls.gov"
domains="${ANALYST_WEB_DOMAINS:-$default_domains}"
model="${ANALYST_MODEL:-opus}"
max_turns="${ANALYST_MAX_TURNS:-40}"
timeout_minutes="${ANALYST_TIMEOUT_MINUTES:-15}"
token_file="${CLAUDE_TOKEN_FILE:-/run/secrets/claude_token}"
expires="${CLAUDE_TOKEN_EXPIRES:-}"
# The host never sees the Claude token: only the claude process below gets it.
host=(env -u CLAUDE_CODE_OAUTH_TOKEN -u ANTHROPIC_API_KEY dotnet "$app/Cix.Stocks.Host.dll")

agent_name="stocks-analyst"
export CIX_JOB_MODE=1 # failure_details prints the run's stderr, masked, to the job's log
# shellcheck source=tools/trader/agent-common.sh
source "$app/analyst/agent-common.sh"

mkdir -p "$work"
"${host[@]}" --brief "$brief"
code=$?
if (( code == 3 )); then say "No brief to analyse now (none yet, this pass already analysed it, or the owner stopped stocks)."; exit 0; fi
(( code == 0 )) || { say "Couldn't read the brief (exit $code)."; exit "$code"; }
[[ -s "$brief" ]] || { say "The host wrote an empty brief."; exit 1; }
pass="$(jq -r '.pass // "night"' "$brief" 2>/dev/null)"
case "$pass" in
  pre-open) prompt="This is the check before the US open: the brief, with your view from the night in its night field, is on standard input. Look for news since the night as your instructions say and answer with the JSON object only." ;;
  *) pass=night; prompt="Tonight's brief is on standard input. Analyse it as your instructions say and answer with the JSON object only." ;;
esac

# Records that Claude didn't run for the brief's close, and stops with the host's exit code (1: not a done analysis).
not_run() {
  say "Claude didn't run: $1."
  "${host[@]}" --not-run "$brief" "$1"
  exit $?
}

[[ "$model" =~ ^[a-z0-9][a-z0-9.-]{1,63}(\[1m\])?$ ]] || { say "ANALYST_MODEL must be a lowercase model alias or name, like opus or sonnet."; not_run failed; }
for n in "$max_turns" "$timeout_minutes"; do
  [[ "$n" =~ ^[1-9][0-9]{0,2}$ ]] || { say "ANALYST_MAX_TURNS and ANALYST_TIMEOUT_MINUTES must be whole numbers from 1 to 999."; not_run failed; }
done

# WebFetch only for listed sites: one permission rule per domain and its subdomains.
fetch_rules=""
IFS=',' read -ra domain_list <<<"$domains"
for d in "${domain_list[@]}"; do
  d="${d// /}"
  [[ -z "$d" ]] && continue
  [[ "$d" =~ ^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$ ]] || { say "ANALYST_WEB_DOMAINS: '$d' isn't a plain domain like reuters.com."; not_run failed; }
  fetch_rules+=",WebFetch(domain:$d),WebFetch(domain:*.$d)"
done
[[ -n "$fetch_rules" ]] || { say "ANALYST_WEB_DOMAINS is empty; the analyst needs at least one site to read."; not_run failed; }
tools="WebSearch${fetch_rules}"
denied="Bash,Read,Edit,Write,MultiEdit,NotebookEdit,Glob,Grep,LS,Task,TodoWrite"
site_note="Open full pages only from these sites: ${domains//,/, }. Use search results to find articles there; a fetch elsewhere may be refused."
instructions="$(cat "$app/analyst/instructions.md")"

load_credentials || not_run credentials
check_expiry || not_run credentials

logs="$work/logs"
mkdir -p "$logs" "$work/empty"
run_id="stocks-analyst-$(date -u +%Y%m%d-%H%M%S)"
log="$logs/$run_id.json"
say "$run_id starting ($pass, $auth, model $model)"

# From an empty folder, with the job's identity variables unset: Claude has no tool to use them, and gets them not at all.
(cd "$work/empty" && timeout -k 60s "${timeout_minutes}m" env -u IDENTITY_ENDPOINT -u IDENTITY_HEADER -u MSI_ENDPOINT -u MSI_SECRET -u AZURE_CLIENT_ID \
    claude -p "$prompt" \
    --append-system-prompt "$instructions"$'\n\n'"$site_note" \
    --strict-mcp-config \
    --tools "WebSearch,WebFetch" \
    --allowedTools "$tools" --disallowedTools "$denied" \
    --model "$model" \
    --max-turns "$max_turns" \
    --output-format json < "$brief") > "$log" 2> "$logs/$run_id.err"
code=$?

if (( code == 124 )); then
  say "$run_id stopped after $timeout_minutes minutes."
  not_run timeout
fi
if hit_usage_limit "$log" "$logs/$run_id.err"; then
  not_run usage-limit
fi
# Credentials Claude refused (expired, revoked or mistyped).
auth_pattern="failed to authenticate|invalid api key|invalid bearer|authentication_error|oauth token (has )?expired|token has expired|please run /login|not logged in|unauthorized|status:? ?401|api error: ?401"
if ! jq -e . "$log" >/dev/null 2>&1; then
  if grep -qiE "$auth_pattern" "$logs/$run_id.err" 2>/dev/null; then say "$(renew_hint)"; not_run credentials; fi
  say "$run_id failed (exit $code)."
  failure_details "$logs" "$run_id"
  not_run failed
fi
if [[ "$(jq -r '.is_error // false' "$log")" == "true" ]]; then
  if jq -r '.result // ""' "$log" | grep -qiE "$auth_pattern"; then say "$(renew_hint)"; not_run credentials; fi
  say "$run_id ended with an error ($(jq -r '.subtype // "?"' "$log"))."
  failure_details "$logs" "$run_id"
  not_run failed
fi

# A run that didn't finish its answer (max turns, for one) isn't an error to the CLI, but there's no answer to check.
if [[ "$(jq -r '.subtype // ""' "$log")" != "success" ]]; then
  say "$run_id ended without an answer ($(jq -r '.subtype // "?"' "$log"))."
  failure_details "$logs" "$run_id"
  not_run failed
fi

say "$run_id done. Models used: $(jq -r '(.modelUsage // {}) | keys | join(", ")' "$log"), turns: $(jq -r '.num_turns // "?"' "$log")"
# Claude's final text alone, never the CLI's own wrapper, goes to the host's check.
jq -r '.result // ""' "$log" > "$work/answer.txt"
"${host[@]}" --accept "$brief" "$work/answer.txt"
