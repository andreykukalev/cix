#!/usr/bin/env bash
# PreToolUse hook (Edit, Write, MultiEdit, Bash): agents may never switch trading to Live.
# Catches a "Mode": "Live" setting written to any file and Mode=Live / Mode:Live on a command line
# (for example an environment variable or a --Trading:Mode argument). Exit code 2 blocks the tool call.
input="$(cat)"

if printf '%s' "$input" | grep -qiE 'mode[\"]*([[:space:]]*[:=][[:space:]]*|[[:space:]]+)[\"]*live'; then
  echo "Blocked: switching trading to Live is owner-only (CLAUDE.md rule 6). Stay in Paper mode and ask the owner." >&2
  exit 2
fi

exit 0
