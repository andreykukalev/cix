#!/usr/bin/env bash
# PreToolUse hook (Bash, GitHub tools): agents may never approve or reconfigure the live deploy (ADR-0019). Its control is
# the owner's approval of each run in the GitHub environment prod, and agents use the owner's GitHub account: approving a
# pending deployment, changing or deleting an environment, the repository's OIDC subject or the LIVE_* variables, or
# touching the live deploy identity's credentials would let an agent deploy to real money without the owner. This is a
# tripwire, not a lock (a script written to a file first gets past it): the owner approves runs on github.com, and runs
# setup-ci-deploy.ps1 for prod from their own terminal. Exit code 2 blocks the tool call and shows stderr to the agent.
input="$(cat)"
block() {
  echo "Blocked: approving or reconfiguring the live deploy is owner-only (CLAUDE.md rule 8, ADR-0019). Ask the owner." >&2
  exit 2
}

# Matched in the whole tool input, with %XX escapes and JSON-escaped slashes decoded, ignoring case.
flat="$(printf '%s' "$input" | sed 's/\\\//\//g' | python3 -c 'import sys, urllib.parse; print(urllib.parse.unquote(sys.stdin.read()))' 2>/dev/null)" \
  || flat="$(printf '%s' "$input" | sed 's/%2[fF]/\//g; s/%5[fF]/_/g; s/\\\//\//g')"
q='["'"'"'?#[:space:]]'

# GitHub's REST paths and GraphQL mutations behind approvals, environments and OIDC subjects (repos/<owner>/<repo>/...,
# so the repository's own infra/environments/ files stay readable).
if printf '%s' "$flat" | grep -qiE "pending_deployments|repos/[^/[:space:]]+/[^/[:space:]]+/environments($q|/|$)|deployment-branch-policies|deployment_protection_rules|actions/oidc|actions/runs/[^/[:space:]]+/approvals?($q|/|$)"; then
  block
fi
if printf '%s' "$flat" | grep -qiE '(approve|reject)Deployments|(create|update|delete)Environment|(create|update|delete)DeploymentProtectionRule'; then
  block
fi
# The LIVE_* variables and the paper gate's switch (ADR-0024), however the command orders its arguments (gh variable set|delete, the REST variables paths).
if printf '%s' "$flat" | grep -qiE '(^|[^a-z_])(LIVE_[A-Z_]+|PAPER_APPROVAL)' &&printf '%s' "$flat" | grep -qiE 'gh[[:space:]]+variable|actions/variables|/variables([/?"'"'"'[:space:]]|$)|createRepositoryVariable|updateRepositoryVariable'; then
  block
fi
# The live deploy identity, and secrets or trusts on any app registration.
if printf '%s' "$flat" | grep -qiE 'cix-agents-deploy-prod|federated-credential|ad[[:space:]]+(app|sp)[[:space:]]+credential|addPassword|addKey|federatedIdentityCredentials'; then
  block
fi

exit 0
