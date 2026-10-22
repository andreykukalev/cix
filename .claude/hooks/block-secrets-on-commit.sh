#!/usr/bin/env bash
# PreToolUse hook for Bash: before any `git commit`, scan staged changes for secrets.
# Exit code 2 blocks the commit and shows stderr to the agent.
input="$(cat)"

if ! printf '%s' "$input" | grep -qE 'git[[:space:]]+commit'; then
  exit 0
fi

staged_files="$(git diff --cached --name-only 2>/dev/null)"
if printf '%s\n' "$staged_files" | grep -qE '\.(pem|key|pfx|p12)$|(^|/)secrets\.json$|(^|/)\.env'; then
  echo "Blocked: a key or secrets file is staged. Unstage it and add it to .gitignore." >&2
  exit 2
fi

if git diff --cached -U0 2>/dev/null | grep -E '^\+' | grep -qiE 'BEGIN [A-Z ]*PRIVATE KEY|X-Revx-API-Key[\"'"'"' :=]+[A-Za-z0-9]{32,}|ANTHROPIC_API_KEY=[^$[:space:]]+|AccountKey=[A-Za-z0-9+/=]{20,}'; then
  echo "Blocked: staged changes look like they contain a private key, API key or connection string. Remove it and load it from user secrets or Key Vault." >&2
  exit 2
fi

exit 0
