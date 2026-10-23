#!/usr/bin/env bash
# PreToolUse hook for mcp__cix__place_order: refuses a buy larger than the active strategy's orderQuote (+10% for
# rounding). Defence in depth only: the engine's risk gate is still final. Exit code 2 blocks the call.
input="$(cat)"
dir="${CLAUDE_PROJECT_DIR:-.}"

# Sells reduce risk and exit positions that may have grown past orderQuote: never block them here.
if printf '%s' "$input" | grep -qiE '"side"[[:space:]]*:[[:space:]]*"sell"'; then
  exit 0
fi

number() { printf '%s' "$input" | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"?[0-9.eE+-]+" | head -1 | grep -oE '[0-9.eE+-]+$'; }
size="$(number baseSize)"
price="$(number limitPrice)"
if [ -z "$size" ] || [ -z "$price" ]; then
  echo "Blocked: place_order needs numeric baseSize and limitPrice." >&2
  exit 2
fi

settings="$dir/src/Cix.Engine/appsettings.json"
name="$(grep -A4 '"Strategy"' "$settings" | grep -oE '"Name"[[:space:]]*:[[:space:]]*"[a-z0-9-]+"' | grep -oE '"[a-z0-9-]+"$' | tr -d '"')"
version="$(grep -A4 '"Strategy"' "$settings" | grep -oE '"Version"[[:space:]]*:[[:space:]]*[0-9]+' | grep -oE '[0-9]+$')"
spec="$dir/strategies/$name.v$version.json"
max="$(grep -oE '"orderQuote"[[:space:]]*:[[:space:]]*[0-9.]+' "$spec" 2>/dev/null | grep -oE '[0-9.]+$')"
if [ -z "$max" ]; then
  echo "Blocked: no orderQuote found in the active strategy ($spec)." >&2
  exit 2
fi

if awk -v s="$size" -v p="$price" -v m="$max" 'BEGIN { exit !(s * p > m * 1.10) }'; then
  echo "Blocked: order of $size at $price is above the strategy's orderQuote of $max. Size it as orderQuote / ask." >&2
  exit 2
fi
exit 0
