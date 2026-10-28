#!/usr/bin/env bash
# Whether the owner approved this workflow run for the live environment (ADR-0019): run by CI's deploy-live job before it
# signs in to Azure. The control is the GitHub environment itself (required reviewer: the owner; deployment branches: main),
# which holds the job until the owner approves it, and the live deploy identity trusts only jobs in that environment. This
# check fails closed if that environment was created without its rules (GitHub creates a missing one bare): the run must
# be on main, and its review history must hold the owner's approval for the environment.
#
# Environment: GH_TOKEN (the job's token, with actions: read), GITHUB_REPOSITORY, GITHUB_REPOSITORY_OWNER, GITHUB_RUN_ID,
# GITHUB_REF, and LIVE_ENVIRONMENT (the GitHub environment's name, prod).
set -euo pipefail

fail() { echo "::error::$*"; exit 1; }

[[ "${GITHUB_REF:-}" == refs/heads/main ]] || fail "Live deploys only from main, not ${GITHUB_REF:-an unknown ref}."
[[ "${GITHUB_RUN_ID:-}" =~ ^[0-9]{1,20}$ ]] || fail "GITHUB_RUN_ID must be the run's ID."
[[ "${GITHUB_REPOSITORY:-}" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || fail "GITHUB_REPOSITORY must be owner/repo."
[[ "${GITHUB_REPOSITORY_OWNER:-}" =~ ^[A-Za-z0-9-]+$ ]] || fail "GITHUB_REPOSITORY_OWNER must be the owner's login."
[[ "${LIVE_ENVIRONMENT:-}" =~ ^[a-z0-9-]{2,12}$ ]] || fail "LIVE_ENVIRONMENT must name the GitHub environment."

if ! approvals="$(gh api "repos/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID/approvals")"; then
  fail "Couldn't read this run's approvals; nothing was deployed."
fi
# Only the owner's own approval counts, for this environment, and none rejected since.
verdict="$(jq -r --arg env "$LIVE_ENVIRONMENT" --arg owner "$GITHUB_REPOSITORY_OWNER" '
  if type != "array" then "unreadable"
  else [.[] | select(any(.environments[]?; .name == $env))] as $mine
    | if any($mine[]; .state == "rejected") then "rejected"
      elif any($mine[]; .state == "approved" and ((.user.login // "") | ascii_downcase) == ($owner | ascii_downcase)) then "approved"
      else "none" end
  end' <<<"$approvals")" || verdict=unreadable

case "$verdict" in
  approved) echo "The owner ($GITHUB_REPOSITORY_OWNER) approved this run for $LIVE_ENVIRONMENT." ;;
  rejected) fail "This run was rejected for $LIVE_ENVIRONMENT; nothing was deployed." ;;
  none) fail "This run has no approval by $GITHUB_REPOSITORY_OWNER for $LIVE_ENVIRONMENT. Give the GitHub environment $LIVE_ENVIRONMENT a required reviewer (you) and allow only main (docs/live-trading.md, Deploys from main); nothing was deployed." ;;
  *) fail "Couldn't read this run's approvals; nothing was deployed." ;;
esac
