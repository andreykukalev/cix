#!/usr/bin/env bash
# What a live deploy would change (ADR-0019): run by CI's live-changes job before deploy-live waits for the owner's approval,
# so the run's summary page, where the owner approves, lists the files and commits since the commit live got last from CI,
# with the ones that could touch real money flagged first. Live's commit is that of its latest GitHub deployment to the
# environment whose job reached the step "Live runs this commit" (deploy-live runs it only once live runs those images);
# when none can be found it says so. It reads only and can't stop the deploy. Its own code comes with the commit it
# describes, so a change to tools/ci/ or .github/workflows/ is flagged: read that diff yourself.
#
# Environment: GH_TOKEN (the job's token, with deployments, contents and actions: read), GITHUB_REPOSITORY, GITHUB_SHA,
# LIVE_ENVIRONMENT (prod), and optionally GITHUB_STEP_SUMMARY (where the summary goes; otherwise standard output).
set -euo pipefail

[[ "${GITHUB_REPOSITORY:-}" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || { echo "::error::GITHUB_REPOSITORY must be owner/repo."; exit 1; }
[[ "${GITHUB_SHA:-}" =~ ^[0-9a-f]{40}$ ]] || { echo "::error::GITHUB_SHA must be a commit SHA."; exit 1; }
[[ "${LIVE_ENVIRONMENT:-}" =~ ^[a-z0-9-]{2,12}$ ]] || { echo "::error::LIVE_ENVIRONMENT must name the GitHub environment."; exit 1; }
out="${GITHUB_STEP_SUMMARY:-/dev/stdout}"

# Paths whose changes could reach real money: all the code, the limits, strategies, the agents' prompts, tools and jobs,
# and what builds and deploys them. Docs, the dashboard and tests aren't flagged.
risky='^(src/|config/|strategies/|tools/(trader|ci)/|\.claude/|\.github/|docker/|infra/|Directory\.|global\.json|NuGet\.config|Cix\.slnx|docker-compose)'

base=""
if ids="$(gh api "repos/$GITHUB_REPOSITORY/deployments?environment=$LIVE_ENVIRONMENT&per_page=30" --jq '.[] | [.id, .sha] | @tsv' 2>/dev/null)"; then
  while IFS=$'\t' read -r id sha; do
    [[ "$id" =~ ^[0-9]+$ && "$sha" =~ ^[0-9a-f]{40}$ && "$sha" != "$GITHUB_SHA" ]] || continue
    # Its job, from any of its statuses' links; then whether that job marked live as running its commit.
    links="$(gh api "repos/$GITHUB_REPOSITORY/deployments/$id/statuses?per_page=100" --jq '.[] | (.log_url // ""), (.target_url // "")' </dev/null 2>/dev/null)" || continue
    [[ "$links" =~ /job/([0-9]+) ]] || continue
    mark="$(gh api "repos/$GITHUB_REPOSITORY/actions/jobs/${BASH_REMATCH[1]}" --jq '[.steps[]? | select(.name == "Live runs this commit") | .conclusion] | .[0] // ""' </dev/null 2>/dev/null)" || continue
    if [[ "$mark" == success ]]; then base="$sha"; break; fi
  done <<<"$ids"
fi

# Commit subjects are text anyone with a commit wrote: shown as code, never as Markdown or HTML.
plain() { sed 's/`/'"'"'/g; s/[<>&]/_/g'; }

{
  echo "## What this live deploy changes"
  echo
  if [[ -z "$base" ]]; then
    echo "**Couldn't tell which commit live runs** (no earlier deploy by CI found). Check every commit since the one you last promoted or approved, up to ${GITHUB_SHA:0:7}, before approving."
    exit 0
  fi
  if ! compare="$(gh api "repos/$GITHUB_REPOSITORY/compare/$base...$GITHUB_SHA" 2>/dev/null)"; then
    echo "**Couldn't compare with ${base:0:7}**, the commit live runs: check the commits since then before approving."
    exit 0
  fi
  status="$(jq -r .status <<<"$compare")"
  if [[ "$status" != ahead ]]; then
    echo "**This commit isn't simply ahead of ${base:0:7}**, the commit live runs (status $status): check before approving."
    echo
  fi
  mapfile -t files < <(jq -r '.files[]?.filename' <<<"$compare")
  flagged=()
  for f in "${files[@]}"; do [[ "$f" =~ $risky ]] && flagged+=("$f"); done
  if (( ${#flagged[@]} > 0 )); then
    echo "**${#flagged[@]} changed files could reach real money** (code, limits, strategies, agents, build or deploy). Read them before approving:"
    echo
    for f in "${flagged[@]}"; do echo "- \`$(plain <<<"$f")\`"; done
  else
    echo "No change to code, limits, strategies, agents, build or deploy."
  fi
  if (( ${#files[@]} >= 300 )); then echo; echo "GitHub lists 300 files at most: there may be more."; fi
  echo
  echo "Since ${base:0:7}, the commit live runs: $(jq -r '.total_commits' <<<"$compare") commits."
  echo
  jq -r '.commits[] | .sha[0:7] + " " + (.commit.message | split("\n")[0])' <<<"$compare" | plain | sed 's/^/    /'
} >> "$out"
