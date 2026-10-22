#!/usr/bin/env bash
# PreToolUse hook: blocks agent edits to the human-only risk ceilings.
# Exit code 2 blocks the tool call and shows stderr to the agent.
input="$(cat)"

if printf '%s' "$input" | grep -qE '"file_path"[[:space:]]*:[[:space:]]*"[^"]*(HardCeilings\.cs|risk-limits\.json)"'; then
  echo "Blocked: HardCeilings.cs and config/risk-limits.json are human-only (see CLAUDE.md rule 3). Explain the proposed change to the owner instead." >&2
  exit 2
fi

exit 0
