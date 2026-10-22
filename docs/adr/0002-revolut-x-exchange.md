# 0002. Revolut X as the exchange

- Status: accepted
- Date: 2026-10-22

## Context

The owner is an EU resident (Lithuania). Binance holds no MiCA licence and restricted services for EU users from 1 July 2026. The owner already uses Revolut and wants money and trading easy to trace.

## Decision

Trade on Revolut X through its REST API, using a dedicated sub-account that holds only the bot's allocation. Keep the exchange behind an interface so another venue can be added later.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Binance | No MiCA licence; EU access uncertain |
| Another MiCA-licensed exchange (Kraken, Bybit EU) | Possible later; Revolut fits the owner's existing money flow |

## Consequences

- No WebSocket: market data, orders and fills are polled within the rate limits.
- No official MCP server or .NET SDK: we build a thin signed REST client and our own tool server.
- No testnet: paper trading is simulated in the engine from live prices.
- Sub-accounts give a second, exchange-enforced boundary around the allocation.
- Open: does the API support IP allowlisting or trade-only keys? The spike answers this.
