#!/usr/bin/env bash
# Run-once mode for the CIX agents as Azure Container Apps Jobs (ADR-0013), shared by run-trader.sh and run-analyst.sh
# (CIX_JOB_MODE=1). The engine decides when a run is due and puts it on this job's Storage queue; the queue starts this
# execution, which:
#   1. takes one message and deletes it (one seen 3 times or more goes to the poison queue instead: an earlier execution
#      died holding it);
#   2. claims the run at the engine (POST /runs/{id}/claim). Only a run the engine still wants, for this job's identity, is
#      run; anything else ends here without using Claude;
#   3. runs it once, with the claimed run ID on every tool call;
#   4. reports how it ended (done, failed or not-run) so the engine counts the caps, refunds and retries.
#
# Every call uses an Entra token from the job's managed identity (the platform's IDENTITY_ENDPOINT); tokens are kept in
# variables, passed to curl through a file descriptor rather than its arguments, and never printed.
#
# Settings (environment): CIX_JOB (trader, trader-requests or analyst: the role of this job's identity), CIX_ENGINE_URL
# (the engine's internal address, no path), CIX_ENGINE_AUDIENCE (its application ID URI), CIX_QUEUE_URL and CIX_POISON_QUEUE_URL
# (https://<account>.queue.core.windows.net/<queue>), AZURE_CLIENT_ID (a user-assigned identity's client ID; empty for
# system-assigned), and the Claude credential as for Compose (CLAUDE_TOKEN_FILE: a Key Vault secret mounted as a file).
# From the platform: IDENTITY_ENDPOINT, IDENTITY_HEADER, CONTAINER_APP_JOB_EXECUTION_NAME. Claude itself starts without the
# identity endpoint, so nothing it runs can get tokens of its own; it gets only this run's engine token.
# The caller defines say (agent-common.sh):
# shellcheck disable=SC2154

job_run_id=""
job_purpose=""
job_attempts=6        # engine calls: a 503 (deployment, busy) or a dropped connection is retried, 10 seconds apart
job_retry_seconds="${CIX_JOB_RETRY_SECONDS:-10}"
[[ "$job_retry_seconds" =~ ^[0-9]{1,3}$ ]] || job_retry_seconds=10

# Succeeds for an https URL, or http on loopback (tests).
job_url_ok() {
  [[ "$1" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?(/[A-Za-z0-9._-]+)*$ ]] || [[ "$1" =~ ^http://127\.0\.0\.1:[0-9]+(/[A-Za-z0-9._-]+)*$ ]]
}

# The engine's address: a host only, no path or trailing slash. Plain http only for a single-label name, which stays inside
# the Container Apps environment (the app's name), or loopback; the token never goes to an outside host unencrypted.
job_engine_url_ok() {
  [[ "$1" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ || "$1" =~ ^http://[a-z0-9-]+(:[0-9]+)?$ || "$1" =~ ^http://127\.0\.0\.1:[0-9]+$ ]]
}

job_check_settings() {
  local expected="$1"
  [[ "${CIX_JOB:-}" =~ ^($expected)$ ]] || { say "CIX_JOB must be one of: ${expected//|/, }."; return 1; }
  job_engine_url_ok "${CIX_ENGINE_URL:-}" || { say "CIX_ENGINE_URL must be the engine's address with no path, like https://cix-engine.internal.<environment domain>."; return 1; }
  job_url_ok "${CIX_QUEUE_URL:-}" || { say "CIX_QUEUE_URL must be the job's queue, like https://<account>.queue.core.windows.net/runs-trader."; return 1; }
  job_url_ok "${CIX_POISON_QUEUE_URL:-}" || { say "CIX_POISON_QUEUE_URL must be the poison queue's https URL."; return 1; }
  [[ "${CIX_ENGINE_AUDIENCE:-}" =~ ^[A-Za-z0-9:/._-]{3,200}$ ]] || { say "CIX_ENGINE_AUDIENCE must be the engine's application ID URI, like api://cix-engine."; return 1; }
  [[ -n "${IDENTITY_ENDPOINT:-}" && -n "${IDENTITY_HEADER:-}" ]] || { say "No managed identity here (IDENTITY_ENDPOINT); job mode runs only as a Container Apps Job."; return 1; }
  [[ -z "${AZURE_CLIENT_ID:-}" || "$AZURE_CLIENT_ID" =~ ^[0-9a-fA-F-]{36}$ ]] || { say "AZURE_CLIENT_ID must be the identity's client ID (a GUID)."; return 1; }
  # A run must end, and report, within the job's 20-minute replica timeout, claim and report retries included.
  (( timeout_minutes <= 15 )) || { say "The run timeout must be 15 minutes or less in job mode (the job stops a replica after 20)."; return 1; }
  return 0
}

# Prints an access token for resource $1 from the job's managed identity.
job_token() {
  local url
  url="${IDENTITY_ENDPOINT}?api-version=2019-08-01&resource=$(jq -rn --arg r "$1" '$r|@uri')"
  [[ -n "${AZURE_CLIENT_ID:-}" ]] && url+="&client_id=${AZURE_CLIENT_ID}"
  curl -fsS --max-time 15 -H @<(printf 'X-IDENTITY-HEADER: %s\n' "$IDENTITY_HEADER") "$url" 2>/dev/null | jq -er '.access_token // empty'
}

# Storage queue REST call: $1 method, $2 URL, $3 optional XML body. Prints the response body; fails on any error status.
job_storage() {
  local token
  token="$(job_token "https://storage.azure.com/")" || { say "No storage token from the managed identity."; return 1; }
  local -a body=(-H "Content-Length: 0")
  [[ -n "${3:-}" ]] && body=(-H "Content-Type: application/xml" --data-binary "$3")
  curl -fsS --max-time 20 -X "$1" \
    -H @<(printf 'Authorization: Bearer %s\n' "$token") \
    -H "x-ms-version: 2021-12-02" -H "x-ms-date: $(date -u '+%a, %d %b %Y %H:%M:%S GMT')" \
    "${body[@]}" "$2"
}

# The text of XML element $1 in stdin (the queue's answers are one short line).
job_xml() { sed -n "s:.*<$1>\([^<]*\)</$1>.*:\1:p" | head -n 1; }

# Takes one message off the queue. Returns 0 with job_run_id and job_purpose set, 1 when there is nothing to run (empty
# queue, poison, or a malformed message), 2 when the queue can't be read.
job_take() {
  local xml id receipt count text message delete_url
  xml="$(job_storage GET "$CIX_QUEUE_URL/messages?numofmessages=1&visibilitytimeout=120")" || { say "Could not read the run queue."; return 2; }
  id="$(job_xml MessageId <<<"$xml")"
  if [[ -z "$id" ]]; then
    say "No run waiting on the queue."
    return 1
  fi
  receipt="$(job_xml PopReceipt <<<"$xml")"
  count="$(job_xml DequeueCount <<<"$xml")"
  text="$(job_xml MessageText <<<"$xml")"
  delete_url="$CIX_QUEUE_URL/messages/$(jq -rn --arg v "$id" '$v|@uri')?popreceipt=$(jq -rn --arg v "$receipt" '$v|@uri')"

  if [[ "$count" =~ ^[0-9]+$ ]] && (( count >= 3 )); then
    # Earlier executions took it and died before deleting it: park it for a person rather than try a fourth time.
    if job_storage POST "$CIX_POISON_QUEUE_URL/messages" "<QueueMessage><MessageText>$text</MessageText></QueueMessage>" >/dev/null; then
      job_storage DELETE "$delete_url" >/dev/null || say "Could not delete message $id after moving it to the poison queue."
      say "Message $id was taken $count times; moved to the poison queue."
    else
      say "Message $id was taken $count times and couldn't be moved to the poison queue; it stays on the run queue."
    fi
    return 1
  fi

  # Deleted before anything else: a crash from here on can't hand the same run to another execution.
  job_storage DELETE "$delete_url" >/dev/null || say "Could not delete message $id; if it comes back, the engine refuses a second claim."
  message="$(printf '%s' "$text" | base64 -d 2>/dev/null)"
  job_run_id="$(jq -r '.runId // empty' <<<"$message" 2>/dev/null)"
  job_purpose="$(jq -r '.purpose // empty' <<<"$message" 2>/dev/null)"
  if [[ ! "$job_run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$ || "$(jq -r '.job // empty' <<<"$message" 2>/dev/null)" != "$CIX_JOB" \
        || ! "$job_purpose" =~ ^[a-z]{1,20}$ ]]; then
    say "Message $id isn't a run for this job; dropped."
    job_run_id=""
    return 1
  fi
  return 0
}

# POST to the engine: $1 path, $2 JSON body. Sets job_status (000 when the engine couldn't be reached) and job_body.
job_engine_post() {
  local token out
  job_status="000"
  job_body=""
  token="$(job_token "$CIX_ENGINE_AUDIENCE")" || { say "No engine token from the managed identity."; return 1; }
  out="$(mktemp)"
  job_status="$(curl -sS --max-time 20 -o "$out" -w '%{http_code}' -X POST \
    -H @<(printf 'Authorization: Bearer %s\n' "$token") -H "Content-Type: application/json" \
    --data-binary "$2" "$CIX_ENGINE_URL$1" 2>/dev/null)" || job_status="000"
  job_body="$(head -c 4096 "$out")"
  rm -f "$out"
  return 0
}

# job_engine_post, retried while the engine can't answer (503, 5xx, no connection).
job_engine_call() {
  local attempt
  for (( attempt = 1; attempt <= job_attempts; attempt++ )); do
    job_engine_post "$1" "$2"
    [[ "$job_status" =~ ^(000|5[0-9][0-9])$ ]] || return 0
    (( attempt < job_attempts )) && sleep "$job_retry_seconds"
  done
  return 1
}

# Returns 0 if the engine wants the run, 1 if not, 2 if it couldn't be asked.
job_claim() {
  local claimant="${CONTAINER_APP_JOB_EXECUTION_NAME:-}"
  [[ "$claimant" =~ ^[A-Za-z0-9._:-]{1,64}$ ]] || claimant=""
  # The execution's name lets this execution repeat a claim whose answer it lost.
  job_engine_call "/runs/$job_run_id/claim" "$(jq -cn --arg c "$claimant" 'if $c == "" then {} else {claimant: $c} end')" \
    || { say "$job_run_id: the engine couldn't be asked (HTTP $job_status); not run."; return 2; }
  if [[ "$job_status" != 200 ]]; then
    say "$job_run_id: the engine refused the claim (HTTP $job_status); not run."
    return 2
  fi
  [[ "$(jq -r '.wanted // false' <<<"$job_body" 2>/dev/null)" == "true" ]] && return 0
  say "$job_run_id is no longer wanted (already claimed, given up or not this job's); nothing to do."
  return 1
}

# Reports outcome $1 (done, failed, not-run) for the claimed run.
job_report() {
  job_engine_call "/runs/$job_run_id/outcome" "{\"outcome\":\"$1\"}" || { say "$job_run_id: couldn't report '$1' (HTTP $job_status); the engine gives up on it after its timeout."; return 1; }
  case "$job_status" in
    204) say "$job_run_id reported: $1." ;;
    404) say "$job_run_id: the engine had already given up on this run; '$1' not recorded." ;;
    *) say "$job_run_id: reporting '$1' got HTTP $job_status."; return 1 ;;
  esac
  return 0
}

# The whole execution: $1 the allowed CIX_JOB values (a|b), $2 the function that runs the claimed run (given the purpose)
# and returns 0 (done), 1 (failed) or NOT_RUN. Exits 0 when nothing needed doing or the run was done and reported.
job_main() {
  job_check_settings "$1" || return 1
  job_take
  case $? in 0) ;; 1) return 0 ;; *) return 1 ;; esac
  job_claim
  case $? in 0) ;; 1) return 0 ;; *) return 1 ;; esac

  # For the MCP connection (mcp-azure.json): this run's tool calls carry the engine token and the claimed run ID.
  CIX_ENGINE_TOKEN="$(job_token "$CIX_ENGINE_AUDIENCE")" || { say "No engine token for the run."; job_report not-run; return 1; }
  export CIX_ENGINE_TOKEN
  local rc=0 outcome
  "$2" "$job_purpose" || rc=$?
  unset CIX_ENGINE_TOKEN
  case "$rc" in
    0) outcome="done" ;;
    "$NOT_RUN") outcome=not-run ;;
    *) outcome=failed ;;
  esac
  job_report "$outcome" || return 1
  [[ "$outcome" != failed ]]
}
