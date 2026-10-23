# CIX trader: the Claude Code CLI plus the run loop. It holds no trading keys and reaches the engine only at
# 127.0.0.1 (it shares the engine's network namespace). Claude credentials arrive at run time as a secret file.
FROM node:22-bookworm-slim

# Pin with --build-arg CLAUDE_CODE_VERSION=x.y.z for reproducible images.
ARG CLAUDE_CODE_VERSION=latest
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates jq \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force

# Only what the trader needs: its instructions, MCP connection, hooks (the order-size check reads the active
# strategy from appsettings.json and strategies/), and the loop.
WORKDIR /cix
COPY .claude/settings.json .claude/settings.json
COPY .claude/hooks/ .claude/hooks/
COPY .claude/agents/trader.md .claude/agents/trader.md
COPY src/Cix.Engine/appsettings.json src/Cix.Engine/appsettings.json
COPY strategies/ strategies/
COPY tools/trader/ tools/trader/

RUN mkdir -p /cix/logs/trader && chown -R node:node /cix/logs
ENV DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
    CLAUDE_PROJECT_DIR=/cix
USER node
ENTRYPOINT ["bash", "/cix/tools/trader/run-trader.sh"]
