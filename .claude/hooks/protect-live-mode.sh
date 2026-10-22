#!/usr/bin/env bash
# PreToolUse hook (Edit, Write, MultiEdit, NotebookEdit, Bash): agents may never switch trading to Live.
# This is a tripwire, not the main control: the engine refuses Live outside Production and Paper mode holds no
# trading credentials. Exit code 2 blocks the tool call and shows stderr to the agent.
input="$(cat)"
block() {
  echo "Blocked: switching trading to Live is owner-only (CLAUDE.md rule 6). Stay in Paper mode and ask the owner." >&2
  exit 2
}

# 1. Any "Live" written to a configuration file, however the key and value are laid out.
if printf '%s' "$input" | grep -qiE '"file_path"[[:space:]]*:[[:space:]]*"[^"]*(appsettings[^"/]*\.json|launchSettings\.json|secrets\.json|/\.env[^"/]*|\.env)"'; then
  if printf '%s' "$input" | grep -qiE '(^|[^a-z])live([^a-z]|$)'; then
    block
  fi
fi

# 1b. A strategy spec marked for live trading: only the owner promotes a spec to live.
if printf '%s' "$input" | grep -qiE '"file_path"[[:space:]]*:[[:space:]]*"[^"]*strategies/[^"]*\.json"'; then
  if printf '%s' "$input" | grep -qiE 'status[\\"]*[[:space:]]*:[[:space:]]*[\\"]*live'; then
    block
  fi
fi

# 2. Commands that set or touch the trading mode (environment variables, arguments, user secrets)
#    or edit configuration files to say Live.
if printf '%s' "$input" | grep -qiE '"command"[[:space:]]*:'; then
  if printf '%s' "$input" | grep -qiE 'trading(__|:)mode'; then
    block
  fi
  if printf '%s' "$input" | grep -qiE '(appsettings|launchSettings|secrets\.json|\.env|strategies/)' \
     && printf '%s' "$input" | grep -qiE '(^|[^a-z])live([^a-z]|$)'; then
    block
  fi
fi

# 3. A Mode = Live setting anywhere else.
if printf '%s' "$input" | grep -qiE 'mode[\"]*([[:space:]]*[:=][[:space:]]*|[[:space:]]+)[\"]*live'; then
  block
fi

exit 0
