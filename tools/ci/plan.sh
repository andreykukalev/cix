#!/usr/bin/env bash
# Decides which CI jobs a run needs, to save GitHub Actions minutes (#73). Writes `name=true|false` lines to
# $GITHUB_OUTPUT (or stdout) for: dotnet, containers, azure, windows, secrets, dashboard.
#
#   pull_request       the jobs for the files the pull request changes (against its base)
#   push to main       the jobs for the files the push changes, minus those the merged pull request already passed on
#                      exactly this content: its CI run (this workflow) on a head that contained the previous tip of main,
#                      with an identical tree, where each of those jobs succeeded (skipped doesn't count). Only when the
#                      pull request's base was main all along: a run against another base tested a different merge.
#                      `dashboard` (publish) follows the files either way.
#   workflow_dispatch  everything
#
# When in doubt, test: a change to CI itself, a file it can't classify, or a diff it can't compute runs everything.
# Environment: EVENT, BASE_SHA, HEAD_SHA (pull requests), BEFORE_SHA, SHA, REPO, GH_TOKEN (pushes).
set -euo pipefail

out="${GITHUB_OUTPUT:-/dev/stdout}"
jobs_of=(dotnet containers azure windows secrets)
emit() { for k in "${jobs_of[@]}" dashboard; do echo "$k=${!k}" >> "$out"; done; summary; }

# The commit this run tests, and the jobs it runs, on the run's summary page (the run list shows only its name).
summary() {
  [[ -n "${GITHUB_STEP_SUMMARY:-}" ]] || return 0
  local tested="${HEAD_SHA:-${SHA:-}}" line k
  {
    echo "### Commit tested"
    if [[ -n "$tested" ]] && line=$(git log -1 --format='`%h` %s (%an, %ad)' --date=format:'%Y-%m-%d %H:%M' "$tested" 2>/dev/null); then
      echo "$line"
    else
      echo "\`${tested:-unknown}\`"
    fi
    echo
    echo "### Jobs"
    for k in "${jobs_of[@]}" dashboard; do echo "- $k: ${!k}"; done
  } >> "$GITHUB_STEP_SUMMARY"
}
all() { dotnet=true containers=true azure=true windows=true secrets=true dashboard=true; }
dotnet=false containers=false azure=false windows=false secrets=true dashboard=false
ci_changed=false unknown=false

zero=0000000000000000000000000000000000000000
case "${EVENT:-}" in
  pull_request) range="$BASE_SHA...$HEAD_SHA" ;;
  push)
    if [[ -z "${BEFORE_SHA:-}" || "$BEFORE_SHA" == "$zero" ]] || ! git cat-file -e "$BEFORE_SHA^{commit}" 2>/dev/null; then
      echo "::notice::No previous commit to compare with: running everything."; all; emit; exit 0
    fi
    range="$BEFORE_SHA..$SHA"
    ;;
  *) all; emit; exit 0 ;;
esac

# Both sides of a rename count (moving a file out of src/ is a src/ change). A diff that fails runs everything.
if ! list=$(git diff --no-renames --name-only "$range"); then
  echo "::warning::Couldn't compute the changed files: running everything."; all; emit; exit 0
fi
files=()
[[ -n "$list" ]] && mapfile -t files <<<"$list"
echo "Changed files (${#files[@]}):"; printf '  %s\n' "${files[@]}"

for f in "${files[@]}"; do
  case "$f" in
    .github/workflows/*|tools/ci/*) ci_changed=true ;;
    src/*|Cix.slnx|Directory.Build.props|global.json|NuGet.config|.editorconfig|config/*|strategies/*)
      dotnet=true; containers=true ;;
    tests/azure/*|tools/azure/*) azure=true; windows=true ;;
    tests/*) dotnet=true ;;
    docker/*|docker-compose*.yml|.dockerignore|tools/trader/*|.claude/agents/*|.claude/hooks/*|.claude/settings.json) containers=true ;;
    infra/*) azure=true; windows=true; dashboard=true ;;
    dashboard/*) azure=true; dashboard=true ;;
    docs/*|*.md|.claude/skills/*|.gitleaks.toml|.gitignore|.gitattributes|LICENSE) ;;
    *) echo "::notice::Unclassified change ($f): running everything."; unknown=true ;;
  esac
done
if [[ "$ci_changed" == true || "$unknown" == true ]]; then all; fi

# A merge to main whose content a pull request already tested: skip the jobs that passed there. Never when CI itself
# changed (the pull request ran its own version of the checks, which main hasn't run yet).
if [[ "$EVENT" == "push" && "$ci_changed" == false ]]; then
  pr=$(gh api "repos/$REPO/commits/$SHA/pulls" \
    --jq "[.[] | select(.merged_at != null and .merge_commit_sha == \"$SHA\" and .base.ref == \"main\")][0] // empty | \"\(.number) \(.head.ref) \(.head.sha)\"" \
    2>/dev/null || true)
  read -r number pr_branch pr_head <<<"${pr:-}" || true
  # gh prints an HTTP error's body on stdout: anything that isn't a pull request number and a commit is no pull request.
  [[ "${number:-}" =~ ^[0-9]+$ && "${pr_head:-}" =~ ^[0-9a-f]{40}$ ]] || pr=""
  if [[ -n "$pr" ]]; then
    git cat-file -e "$pr_head^{commit}" 2>/dev/null || git -c "http.extraheader=AUTHORIZATION: bearer $GH_TOKEN" fetch -q --no-tags origin "refs/pull/$number/head" 2>/dev/null || true
  fi
  # A base changed at any time (for example from a stacked branch to main) means a run may have tested another merge, with
  # another workflow file: reuse nothing. With no change the base was main, which only moves forward, so the run's merge
  # base is an ancestor of BEFORE and the tested tree is pr_head's. A failed lookup reuses nothing.
  base_changed=yes
  if [[ -n "$pr" ]] && events=$(gh api --paginate "repos/$REPO/issues/$number/timeline?per_page=100" \
       --jq '.[] | select(.event == "base_ref_changed") | .event' 2>/dev/null); then
    [[ -z "$events" ]] && base_changed=no
  fi
  [[ -n "$pr" && "$base_changed" == yes ]] && echo "Pull request #$number changed its base or its history couldn't be read: testing here."
  if [[ -n "$pr" && "$base_changed" == no ]] && git cat-file -e "$pr_head^{commit}" 2>/dev/null \
     && git merge-base --is-ancestor "$BEFORE_SHA" "$pr_head" \
     && [[ "$(git rev-parse "$SHA^{tree}")" == "$(git rev-parse "$pr_head^{tree}")" ]]; then
    # The latest completed pull-request run of this workflow on that head. Only jobs that succeeded there count: one
    # that was skipped (for example under another base) tested nothing.
    run=$(gh api "repos/$REPO/actions/runs?head_sha=$pr_head&event=pull_request&per_page=50" 2>/dev/null | jq -r --arg branch "$pr_branch" '
      [.workflow_runs[] | select(.path == ".github/workflows/ci.yml" and .status == "completed" and .head_branch == $branch)]
      | sort_by(.created_at) | last | .id // empty' 2>/dev/null || true)
    passed=""
    [[ -n "$run" ]] && passed=$(gh api "repos/$REPO/actions/runs/$run/jobs?per_page=100" \
      --jq '[.jobs[] | select(.conclusion == "success") | .name] | join(" ")' 2>/dev/null || true)
    declare -A job_name=([dotnet]=build-test [containers]=containers [azure]=azure-and-dashboard
      [windows]=azure-scripts-windows-powershell [secrets]=secret-scan)
    skipped=()
    for k in "${jobs_of[@]}"; do
      if [[ "${!k}" == true && " $passed " == *" ${job_name[$k]} "* ]]; then printf -v "$k" false; skipped+=("${job_name[$k]}"); fi
    done
    if [[ ${#skipped[@]} -gt 0 ]]; then
      echo "::notice::Already passed in pull request #$number (run $run, head ${pr_head:0:7}): ${skipped[*]}."
    else
      echo "Pull request #$number: no passed jobs to reuse${run:+ from run $run}; testing here."
    fi
  fi
fi

emit
