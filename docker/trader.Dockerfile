# CIX agents (trader and market analyst): the Claude Code CLI plus their run loops. It holds no trading keys and reaches the engine only at
# 127.0.0.1 (it shares the engine's network namespace). Claude credentials arrive at run time as a secret file.
FROM node:22-bookworm-slim

# Pin with --build-arg CLAUDE_CODE_VERSION=x.y.z for reproducible images.
ARG CLAUDE_CODE_VERSION=latest
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl jq \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force

# Only what the trader needs: its instructions, MCP connection, hooks (the order-size check reads the active
# strategy from appsettings.json and strategies/), and the loop.
WORKDIR /cix
COPY .claude/settings.json .claude/settings.json
COPY .claude/hooks/ .claude/hooks/
COPY .claude/agents/trader.md .claude/agents/analyst.md .claude/agents/
COPY src/Cix.Engine/appsettings.json src/Cix.Engine/appsettings.json
COPY strategies/ strategies/
COPY tools/trader/ tools/trader/

# A Windows checkout may have CRLF line endings (e.g. one made before .gitattributes forced LF); bash would then fail on
# them, and a failing order-size hook blocks every buy. Strip them here so the image never depends on the checkout.
RUN find /cix/.claude/hooks /cix/tools/trader -name '*.sh' -exec sed -i 's/\r$//' {} + \
    && ! grep -rl "$(printf '\r')" /cix/.claude/hooks /cix/tools/trader --include='*.sh'
RUN mkdir -p /cix/logs/trader /cix/logs/analyst && chown -R node:node /cix/logs
ENV DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
    CLAUDE_PROJECT_DIR=/cix
USER node
ENTRYPOINT ["bash", "/cix/tools/trader/run-trader.sh"]
