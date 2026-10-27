# Live trading: the owner's runbook

Live trading runs next to paper, never instead of it (epic #72). The live engine runs in Azure in its own resource group, `rg-cix-live`, with its own Key Vault, identity and database in the shared Cosmos DB account. CI and the agents have no role there (ADR-0013, [azure-hosting.md](research/azure-hosting.md), owner decisions 5 and 6). The first run is 100 EUR on the fixed Safe strategy.

Every step on this page is yours: agents can't set the live mode, add the keys, change the live limits or resume trading (CLAUDE.md rules 3, 5 and 6). This page grows as each part of #72 lands.

## The live limits (#66)

The live engine reads its own limits file, `config/risk-limits.live.json`, never paper's `config/risk-limits.json`. Without it, a Live engine refuses to start. Like paper's file, only you write it (CLAUDE.md rule 3; a hook stops agents from writing either file, whether by editing it or with a command), and the hard ceilings in `HardCeilings.cs` still cap whatever it says. It is built into the image, so a change reaches the live engine with the next image you promote.

A proposal for the first run, 100 EUR on the fixed Safe strategy. Safe's own limits are tighter still: 5 EUR orders, at most 10 EUR per coin, 25% of the allocation in coins, and no buys after a 3 EUR loss in a day.

```json
{
  "Risk": {
    "AllocationQuote": 100,
    "MaxOrderQuote": 20,
    "MaxPositionQuotePerSymbol": 25,
    "MaxDailyLossQuote": 5,
    "MaxDrawdownFraction": 0.10,
    "MaxOrdersPerMinute": 4,
    "MaxPriceDeviationFraction": 0.01,
    "AllowedSymbols": [ "BTC-EUR", "ETH-EUR", "SOL-EUR", "XRP-EUR" ]
  }
}
```

- **Drawdown 10%**, not paper's 15%: new buys stop after a 10% fall from peak equity (10 EUR from 100 EUR) until you resume. Sells, stops and targets carry on.
- **Daily loss 5 EUR**, half the ceiling (10% of the allocation).
- **Four of the most liquid pairs.** There is no `AllowedQuoteCurrency` for now, because adding pairs from the dashboard is paper-only until #71. Revolut X's smallest order on these pairs is 0.1 EUR (its public pairs list, 27 Oct), well under Safe's 5 EUR.

## The strategy

A Live engine runs only a spec whose `status` is `live`, and only you set that (the `protect-live-mode` hook stops agents). For the first run, `strategies/safe.v2.json` is Safe v1 with the same parameters, and instructions that no longer say "paper". It is a `draft`, which never runs: change its `"status"` to `"live"`. Leave `safe.v1.json` as it is. The paper database holds a copy of v1, and a spec file that no longer matches its copy can't be selected on paper. A `live` spec runs on paper too, so paper can switch to Safe v2 and keep the same baseline as live.

## The Revolut X keys (#68)

### The sub-account

Create a dedicated Revolut X sub-account for CIX and move only the allocation into it: 100 EUR for the first run. The aim is that the engine's key reaches this sub-account's funds and nothing else you hold. Check, when you create the key, that Revolut X ties it to the sub-account: if it doesn't, the key reaches everything in the account, and the engine's reconciliation halts trading at your first trade elsewhere. The live engine halts trading whenever the holdings change outside its own orders, so don't trade in this sub-account by hand either. Record what you find in [revolut-x-api.md](research/revolut-x-api.md).

### Creating the key pair and the API key

Create the key pair on your own machine. The private key never leaves it, except into the live Key Vault.

```bash
openssl genpkey -algorithm ed25519 -out revx_private.pem
openssl pkey -in revx_private.pem -pubout -out revx_public.pem
```

In the Revolut X web app, open the sub-account's API settings and add a key with the contents of `revx_public.pem`. Revolut X then shows the 64-character API key. If Revolut X offers permission choices, choose trading only, never withdrawals or transfers. If Revolut X offers an IP allowlist, leave it for now: the live engine has no fixed outbound IP yet, and that is decided together with its deployment (owner decision 3).

Keep `revx_private.pem` outside the repository (`.gitignore` excludes `*.pem` anyway). Never paste either key into a chat, an issue or a pull request.

### Where the keys go

| Secret | Key Vault secret (live vault) | Engine setting (secret file) |
|---|---|---|
| The API key | `revolutx-api-key` | `RevolutX__ApiKey` |
| The private key, PEM | `revolutx-private-key` | `RevolutX__PrivateKeyPem` |

The live engine's container app references the two secrets in its own Key Vault and mounts them as files under `/run/secrets`. A secret volume names each file after its secret unless told otherwise, so the volume maps them to the names the engine reads, as the jobs do for `claude-token`: `secrets: [{ secretRef: 'revolutx-api-key', path: 'RevolutX__ApiKey' }, { secretRef: 'revolutx-private-key', path: 'RevolutX__PrivateKeyPem' }]`. The engine reads them only in Live mode:

- **Paper refuses them.** A Paper engine with either file in its secrets folder, or either setting from any other source, refuses to start. Paper mode keeps holding no trading credentials.
- **Live needs both.** A Live engine without both refuses to start, rather than failing at its first order. Both are trimmed first, so a file saved with a Windows line ending works, and an API key with a space or line break inside refuses to start.
- **Nothing prints them.** The engine's settings print as `<set>` or `<missing>`, and startup errors name the settings without their values.

The script that uploads the two files to the live vault, without showing them, comes with the live engine's deployment (#66).

### Rotating the keys

Rotate the keys at least once a year, and at once if you think a key was exposed.

1. Create a new key pair and API key as above. If Revolut X lets the sub-account have two keys at once, the old one keeps working meanwhile; otherwise pause trading first.
2. Upload both new secrets to the live vault. Container Apps picks up a new secret version only within 30 minutes, so a restart straight away can still mount the old key. Wait 30 minutes, then restart the live engine's revision.
3. Check that the engine uses the new key before you remove the old one. The old key still works at this point, so a recent sync alone doesn't prove it. The deployment script that comes with #66 will show which secret version the revision mounted.
4. Delete the old API key in Revolut X, then delete the old `revx_private.pem`.

If a key may be exposed, delete it in Revolut X first, and read the next section: open orders stay on the book. Then stop live trading until the new key is in place.

### Stopping live trading at once

Deleting the API key in Revolut X stops the engine from placing or cancelling orders, whatever the engine does. Orders already open stay on the book and can still fill, so cancel them in the Revolut X web app. Without the key the engine can't sell at a stop either: whatever the sub-account holds stays there until you sell it yourself or a new key is in place.
