#!/usr/bin/env bash
# Decides which CI jobs a run needs, to save GitHub Actions minutes (#73). Writes `name=true|false` lines to
# $GITHUB_OUTPUT (or stdout) for: dotnet, containers, azure, windows, secrets, dashboard.
#
#   pull_request       jobs for the files the pull request changes (against its base)
#   push to main       nothing again when the commit is exactly what a pull request already tested green: the pull
#                      request contained the previous tip of main and the trees are identical. Otherwise the files the
#                      push changed. The dashboard is still published when its files changed.
#   workflow_dispatch  everything
#
# A change to the workflow, this script, or anything it can't classify runs everything: when in doubt, test.
# Environment: EVENT, BASE_SHA, HEAD_SHA (pull requests), BEFORE_SHA, SHA, REPO, GH_TOKEN (pushes).
set -euo pipefail

out="${GITHUB_OUTPUT:-/dev/stdout}"
emit() { for k in dotnet containers azure windows secrets dashboard; do echo "$k=${!k}" >> "$out"; done; }
all() { dotnet=true containers=true azure=true windows=true secrets=true dashboard=true; }
dotnet=false containers=false azure=false windows=false secrets=true dashboard=false
ci_changed=false

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

mapfile -t files < <(git diff --name-only "$range")
echo "Changed files (${#files[@]}):"; printf '  %s\n' "${files[@]}"

for f in "${files[@]}"; do
  case "$f" in
    .github/workflows/*|tools/ci/*) all; ci_changed=true; break ;;
    src/*|Cix.slnx|Directory.Build.props|global.json|NuGet.config|config/*|strategies/*)
      dotnet=true; containers=true ;;
    tests/azure/*|tools/azure/*) azure=true; windows=true ;;
    tests/*) dotnet=true ;;
    docker/*|docker-compose*.yml|.dockerignore|tools/trader/*|.claude/agents/*|.claude/hooks/*|.claude/settings.json) containers=true ;;
    infra/*) azure=true; windows=true; dashboard=true ;;
    dashboard/*) azure=true; dashboard=true ;;
    docs/*|*.md|.claude/skills/*|.gitleaks.toml|.gitignore|.gitattributes|.editorconfig|LICENSE) ;;
    *) echo "::notice::Unclassified change ($f): running everything."; all; break ;;
  esac
done

# A merge to main that a green pull request already tested, byte for byte: don't test it again. Not when CI itself
# changed: the pull request ran its own version of the checks, which main hasn't run yet.
if [[ "$EVENT" == "push" && "$ci_changed" == false ]]; then
  pr_head=$(gh api "repos/$REPO/commits/$SHA/pulls" --jq "[.[] | select(.merged_at != null and .merge_commit_sha == \"$SHA\")][0].head.sha // empty" 2>/dev/null || true)
  if [[ -n "$pr_head" ]] && git cat-file -e "$pr_head^{commit}" 2>/dev/null \
     && git merge-base --is-ancestor "$BEFORE_SHA" "$pr_head" \
     && [[ "$(git rev-parse "$SHA^{tree}")" == "$(git rev-parse "$pr_head^{tree}")" ]]; then
    # Every CI job on the pull request's last commit passed or was skipped, and the plan itself ran.
    verdict=$(gh api "repos/$REPO/commits/$pr_head/check-runs?per_page=100" --jq '
      [.check_runs[] | select(.app.slug == "github-actions")] as $runs
      | if ($runs | map(select(.name == "plan" and .conclusion == "success")) | length) == 0 then "untested"
        elif ($runs | map(select(.status != "completed" or (.conclusion | IN("success", "skipped", "neutral") | not))) | length) > 0 then "failed"
        else "green" end' 2>/dev/null || echo unknown)
    if [[ "$verdict" == "green" ]]; then
      echo "::notice::Already tested green in the pull request (head ${pr_head:0:7}); only publishing what changed."
      dotnet=false containers=false azure=false windows=false secrets=false
    else
      echo "Pull request ${pr_head:0:7}: $verdict, so testing again."
    fi
  fi
fi

emit
