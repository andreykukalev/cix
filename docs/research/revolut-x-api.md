# Revolut X API: confirmed behaviour

What the spike runs (`tools/spike/revx_spike.py`, issues #1 and #3) showed, as of October 2026. Prefer this over the published docs where they differ.

## Endpoints and limits

| Use | Endpoint | Auth | Limit |
|---|---|---|---|
| Balances, orders, private candles | `/api/1.0/...` | Ed25519 signed | 100/s, 1,000/min |
| Order book top (quotes) | `/api/2.0/public/order-book/{symbol}?limit=&region=` | none | 1/s |
| Tickers (all pairs) | `/api/1.0/public/tickers?region=` | none | 1/s |
| Candles | `/api/1.0/public/candles/{symbol}?interval=&since=&until=&region=` | none | 1/s |
| Pairs | `/api/1.0/public/configuration/pairs?region=` | none | 1/s |

- Base URL `https://revx.revolut.com`; public calls use `region=EEA`.
- `Retry-After` is in **milliseconds**.
- Signature: Ed25519 over `timestamp + METHOD + path + query + minified body`, base64, in `X-Revx-Signature`, with `X-Revx-API-Key` and `X-Revx-Timestamp` (ms).

## Shapes and inconsistencies

- Symbols: paths use `BTC-EUR`, responses often `BTC/EUR`. The client normalises to `BTC-EUR`.
- Pairs come back as an object keyed by `"BTC/EUR"`, not a list.
- Sides and states vary in case (`buy`/`BUY`); fields vary in name (`state`/`status`, `id`/`venue_order_id`).
- Numbers are strings; timestamps are epoch ms numbers.
- Public order book: `{data:{bids:[{price,quantity,count}],asks:[...]},metadata:{region,timestamp}}`, best level first. Recorded in `tests/Cix.Exchange.RevolutX.Tests/Fixtures/order_book.json`.
- **Signed order book (`/api/1.0/order-book/{symbol}`) names its levels differently**: `{aid, anm, s: SELL|BUYI, p, pc, pn, q, qc, qn, ve, no, ts, pdt}` (`p` price, `q` quantity, `no` order count, `pdt` publication ms), `metadata:{timestamp}` only. Confirmed on the live account on 2026-10-28, when the first signed-quotes build read `price` and failed. The parser reads both spellings; fixture `order_book_signed.json`.
- Tickers give bid/ask/mid/24h stats for every pair in one call but no sizes, so fills and stop confirmations use the order book (the paper exchange fills against level sizes). **Public tickers confirmed 2026-10-28** (`GET /api/1.0/public/tickers?region=EEA`, no credentials): `{data:[{symbol:"UNI/EUR", bid, ask, mid, index_price, last_price, low_24h, high_24h, price_change_24h, volume_24h, quote_volume_24h, region}], metadata:{timestamp}}`, 375 rows of which 61 EUR pairs, the same row shape as the signed endpoint; the engine's paper mode reads it every 5 s for every watched coin (ADR-0021).
- Candles: `{data:[{start,open,high,low,close,volume}]}`, oldest first from `since`; the last candle is still open. How a long range is paged is not yet confirmed (the spike prints a 24h paging check).

## Orders

- `DELETE` returns 204 with no body.
- A `GET` for an order right after placing it can return 404; retry briefly before treating it as missing.
- A timeout or 5xx on placement means the outcome is unknown (`OrderPlacementUncertainException`): reconcile by client order ID, never re-place blindly.

### From the published spec, not yet confirmed by a spike (October 2026)

Read from the OpenAPI document on developer.revolut.com. Treat as the plan until `revx_spike.py` has run against it.

- **Immediate-or-cancel is honoured** (spike `--ioc`, live account, 2026-10-28): a `limit` sell/buy with `time_in_force: ioc` that doesn't cross comes back from `POST /api/1.0/orders` already with `state: cancelled`, `filled_quantity 0`, and is not in the active list. So a protective sell never rests with the coins reserved; the engine's re-cancel path stays as a safety net. The crossing case (step D, DOGE-EUR, about 2 EUR): an IOC buy limited 0.5% above the ask filled at the ask, and an IOC sell limited 3% under the mid filled in full at the bid (`state: filled` in the placement response itself, answered in about 200 ms). The limit is a floor, not the fill price. Two details for the journal: the buy's fee was taken in the base coin (28.2925 bought, 28.267 available, about 0.09%), so a protective sell must size from the balance, not from the fill, which `SellableNowAsync` does; and `filled_amount` is reported to 2 decimals (1.99 for 28.267 × 0.07057 = 1.9948), so an average price derived from it is coarse on a small order (0.25% on 2 EUR, 0.02% on 25 EUR).
- `order_configuration` on `POST /api/1.0/orders` takes one of: `market`; `limit` with `time_in_force` `gtc` or `ioc` (the engine sends `ioc` for protective exits) and `execution_instructions` `allow_taker` or `post_only`; `conditional` with `trigger_price` and an optional `limit_price`; `tpsl` with `stop_loss` and `take_profit` triggered on the index price. Whether `conditional` and `tpsl` reserve balance, appear in active orders and survive for every pair is the question for the tpsl spike (about 5 EUR).
- Placement is limited to 10 orders a second and 1,000 a day (the daily cap is not applied to sub-accounts); at most 300 active orders.
- Signed `GET /api/1.0/tickers` (`symbols=` filter; `{data:[{symbol, bid, ask, mid, index_price, last_price, 24h stats}], metadata:{timestamp}}`, no sizes) and `GET /api/1.0/order-book/{symbol}` (`limit` up to 192; level shape above) share the signed 100/s, 1,000/min budget. The live engine uses both since #130: tickers for every coin every 2 s (30 requests a minute) and the signed book for the 20-second quotes and the stop-loss confirmation. Paper stays on the public endpoints. **Confirmed on the live account (spike `--tickers`, 2026-10-28):** the filter accepts the dash form with a literal comma (`symbols=BTC-EUR,ETH-EUR,HYPE-EUR,INJ-EUR` returned exactly those four, as `BTC/EUR` etc.); `metadata.timestamp` on both endpoints is the response time (about 5 ms before receipt, 100–135 ms after the request left), so it is not a snapshot age and a stale book can't hide behind it. The engine takes the earlier of that timestamp and the receipt time, keeps the later-stamped of a ticker and a book in the market state, and hands the protection the book it asked for whatever the two stamps say.
