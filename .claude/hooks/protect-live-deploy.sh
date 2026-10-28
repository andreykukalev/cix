#!/usr/bin/env bash
# PreToolUse hook (Bash, GitHub tools): agents may never approve or reconfigure the live deploy (ADR-0019). Its control is
# the owner's approval of each run in the GitHub environment prod, and agents use the owner's GitHub account: approving a
# pending deployment, changing an environment, the repository's OIDC subject or the LIVE_* variables, or touching the live
# deploy identity's credentials would let an agent deploy to real money without the owner. This is a tripwire, not a
# lock: the owner approves runs on github.com, and runs setup-ci-deploy.ps1 for prod from their own terminal.
# Exit code 2 blocks the tool call and shows stderr to the agent.
input="$(cat)"
block() {
  echo "Blocked: approving or reconfiguring the live deploy is owner-only (CLAUDE.md rule 6, ADR-0019). Ask the owner." >&2
  exit 2
}

# The REST paths and CLI commands behind approvals, environments, OIDC subjects and the live variables; and the live
# deploy identity's app registration and its secrets or trusts. Matched in the whole tool input, quoting or URL-encoding aside.
flat="$(printf '%s' "$input" | sed 's/%2[fF]/\//g; s/\\\//\//g')"
if printf '%s' "$flat" | grep -qiE 'pending_deployments|/environments([/"'"'"'?[:space:]]|$)|deployment-branch-policies|deployment_protection_rules|actions/oidc|/approvals?([/"'"'"'?[:space:]]|$)'; then
  block
fi
if printf '%s' "$flat" | grep -qiE '(variable|variables)[/[:space:]]+(set|delete|create|update)?[/[:space:]]*["'"'"']?LIVE_|actions/variables(/|[[:space:]"'"'"']+)LIVE_'; then
  block
fi
if printf '%s' "$flat" | grep -qiE 'cix-agents-deploy-prod|federated-credential|ad[[:space:]]+(app|sp)[[:space:]]+credential|addPassword|addKey|federatedIdentityCredentials'; then
  block
fi

exit 0
