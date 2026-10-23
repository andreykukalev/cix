# 0005. Docker Compose locally, Azure Container Apps in the cloud

- Status: accepted
- Date: 2026-10-23
- Supersedes: 0004

## Context

Paper trading runs on the owner's PC, which restarts. The engine, the Cosmos DB emulator and the trader loop were three things started by hand, and none came back after a reboot. The same pieces must later run in Azure with as little change as possible.

## Decision

Run the system as containers. Locally, one Docker Compose file starts `cosmos` (the emulator), `engine` and `trader`, each with `restart: unless-stopped`. In Azure, the same images run on **Azure Container Apps**: the engine as an app with one always-on replica, the trader (and the analyst and reviewer) as **scheduled Container Apps Jobs**, Cosmos DB serverless, Key Vault for secrets.

- The tool server stays loopback-only until it has authentication: in Compose, agent containers share the engine's network namespace (`network_mode: service:engine`), so they reach it at `127.0.0.1` and nothing else can.
- Secrets are Docker secrets (files under `/run/secrets`, read through .NET key-per-file configuration); in Azure the same names come from Key Vault.
- Claude Code authenticates with a long-lived token from `claude setup-token` (`CLAUDE_CODE_OAUTH_TOKEN`), or an Anthropic API key (`ANTHROPIC_API_KEY`) as the alternative.

## Alternatives considered

| Option | Why not |
| --- | --- |
| App Service B1 for the engine (ADR-0004) | Works, but runs the engine differently from the agents; Container Apps hosts both from the same images |
| Azure Container Instances | No scheduler for the hourly agents, no revisions; restarts are basic |
| Keep running with `dotnet run` and a PowerShell loop | Nothing restarts after a reboot; three manual steps |

## Consequences

- One image per component, the same locally and in Azure.
- Agent containers need Claude credentials as secrets; a setup token expires after a year, so the trader checks it and warns before expiry.
- Container Apps with an always-on replica costs more than a stopped VM; review against the $150 budget at deployment.
