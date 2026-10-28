#!/usr/bin/env bash
# What a live deploy would change (ADR-0019): run by CI's live-changes job before deploy-live waits for the owner's approval,
# so the run's summary page, where the owner approves, lists the commits and files since the commit live last got from CI
# (its latest successful GitHub deployment to the environment whose job really deployed: one left to a newer commit by
# agents-tip.sh succeeds without deploying), with the ones that touch real money's rules flagged. It reads only; it can't
# stop the deploy, and a lookup that fails only says so.
#
# Environment: GH_TOKEN (the job's token, with deployments, contents and actions: read), GITHUB_REPOSITORY, GITHUB_SHA,
# LIVE_ENVIRONMENT (prod), and optionally GITHUB_STEP_SUMMARY (where the summary goes; otherwise standard output).
set -euo pipefail

[[ "${GITHUB_REPOSITORY:-}" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || { echo "::error::GITHUB_REPOSITORY must be owner/repo."; exit 1; }
[[ "${GITHUB_SHA:-}" =~ ^[0-9a-f]{40}$ ]] || { echo "::error::GITHUB_SHA must be a commit SHA."; exit 1; }
[[ "${LIVE_ENVIRONMENT:-}" =~ ^[a-z0-9-]{2,12}$ ]] || { echo "::error::LIVE_ENVIRONMENT must name the GitHub environment."; exit 1; }
out="${GITHUB_STEP_SUMMARY:-/dev/stdout}"

# Paths whose changes reach real money's rules: the risk gate and ceilings, the limits, strategies, order execution and the
# exchange client, the agents' tools and prompts, and what deploys them.
risky='^(src/Cix\.Domain/Risk/|config/risk-limits|strategies/|src/Cix\.Engine/(Execution|Tools|Strategy|Pairs)/|src/Cix\.Exchange\.RevolutX/|\.claude/|\.github/workflows/|tools/ci/|docker/|infra/)'

base=""
if ids="$(gh api "repos/$GITHUB_REPOSITORY/deployments?environment=$LIVE_ENVIRONMENT&per_page=20" --jq '.[] | [.id, .sha] | @tsv' 2>/dev/null)"; then
  while IFS=$'\t' read -r id sha; do
    [[ "$id" =~ ^[0-9]+$ && "$sha" =~ ^[0-9a-f]{40}$ && "$sha" != "$GITHUB_SHA" ]] || continue
    status="$(gh api "repos/$GITHUB_REPOSITORY/deployments/$id/statuses?per_page=1" --jq '.[0] | [.state, (.log_url // .target_url // "")] | @tsv' </dev/null 2>/dev/null)" || continue
    [[ "${status%%$'\t'*}" == success ]] || continue
    # The job's deploy step ran (not skipped as superseded); a job it can't find is taken as having deployed.
    if [[ "$status" =~ /job/([0-9]+) ]]; then
      step="$(gh api "repos/$GITHUB_REPOSITORY/actions/jobs/${BASH_REMATCH[1]}" --jq '[.steps[]? | select(.name == "Deploy the live engine and its agents") | .conclusion] | .[0] // ""' </dev/null 2>/dev/null)" || step=""
      [[ "$step" == skipped ]] && continue
    fi
    base="$sha"; break
  done <<<"$ids"
fi

{
  echo "## What this live deploy changes"
  echo
  if [[ -z "$base" ]]; then
    echo "No earlier live deploy from CI to compare with: live runs what you promoted with provision.ps1. Check this commit, ${GITHUB_SHA:0:7}, before approving."
    exit 0
  fi
  if ! compare="$(gh api "repos/$GITHUB_REPOSITORY/compare/$base...$GITHUB_SHA" 2>/dev/null)"; then
    echo "Couldn't compare with ${base:0:7}, the commit live got last: check the commits since then before approving."
    exit 0
  fi
  status="$(jq -r .status <<<"$compare")"
  if [[ "$status" != ahead ]]; then
    echo "This commit isn't simply ahead of ${base:0:7}, the commit live got last (status $status): check before approving."
  fi
  echo "Since ${base:0:7}, the commit live got last: $(jq -r '.total_commits' <<<"$compare") commits."
  echo
  jq -r '.commits[] | "- " + .sha[0:7] + " " + (.commit.message | split("\n")[0])' <<<"$compare"
  echo
  mapfile -t files < <(jq -r '.files[]?.filename' <<<"$compare")
  flagged=()
  for f in "${files[@]}"; do [[ "$f" =~ $risky ]] && flagged+=("$f"); done
  if (( ${#flagged[@]} > 0 )); then
    echo "**Changes real money's rules (risk, limits, strategies, orders, tools, deploy): ${#flagged[@]} files.** Read them before approving:"
    echo
    for f in "${flagged[@]}"; do echo "- \`$f\`"; done
  else
    echo "No change to risk, limits, strategies, orders, tools or the deploy."
  fi
  if (( ${#files[@]} >= 300 )); then echo; echo "GitHub lists 300 files at most: there may be more."; fi
} >> "$out"
