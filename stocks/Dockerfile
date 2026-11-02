# The stocks advisory's image (ADR-0023): the nightly run, and the Claude analyst (ADR-0027) on the same build. Built from
# the repository root (`docker build -f stocks/Dockerfile .`): it needs Cix.Domain, the shared build files and the agents'
# shared shell helpers, never the engine. No secrets are baked in: the EODHD key arrives at run time as
# /run/secrets/eodhd-api-token and the Claude token as /run/secrets/claude_token, from Key Vault, each to its own container.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Cix.Domain/ src/Cix.Domain/
COPY stocks/src/ stocks/src/
COPY stocks/config/ stocks/config/
RUN dotnet publish stocks/src/Cix.Stocks.Host/Cix.Stocks.Host.csproj -c Release -o /app --nologo

# Node for the Claude Code CLI, from the official image (the runtime image has none).
FROM node:22-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/runtime:10.0
# Pin with --build-arg CLAUDE_CODE_VERSION=x.y.z (provision-stocks.ps1 passes stocks.json's claudeCodeVersion).
ARG CLAUDE_CODE_VERSION=2.1.316
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/npm
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates jq \
    && rm -rf /var/lib/apt/lists/* \
    && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force \
    && claude --version
WORKDIR /app
COPY --from=build /app ./
COPY stocks/analyst/ analyst/
COPY tools/trader/agent-common.sh analyst/agent-common.sh
# A Windows checkout may have CRLF line endings; the shell scripts would fail on them.
RUN sed -i 's/\r$//' analyst/*.sh && ! grep -l "$(printf '\r')" analyst/*.sh \
    && mkdir -p /work && chown "$APP_UID" /work
ENV DOTNET_NOLOGO=1 \
    DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1
USER $APP_UID
# The nightly run; the analyst job runs analyst/run.sh.
ENTRYPOINT ["dotnet", "Cix.Stocks.Host.dll"]
