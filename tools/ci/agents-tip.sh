#!/usr/bin/env bash
# Whether this run deploys the engine and the agents (agents-images, deploy-agents in ci.yml): writes deploy=true or
# deploy=false to $GITHUB_OUTPUT.
#   - Not while the environment has nothing in Azure (agents.deploy none in SETTINGS).
#   - Runs can finish out of order, and an older one must not deploy over newer images. It deploys when it is the tip of
#     main, or when the tip is strictly ahead of it and changed nothing that tools/ci/plan.sh deploys images for (so the
#     newer push didn't deploy, and this run's images are the tip's). Anything else, or anything it can't tell, leaves
#     deploying to the newer run.
# Needs SETTINGS, GH_TOKEN, GITHUB_REPOSITORY and GITHUB_SHA.
set -euo pipefail

out="${GITHUB_OUTPUT:-/dev/stdout}"
# The files plan.sh sets `images` for, and CI itself (which makes it run everything). test-deploy-agents.py checks that
# the two agree.
relevant='^(src/|Cix\.slnx$|Directory\.Build\.props$|global\.json$|NuGet\.config$|\.editorconfig$|config/|strategies/|docker/|docker-compose[^/]*\.yml$|\.dockerignore$|tools/trader/|\.claude/agents/|\.claude/hooks/|\.claude/settings\.json$|\.github/workflows/|tools/ci/)'

if [[ ! -f "${SETTINGS:-}" ]]; then
  echo "::error::No environment file ${SETTINGS:-} (AGENTS_ENVIRONMENT names it)."
  exit 1
fi
deploy="$(jq -r '.agents.deploy // "none"' "$SETTINGS")"
if [[ "$deploy" != platform && "$deploy" != apps ]]; then
  echo "deploy=false" >> "$out"
  echo "::notice::agents.deploy is '$deploy' in $SETTINGS: nothing to deploy to."
  exit 0
fi
if ! tip=$(gh api "repos/$GITHUB_REPOSITORY/commits/main" --jq .sha) || [ -z "$tip" ]; then
  echo "::error::Couldn't read the tip of main, so nothing is deployed."
  exit 1
fi
if [ "$tip" = "$GITHUB_SHA" ]; then
  echo "deploy=true" >> "$out"
elif ! changed=$(gh api "repos/$GITHUB_REPOSITORY/compare/$GITHUB_SHA...$tip" | jq -r --arg re "$relevant" '
       if .status == "ahead" and (.files | length) < 300
       then [.files[].filename | select(test($re))] | length
       else -1 end'); then
  echo "deploy=false" >> "$out"
  echo "::warning::Couldn't compare this commit with the tip of main ($tip); not deploying. The newer run deploys."
elif [ "$changed" = -1 ]; then
  echo "deploy=false" >> "$out"
  echo "::notice::main ($tip) isn't simply ahead of this commit, or changed too many files to list; the newer run deploys."
elif [ "$changed" = 0 ]; then
  echo "deploy=true" >> "$out"
  echo "::notice::main has moved on, but its images are this commit's; deploying."
else
  echo "deploy=false" >> "$out"
  echo "::notice::main has moved on with changes to the images ($tip); the newer run deploys them."
fi
