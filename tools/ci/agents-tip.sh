#!/usr/bin/env bash
# Whether this run should deploy the engine and the agents (agents-images, deploy-agents in ci.yml): writes deploy=true or
# deploy=false to $GITHUB_OUTPUT. Runs can finish out of order, and an older one must not deploy over newer images. It
# deploys when it is the tip of main, or when the tip is strictly ahead of it and changed nothing that goes into the
# images or decides how they deploy (the newer push then didn't deploy, so this run's images are the tip's). Anything else,
# or anything it can't tell, leaves deploying to the newer run. Needs GH_TOKEN, GITHUB_REPOSITORY and GITHUB_SHA.
set -euo pipefail

out="${GITHUB_OUTPUT:-/dev/stdout}"
# What goes into the images or into deploying them (tools/ci/plan.sh sets images for the first lines of this list).
relevant='^(src/|config/|strategies/|docker/|tools/trader/|\.claude/|Cix\.slnx$|Directory\.Build\.props$|global\.json$|NuGet\.config$|\.editorconfig$|\.dockerignore$|infra/|tools/ci/|\.github/workflows/)'

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
