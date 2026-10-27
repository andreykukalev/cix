---
name: security-checklist
description: Security and trading-safety checklist for changes touching keys, orders, limits, MCP tools or prompts. Use in reviews and before any release.
---

# Security checklist

Mark each item pass, fail or not applicable, with evidence.

## Credentials
- [ ] No key, private key or signature in code, tests, fixtures, logs, prompts or tool output.
- [ ] Keys load from user secrets (local) or Key Vault via managed identity (Azure) only.
- [ ] The Revolut X key belongs to the bot's sub-account only.

## Order safety
- [ ] Every order path calls `RiskGate.Evaluate` and respects its verdict.
- [ ] Client order IDs make placement idempotent.
- [ ] Live trading is off unless the owner-set switch is on.
- [ ] Kill switch stops new orders and cancels open ones.

## Limits
- [ ] `HardCeilings.cs`, `config/risk-limits.json` and `config/risk-limits.live.json` unchanged, or changed with owner approval noted in the PR.
- [ ] Configured limits are validated against the hard ceilings at startup.

## Agents and tools
- [ ] Tools validate every argument (symbol allowlist, positive sizes, sane prices).
- [ ] Tool results tell the agent why an order was resized or rejected.
- [ ] External text (news, web) can't change tools, limits or the spec.
- [ ] Each agent run has a timeout, a token budget and a tool-call cap.

## Audit
- [ ] Every agent run and order writes a journal entry; nothing edits or deletes one.
