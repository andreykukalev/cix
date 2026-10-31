# 0026. Stocks: the owner's real portfolio

- Status: proposed
- Date: 2026-11-01

## Context

The stocks advisor (ADR-0023) advises a paper portfolio of its own and assumes the owner holds the same at Revolut's broker. The owner doesn't: he has his own holdings, buys on his own judgement too, and wants every piece of advice (sums, stops, sells) to start from what he really holds and the capital he gives the advisor. His broker account is in dollars. He changes the capital now and then (10,000 today, 5,000 later), and a change only needs to count from the next nightly run. Revolut's brokerage has no API, so the advisor can only know what he tells it.

## Decision

1. **The Stocks page sends the whole portfolio.** A dialog holds the capital and every holding: ticker (US shares only), shares, average price paid, the stop resting at the broker if any, and the day bought. Saving sends it all as one submission, never a change to apply on top of another, so a lost or repeated send can't leave the record half-changed.
2. **Written the way crypto's buttons write.** `POST /api/owner/stocks-portfolio` creates `portfolio-N` in a `requests` container of the stocks database, with the dashboard's writer identity (ADR-0012). Its role there allows creating items in that container and nothing else; provision-stocks.ps1 grants it, finding the identity from the dashboard's own settings. N is the first number the host hasn't seen; the same dialog sent twice (its client id) is one submission. No new secret.
3. **The next nightly run applies the newest.** The host reads its saved owner state (`state/owner`, with an ETag), looks at `portfolio-N` after the one it applied, checks the newest again (the same rules as the page) and applies it, or keeps the portfolio as it was and shows why it refused it. It never reads one twice.
4. **The advice is for the real portfolio.** The same advisor, on the same bars, runs a second time for the owner: sized on the owner's capital less what his holdings cost, his holdings checked first (their bars fetched before the rest of the watchlist), a stop for each, sells when the trend breaks or the stop is hit. The page shows it first, under "My portfolio"; the paper portfolio stays beside it as the benchmark. The market-wide search sizes its candidates on the owner's capital and leaves out what he holds.
5. **What the advisor assumes until told otherwise.** A stop it sets or raises is taken as placed (the next run trails from it). A sell is advised again each night until the owner sends the portfolio without it. A split moves the holding into the new terms, with a note to check it; a dividend changes nothing (the broker leaves stops and costs alone).

## Alternatives considered

| Option | Why not |
| --- | --- |
| Sending each buy and sell as its own change | Applying changes in order needs every one of them, exactly once; a whole portfolio is simple to check and to correct. The chat can still fill the dialog's figures later. |
| A writer secret of its own for the stocks database | Another secret to rotate; the existing writer gets a create-only role in the stocks account instead. |
| Applying the portfolio at once from the dashboard | The dashboard can't read EODHD or run the advisor, and the owner asked for the next scheduled run. |
| Dropping the paper portfolio | It shows what following every piece of advice earns, against which the owner's own choices can be judged. |

## Consequences

- The owner reruns provision-stocks.ps1 once (after setup-dashboard-requests.ps1 has made the writer): the `requests` container and the writer's role appear, and the new image is built.
- Each holding outside the watchlist costs one EODHD call a night: with the free key's 18 calls, a long list pushes watchlist stocks to the next night (noted `call-budget`). The fixed watchlist goes once the search is on.
- The owner records his buys and sells himself (the dialog now, the chat later); the advice is only as current as the last portfolio he sent.
- Next: warnings on holdings (sell half, sell all, buy more) from the Claude analyst, and the chat.
