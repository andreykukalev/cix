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
- Order book: `{data:{bids:[{price,quantity,count}],asks:[...]},metadata:{region,timestamp}}`, best level first. Recorded in `tests/Cix.Exchange.RevolutX.Tests/Fixtures/order_book.json`.
- Tickers give bid/ask/mid/24h stats for every pair in one call but no sizes, so quotes use the order book (the paper exchange fills against level sizes).
- Candles: `{data:[{start,open,high,low,close,volume}]}`, oldest first from `since`; the last candle is still open. How a long range is paged is not yet confirmed (the spike prints a 24h paging check).

## Orders

- `DELETE` returns 204 with no body.
- A `GET` for an order right after placing it can return 404; retry briefly before treating it as missing.
- A timeout or 5xx on placement means the outcome is unknown (`OrderPlacementUncertainException`): reconcile by client order ID, never re-place blindly.
