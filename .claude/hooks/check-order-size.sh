#!/usr/bin/env bash
# PreToolUse hook for mcp__cix__place_order: refuses a buy larger than the largest orderQuote of any strategy in
# strategies/ (+10% for rounding). The owner switches strategies at run time in the engine, which this container can't
# see, so the hook bounds every buy by what any strategy allows, and the engine's strategy gate holds it to the active
# one (strategy-order-size). Needs Git Bash on Windows (Claude Code's default), not WSL bash. Defence in depth only: the
# engine's risk gate is still final. Exit code 2 blocks the call.
input="$(cat)"
dir="${CLAUDE_PROJECT_DIR:-.}"

# Sells reduce risk and exit positions that may have grown past orderQuote: never block them here.
if printf '%s' "$input" | grep -qiE '"side"[[:space:]]*:[[:space:]]*"[[:space:]]*sell[[:space:]]*"'; then
  exit 0
fi

number() { printf '%s' "$input" | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"?[0-9.eE+-]+" | head -1 | grep -oE '[0-9.eE+-]+$'; }
size="$(number baseSize)"
price="$(number limitPrice)"
if [ -z "$size" ] || [ -z "$price" ]; then
  echo "Blocked: place_order needs numeric baseSize and limitPrice." >&2
  exit 2
fi

max="$(cat "$dir"/strategies/*.json 2>/dev/null | grep -oE '"orderQuote"[[:space:]]*:[[:space:]]*[0-9.]+' | grep -oE '[0-9.]+$' \
  | awk 'NR == 1 || $1 + 0 > m { m = $1 + 0 } END { if (NR) print m }')"
if [ -z "$max" ]; then
  echo "Blocked: no orderQuote found in any strategy under $dir/strategies." >&2
  exit 2
fi

if awk -v s="$size" -v p="$price" -v m="$max" 'BEGIN { exit !(s * p > m * 1.10) }'; then
  echo "Blocked: order of $size at $price is above $max, the largest orderQuote of any strategy. Size it as the active strategy's orderQuote / ask." >&2
  exit 2
fi
exit 0
