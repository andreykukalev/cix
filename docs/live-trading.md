# Live trading: the owner's runbook

Live trading runs next to paper, never instead of it (epic #72). The live engine runs in Azure in its own resource group, `rg-cix-live`, with its own Key Vault, identity and database in the shared Cosmos DB account. CI and the agents have no role there (ADR-0013, [azure-hosting.md](research/azure-hosting.md), owner decisions 5 and 6). The first run is 100 EUR on the fixed Safe strategy.

Every step on this page is yours: agents can't set the live mode, add the keys, change the live limits or resume trading (CLAUDE.md rules 3, 5 and 6). This page grows as each part of #72 lands.

## The Revolut X keys (#68)

### The sub-account

Create a dedicated Revolut X sub-account for CIX and move only the allocation into it: 100 EUR for the first run. A sub-account has its own funds and its own rate limits, so the engine can't spend, sell or see anything else you hold. The live engine halts trading if the holdings change outside its own orders, so don't trade in this sub-account by hand.

### Creating the key pair and the API key

Create the key pair on your own machine. The private key never leaves it, except into the live Key Vault.

```bash
openssl genpkey -algorithm ed25519 -out revx_private.pem
openssl pkey -in revx_private.pem -pubout -out revx_public.pem
```

In the Revolut X web app, open the sub-account's API settings and add a key with the contents of `revx_public.pem`. Revolut X then shows the 64-character API key. Give it trading permission only, never withdrawals or transfers. If Revolut X offers an IP allowlist, leave it for now: the live engine has no fixed outbound IP yet, and that is decided together with its deployment (owner decision 3).

Keep `revx_private.pem` outside the repository (`.gitignore` excludes `*.pem` anyway). Never paste either key into a chat, an issue or a pull request.

### Where the keys go

| Secret | Key Vault secret (live vault) | Engine setting (secret file) |
|---|---|---|
| The API key | `revolutx-api-key` | `RevolutX__ApiKey` |
| The private key, PEM | `revolutx-private-key` | `RevolutX__PrivateKeyPem` |

The live engine's container app references the two secrets in its own Key Vault and mounts them as files under `/run/secrets`. The engine reads them only in Live mode:

- **Paper refuses them.** A Paper engine with either file in its secrets folder, or either setting from any other source, refuses to start. Paper mode keeps holding no trading credentials.
- **Live needs both.** A Live engine without both refuses to start, rather than failing at its first order.
- **Nothing prints them.** The engine's settings print as `<set>` or `<missing>`, and startup errors name the settings without their values.

The script that uploads the two files to the live vault, without showing them, comes with the live engine's deployment (#66).

### Rotating the keys

Rotate the keys at least once a year, and at once if you think a key was exposed.

1. Create a new key pair and API key as above. If Revolut X lets the sub-account have two keys at once, the old one keeps working meanwhile; otherwise pause trading first.
2. Upload both new secrets to the live vault. Restart the live engine's revision so it reads them.
3. Check the live engine's status: its last sync with the exchange should be recent and show no errors.
4. Delete the old API key in Revolut X, then delete the old `revx_private.pem`.

If a key may be exposed, delete it in Revolut X first. Then stop live trading (see the next section) until the new key is in place.

### Stopping live trading at once

Deleting the API key in Revolut X stops every order at the exchange, whatever the engine does. Whatever the sub-account holds stays there.
