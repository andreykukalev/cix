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

# What the tool call leaves behind: the whole file after a Write, Edit or MultiEdit (so a check sees the result, not
# only the typed fragment), the typed text itself, and a Bash command. Without jq, or if jq fails, the typed text stands
# in for the file (newlines as spaces) and removed lines can't be seen.
path="" before="" after="" typed="" command=""
flat() { printf '%s' "$input" | sed 's/\\n/ /g; s/\\"/"/g'; }
if command -v jq >/dev/null 2>&1 && path="$(printf '%s' "$input" | jq -r '.tool_input.file_path // .tool_input.notebook_path // empty' 2>/dev/null)"; then
  command="$(printf '%s' "$input" | jq -r '.tool_input.command // empty' 2>/dev/null)"
  typed="$(printf '%s' "$input" | jq -r '[.tool_input | (.content, .new_string, .new_source, (.edits // [] | .[].new_string)) | strings] | join("\n")' 2>/dev/null)" || typed="$(flat)"
  if [[ -n "$path" ]]; then
    source_file=(--arg before "")   # the file as it is now: read by jq directly, so its size doesn't matter
    if [[ -f "$path" ]]; then before="$(cat -- "$path" 2>/dev/null)"; source_file=(--rawfile before "$path"); fi
    if ! after="$(printf '%s' "$input" | jq -r "${source_file[@]}" '
      def first($s; $o; $n): ($s | split($o)) as $p | if $o == "" or ($p | length) < 2 then $s else $p[0] + $n + ($p[1:] | join($o)) end;
      def apply($s; $e): if ($e.replace_all // false) then ($s | split($e.old_string) | join($e.new_string)) else first($s; $e.old_string; $e.new_string) end;
      .tool_input as $t
      | if $t.content != null then $t.content
        elif $t.edits != null then reduce $t.edits[] as $e ($before; apply(.; $e))
        elif $t.old_string != null then apply($before; $t)
        elif $t.new_source != null then $t.new_source
        else $before end' 2>/dev/null)"; then
      after="$(flat)"; before=""
    fi
  fi
else
  path="$(printf '%s' "$input" | grep -oE '"(file|notebook)_path"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | sed -E 's/.*"([^"]*)"$/\1/')"
  printf '%s' "$input" | grep -qE '"command"[[:space:]]*:' && command="$(flat)"
  [[ -n "$path" ]] && { after="$(flat)"; typed="$after"; }
fi
# A plain search or history command (nothing chained, redirected or run through it) only reads.
read_only=false
if printf '%s' "$command" | grep -qE '^[[:space:]]*(git (log|show|diff|grep)|grep|rg)[[:space:]]' \
   && ! printf '%s' "$command" | grep -qE '[;&|`<>]|\$\(|--pre'; then
  read_only=true
fi
production="(aspnetcore|dotnet)_environment[\\\"']*[[:space:]]*[:=][[:space:]]*[\\\"']*production"
allow_live="allow_?live[\\\"']*[[:space:]]*[:=][[:space:]]*[\\\"']*true"   # ManualRequests:AllowLive (ADR-0011)
quoted_live="[\\\"'](live|production)[\\\"']"
templates='(^|[^a-z])infra([/[:space:]]|$)|\.bicep'

# 1c. The Azure templates (infra/, *.bicep), as they will be after the edit:
#   - no quoted 'Live' or 'Production' anywhere (prose about the live engine stays allowed: comments don't quote it);
#   - the lines that pin the engine to Paper and to the Azure environment (.NET assumes Production without one) stay
#     exactly as they are: every other line naming those settings is refused, and none of them can go.
#   This is a tripwire for mistakes, not a parser: CI checks the templates too (tests/azure), and the engine refuses Live
#   outside Production.
if [[ -n "$path" ]] && printf '%s' "$path" | grep -qiE '(^|/)infra/|\.bicep(param)?$'; then
  if printf '%s' "$after" | grep -qiE "$quoted_live"; then
    block
  fi
  for pinned in "{ name: 'Trading__Mode', value: 'Paper' }" "{ name: 'ASPNETCORE_ENVIRONMENT', value: 'Azure' }"; do
    setting="${pinned#*name: \'}"; setting="${setting%%\'*}"
    if printf '%s\n' "$after" | grep -iF -- "$setting" | grep -vE '^[[:space:]]*//' | sed -E 's/^[[:space:]]+|[[:space:]]+$//g' | grep -qvxF -- "$pinned"; then
      block
    fi
    was="$(printf '%s\n' "$before" | sed -E 's/^[[:space:]]+|[[:space:]]+$//g' | grep -cxF -- "$pinned")"
    now="$(printf '%s\n' "$after" | sed -E 's/^[[:space:]]+|[[:space:]]+$//g' | grep -cxF -- "$pinned")"
    if (( now < was )); then
      block
    fi
  done
fi

# 1d. The engine's environment set to Production, the only one where the trading mode can be Live, or owner requests
#     allowed on a Live engine: in any file but tests and docs (which may describe them), or a command. So is a line in a
#     script (any file outside tests and docs) that would write Live or Production into the templates.
if [[ -n "$path" ]] && ! printf '%s' "$path" | grep -qiE '(^|/)(tests|docs)/|\.md$'; then
  if printf '%s' "$after" | grep -qiE "$production|$allow_live"; then
    block
  fi
  if printf '%s\n' "$typed" | grep -iE "$templates" | grep -qiE "$quoted_live|[/=][[:space:]]*(live|production)([^a-z]|$)"; then
    block
  fi
fi
if [[ -n "$command" && "$read_only" == false ]]; then
  if printf '%s' "$command" | grep -qiE "$production|$allow_live|--environment[=[:space:]]+[\\\"']*production|remove-env-vars.*(aspnetcore|dotnet)_environment"; then
    block
  fi
  # A command on the templates that writes Live or Production as a value ('Live', =Live, s/Paper/Live/).
  if printf '%s' "$command" | grep -qiE "$templates" \
     && printf '%s' "$command" | grep -qiE "[/\\\"'=:][[:space:]]*(live|production)([^a-z]|$)"; then
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

# 2b. Azure CLI or PowerShell commands on the live environment (prod, in rg-cix-live: ADR-0017). Its resources are the owner's
#     alone, from their own sign-in, and a session on their PC shares that sign-in: switching the engine, deploying, uploading
#     keys or promoting images there is refused here whatever the command looks like (az rest, --yaml, provision.ps1).
if [[ -n "$command" && "$read_only" == false ]]; then
  if printf '%s' "$command" | grep -qiE '(^|[;&|(`[:space:]])(az|pwsh|powershell)([[:space:]]|$)|\.ps1' \
     && printf '%s' "$command" | grep -qiE 'rg-cix-live|(ca|caj|id|cae|log|vnet)-cix-prod-?|(kv|cr|st)cixprod|-environment[=:[:space:]]+["'"'"']?prod([^a-z0-9-]|$)'; then
    block
  fi
fi

# 3. A Mode = Live setting anywhere else.
if printf '%s' "$input" | grep -qiE 'mode[\"]*([[:space:]]*[:=][[:space:]]*|[[:space:]]+)[\"]*live'; then
  block
fi

exit 0
