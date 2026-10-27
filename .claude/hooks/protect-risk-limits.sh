#!/usr/bin/env bash
# PreToolUse hook (Edit, Write, MultiEdit, NotebookEdit, Bash): blocks agent changes to the human-only risk ceilings and
# limits files: HardCeilings.cs, config/risk-limits.json and the live engine's config/risk-limits.live.json.
# Exit code 2 blocks the tool call and shows stderr to the agent.
input="$(cat)"
block() {
  echo "Blocked: HardCeilings.cs and the config/risk-limits*.json files are human-only (see CLAUDE.md rule 3). Explain the proposed change to the owner instead." >&2
  exit 2
}
files='(HardCeilings\.cs|risk-limits[^[:space:]"'\''/\\]*\.json)'

# 1. An edit or write of one of the files, by any path or spelling of its case (Windows and macOS ignore case).
if printf '%s' "$input" | grep -qiE '"(file|notebook)_path"[[:space:]]*:[[:space:]]*"[^"]*'"$files"'"'; then
  block
fi

# 2. A command naming one of them (cp, mv, sed -i, a redirect, a script), unless it plainly only reads: a search, a history
#    command or a viewer, with nothing chained, redirected or substituted.
command=""
if command -v jq >/dev/null 2>&1; then
  command="$(printf '%s' "$input" | jq -r '.tool_input.command // empty' 2>/dev/null)"
elif printf '%s' "$input" | grep -qE '"command"[[:space:]]*:'; then
  command="$input"
fi
if [[ -n "$command" ]] && printf '%s' "$command" | grep -qiE "$files"; then
  if printf '%s' "$command" | grep -qE '^[[:space:]]*(git (log|show|diff|grep|status)|grep|rg|cat|head|tail|wc|ls)[[:space:]]' \
     && ! printf '%s' "$command" | grep -qE '[;&|`<>]|\$\(|--pre|--output|-o[[:space:]]'; then
    exit 0
  fi
  block
fi

exit 0
