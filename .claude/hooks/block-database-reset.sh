#!/usr/bin/env bash
# PreToolUse hook (Bash): agents may never reset or delete the database. tools/azure/reset-database.ps1 is an owner-only
# testing tool (docs/azure.md, ADR-0008): it deletes journal entries, which CLAUDE.md rule 4 otherwise forbids. Deleting
# Cosmos DB accounts, databases or containers directly is blocked for the same reason. Exit code 2 blocks the call.
input="$(cat)"
if printf '%s' "$input" | grep -qiE '"command"[[:space:]]*:'; then
  if printf '%s' "$input" | grep -qiE 'reset-database' \
     || printf '%s' "$input" | grep -qiE 'cosmosdb[^"]*(delete|regenerate)' \
     || printf '%s' "$input" | grep -qiE 'compose[^"]*down[^"]*(-v|--volumes)'; then
    echo "Blocked: resetting or deleting the database (or its keys) is owner-only. Describe what you need and ask the owner to run it." >&2
    exit 2
  fi
fi
exit 0
