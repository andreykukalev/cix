#!/usr/bin/env python3
"""Revolut X API spike for CIX (issue #1).

Checks, in order:
  1. Authenticated read: balances.
  2. Trading pairs and step sizes for the target symbol.
  3. Recent candles.
  4. Optional (--place-and-cancel): place a tiny post-only limit buy far below the market,
     read it back, cancel it, and confirm it is cancelled.

Separate checks for the crash protection (October 2026, after #129 and #130):
  --tickers   Signed tickers and order book, read-only: does the `symbols=` filter take the dash form with a
              literal comma, as the engine sends it, and what does each endpoint's metadata.timestamp mean.
  --ioc       Immediate-or-cancel limit orders on a pair the live engine doesn't trade (default DOGE-EUR):
              first a buy far below the market that must neither fill nor stay open (costs nothing), then,
              after you type yes, a round trip of about 2 EUR: an IOC buy at the ask, then an IOC sell of the
              same coins priced 3% under the mid, the way the engine's stop-loss sells.

Runs on YOUR machine. Your keys never leave it.

Setup:
  pip install requests cryptography
  # Ed25519 key pair; upload the PUBLIC key in the Revolut X web app (API settings):
  openssl genpkey -algorithm ed25519 -out revx_private.pem
  openssl pkey -in revx_private.pem -pubout -out revx_public.pem
  export REVX_API_KEY=...            # the 64-character key Revolut X shows you
  export REVX_PRIVATE_KEY_PATH=./revx_private.pem

Usage:
  python revx_spike.py --symbol BTC-EUR
  python revx_spike.py --symbol BTC-EUR --place-and-cancel
  python revx_spike.py --symbol BTC-EUR --public     # public market data only, no keys needed
  python revx_spike.py --tickers                      # signed tickers + book timestamps, read-only
  python revx_spike.py --ioc                          # IOC checks on DOGE-EUR, about 2 EUR round trip

Never commit revx_private.pem; .gitignore excludes *.pem.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import sys
import time
import uuid
from decimal import ROUND_DOWN, Decimal

import requests
from cryptography.hazmat.primitives.serialization import load_pem_private_key

BASE = "https://revx.revolut.com"


class RevX:
    def __init__(self, api_key: str, private_key_path: str) -> None:
        self.api_key = api_key
        with open(private_key_path, "rb") as f:
            self.key = load_pem_private_key(f.read(), password=None)

    def raw(self, method: str, path: str, query: str = "", body: dict | None = None) -> requests.Response:
        """Signs and sends the query exactly as given (no re-encoding), so the signature covers what is sent."""
        body_text = json.dumps(body, separators=(",", ":")) if body is not None else ""
        ts = str(int(time.time() * 1000))
        message = f"{ts}{method.upper()}{path}{query}{body_text}".encode("utf-8")
        signature = base64.b64encode(self.key.sign(message)).decode()
        headers = {
            "X-Revx-API-Key": self.api_key,
            "X-Revx-Timestamp": ts,
            "X-Revx-Signature": signature,
            "Accept": "application/json",
        }
        if body is not None:
            headers["Content-Type"] = "application/json"
        url = BASE + path + (f"?{query}" if query else "")
        return requests.request(method, url, headers=headers, data=body_text or None, timeout=15)

    def request(self, method: str, path: str, params: dict | None = None, body: dict | None = None,
                allow: tuple[int, ...] = ()):
        query = "&".join(f"{k}={v}" for k, v in (params or {}).items())
        r = self.raw(method, path, query, body)
        print(f"  {method} {path} -> HTTP {r.status_code}")
        if r.status_code in allow:
            return None
        if r.status_code >= 400:
            print(f"  body: {r.text[:500]}")
            r.raise_for_status()
        return r.json() if r.text else None


def step_round(value: Decimal, step: Decimal) -> Decimal:
    return (value / step).to_integral_value(rounding=ROUND_DOWN) * step


def unwrap(payload):
    return payload.get("data", payload) if isinstance(payload, dict) else payload


def active_orders(revx: RevX) -> list[dict]:
    rows = unwrap(revx.request("GET", "/api/1.0/orders/active")) or []
    return [o for o in rows if isinstance(o, dict)]


def get_order(revx: RevX, venue_id: str, attempts: int = 8) -> dict | None:
    """Orders may not be visible for a moment after placement, so retry, then fall back to the active list."""
    for attempt in range(attempts):
        order = unwrap(revx.request("GET", f"/api/1.0/orders/{venue_id}", allow=(404,)))
        if isinstance(order, dict):
            return order
        for o in active_orders(revx):
            if venue_id in (o.get("venue_order_id"), o.get("id")):
                print("  (found in active orders, not yet by ID)")
                return o
        time.sleep(1 + attempt)
    return None


def cancel_and_confirm(revx: RevX, venue_id: str) -> int:
    print(f"  Cancelling {venue_id}")
    result = revx.request("DELETE", f"/api/1.0/orders/{venue_id}", allow=(404,))
    print(f"  cancel response: {json.dumps(result)[:300] if result is not None else '(empty or 404)'}")
    for attempt in range(6):
        time.sleep(1 + attempt)
        order = unwrap(revx.request("GET", f"/api/1.0/orders/{venue_id}", allow=(404,)))
        still_active = any(venue_id in (o.get("venue_order_id"), o.get("id")) for o in active_orders(revx))
        status = (order.get("status") or order.get("state")) if isinstance(order, dict) else None
        print(f"  status: {status}; in active list: {still_active}")
        if not still_active:
            print("  Cancel confirmed: the order is no longer active.")
            return 0
    print("  Order still active. Cancel it in the Revolut X app and note this in issue #1.")
    return 1


def dec(value) -> Decimal:
    return Decimal(str(value)) if value not in (None, "") else Decimal(0)


def plain(value: Decimal) -> str:
    return format(value.normalize(), "f")


def now_ms() -> int:
    return int(time.time() * 1000)


def find_pair(revx: RevX, symbol: str) -> dict | None:
    pairs = revx.request("GET", "/api/1.0/configuration/pairs")
    base_ccy, quote_ccy = symbol.split("-")
    items = pairs.values() if isinstance(pairs, dict) else pairs or []
    return next((p for p in items if isinstance(p, dict) and p.get("base") == base_ccy and p.get("quote") == quote_ccy), None)


def available(revx: RevX, currency: str) -> Decimal:
    rows = unwrap(revx.request("GET", "/api/1.0/balances")) or []
    row = next((b for b in rows if isinstance(b, dict) and b.get("currency") == currency), None)
    return dec((row or {}).get("available"))


def signed_top(revx: RevX, symbol: str) -> tuple[Decimal, Decimal, int | None]:
    """Best bid, best ask and metadata.timestamp from the signed order book, as the live engine reads it."""
    book = revx.request("GET", f"/api/1.0/order-book/{symbol}", params={"limit": 5})
    data = book.get("data", {}) if isinstance(book, dict) else {}
    bid = max(dec(level["price"]) for level in data["bids"])
    ask = min(dec(level["price"]) for level in data["asks"])
    return bid, ask, (book.get("metadata") or {}).get("timestamp")


def tickers_check(symbols: list[str], revx: RevX) -> int:
    """Signed tickers as the engine requests them, then the timestamps of tickers and books against this clock."""
    print("A. Signed tickers: which form of the symbols filter works")
    forms = [
        ("dash, literal comma (what the engine sends)", ",".join(symbols)),
        ("slash, literal comma", ",".join(s.replace("-", "/") for s in symbols)),
        ("dash, encoded comma", "%2C".join(symbols)),
    ]
    engine_form_ok = False
    for i, (name, value) in enumerate(forms):
        r = revx.raw("GET", "/api/1.0/tickers", f"symbols={value}")
        print(f"  {name}: HTTP {r.status_code}")
        if r.ok:
            body = r.json()
            rows = body.get("data", []) if isinstance(body, dict) else []
            got = sorted(str(row.get("symbol")) for row in rows if isinstance(row, dict))
            print(f"    returned {len(got)} rows: {', '.join(got)}")
            for row in rows[:2]:
                print(f"    {json.dumps(row)[:300]}")
            print(f"    metadata: {json.dumps(body.get('metadata'))}")
            if i == 0:
                engine_form_ok = len(got) == len(symbols)
        else:
            print(f"    body: {r.text[:300]}")
        if i == 0 and engine_form_ok:
            break
        time.sleep(0.3)

    print("B. Timestamps: ticker vs signed order book vs this computer's clock, 5 rounds 2 s apart")
    print("   (a timestamp that tracks the request time means 'response time'; one that lags and moves in steps")
    print("    means 'snapshot time'; this computer's clock may be off by a few hundred ms)")
    path_query = f"symbols={','.join(symbols) if engine_form_ok else symbols[0]}"
    for _ in range(5):
        sent = now_ms()
        r = revx.raw("GET", "/api/1.0/tickers", path_query)
        got = now_ms()
        t_ts = (r.json().get("metadata") or {}).get("timestamp") if r.ok else None
        sent_b = now_ms()
        b = revx.raw("GET", f"/api/1.0/order-book/{symbols[0]}", "limit=5")
        got_b = now_ms()
        b_ts = (b.json().get("metadata") or {}).get("timestamp") if b.ok else None
        t_line = f"ticker ts - sent {int(t_ts) - sent:+d} ms, received - ts {got - int(t_ts):+d} ms" if t_ts else f"ticker HTTP {r.status_code}"
        b_line = f"book ts - sent {int(b_ts) - sent_b:+d} ms, received - ts {got_b - int(b_ts):+d} ms" if b_ts else f"book HTTP {b.status_code}"
        print(f"  {t_line} | {b_line}")
        time.sleep(2)

    print(f"RESULT tickers: engine's form {'ACCEPTED' if engine_form_ok else 'NOT accepted, see the other forms above'}")
    print("Paste everything above into the project thread.")
    return 0 if engine_form_ok else 1


def place_limit(revx: RevX, symbol: str, side: str, size: Decimal, price: Decimal, ioc: bool) -> str | None:
    config = {"base_size": plain(size), "price": plain(price)}
    if ioc:
        config["time_in_force"] = "ioc"
    order = revx.request("POST", "/api/1.0/orders", body={
        "client_order_id": str(uuid.uuid4()),
        "symbol": symbol,
        "side": side,
        "order_configuration": {"limit": config},
    })
    print(f"  response: {json.dumps(order)[:400]}")
    data = (order.get("data") or {}) if isinstance(order, dict) else {}
    return data.get("venue_order_id") or data.get("id")


def settle(revx: RevX, venue_id: str) -> tuple[dict | None, bool]:
    """The order as the exchange reports it a moment after placing, and whether it is still in the active list."""
    time.sleep(1)
    order = get_order(revx, venue_id)
    resting = any(venue_id in (o.get("venue_order_id"), o.get("id")) for o in active_orders(revx))
    status = (order.get("status") or order.get("state")) if order else None
    print(f"  status: {status}; filled {order.get('filled_quantity') if order else '?'} "
          f"for {order.get('filled_amount') if order else '?'}; still active: {resting}")
    return order, resting


def ioc_check(symbol: str, quote_eur: Decimal, revx: RevX) -> int:
    base_ccy, quote_ccy = symbol.split("-")
    pair = find_pair(revx, symbol)
    if not pair:
        print(f"  {symbol} not found in the pair configuration.")
        return 1
    base_step, quote_step = dec(pair["base_step"]), dec(pair["quote_step"])
    min_quote = dec(pair.get("min_order_size_quote"))
    if quote_eur < min_quote:
        print(f"  {quote_eur} {quote_ccy} is below the pair's minimum order of {min_quote} {quote_ccy}.")
        return 1

    bid, ask, _ = signed_top(revx, symbol)
    print(f"  {symbol}: bid {bid}, ask {ask}")
    cash = available(revx, quote_ccy)
    if cash < quote_eur * Decimal("1.05"):
        print(f"  Not enough {quote_ccy} available: {cash}, need about {quote_eur * Decimal('1.05'):.2f}.")
        return 1

    print(f"C. IOC buy 20% below the market: must not fill and must not stay open (costs nothing)")
    price = step_round(bid * Decimal("0.8"), quote_step)
    size = step_round(quote_eur / price, base_step)
    print(f"  limit BUY {plain(size)} {base_ccy} at {plain(price)} {quote_ccy}, time_in_force ioc")
    venue_id = place_limit(revx, symbol, "BUY", size, price, ioc=True)
    if not venue_id:
        print("  The exchange returned no order ID. If it refused time_in_force, the body above says why.")
        return 1
    order, resting = settle(revx, venue_id)
    if resting:
        print("RESULT ioc-far: NOT HONOURED, the order is resting. Cancelling it now.")
        cancel_and_confirm(revx, venue_id)
        return 1
    filled = dec(order.get("filled_quantity")) if order else Decimal(0)
    print(f"RESULT ioc-far: {'HONOURED' if filled == 0 else 'FILLED UNEXPECTEDLY'}: not resting, filled {filled}")

    print(f"D. Round trip of about {quote_eur} {quote_ccy}: IOC buy at the ask, then IOC sell of the same coins")
    print(f"   priced 3% under the mid, as the engine's stop-loss sells. Costs the spread and fees on {quote_eur} {quote_ccy}.")
    answer = input("  Type yes to run it (anything else skips): ").strip().lower()
    if answer != "yes":
        print(f"  Skipped (you typed {answer!r}).")
        return 0

    bid, ask, _ = signed_top(revx, symbol)
    buy_price = step_round(ask * Decimal("1.005"), quote_step)
    buy_size = step_round(quote_eur / ask, base_step)
    print(f"  limit BUY {plain(buy_size)} {base_ccy} at {plain(buy_price)} {quote_ccy} (ask {ask}), time_in_force ioc")
    venue_id = place_limit(revx, symbol, "BUY", buy_size, buy_price, ioc=True)
    if not venue_id:
        return 1
    order, resting = settle(revx, venue_id)
    if resting:
        print("  The buy is resting: IOC not honoured. Cancelling it.")
        cancel_and_confirm(revx, venue_id)
    bought = dec(order.get("filled_quantity")) if order else Decimal(0)
    if bought <= 0:
        print("RESULT ioc-buy: nothing filled at the ask; re-run when the book is calmer.")
        return 1

    time.sleep(1)
    coins = available(revx, base_ccy)
    sell_size = step_round(min(bought, coins), base_step)
    bid, ask, _ = signed_top(revx, symbol)
    mid = (bid + ask) / 2
    floor = step_round(mid * Decimal("0.97"), quote_step)
    print(f"  bought {bought}, available {coins}; selling {plain(sell_size)} at limit {plain(floor)} (mid {mid}, bid {bid})")
    sent = now_ms()
    venue_id = place_limit(revx, symbol, "SELL", sell_size, floor, ioc=True)
    answered = now_ms()
    if not venue_id:
        print("RESULT ioc-sell: REFUSED, see the response above. Sell the coins in the app.")
        return 1
    order, resting = settle(revx, venue_id)
    if resting:
        print("RESULT ioc-sell: NOT HONOURED, the sell is resting. Cancelling it; sell the rest in the app.")
        cancel_and_confirm(revx, venue_id)
        return 1
    sold, proceeds = (dec(order.get("filled_quantity")), dec(order.get("filled_amount"))) if order else (Decimal(0), Decimal(0))
    if sold <= 0:
        print("RESULT ioc-sell: nothing filled, the coins are still held; sell them in the app.")
        return 1
    average = proceeds / sold
    print(f"RESULT ioc-sell: HONOURED, sold {sold} of {plain(sell_size)} at an average {average:.6f}; "
          f"vs bid {(average - bid) / bid:+.4%}, vs limit {(average - floor) / floor:+.4%}; "
          f"placement answered in {answered - sent} ms")
    print("Paste everything above into the project thread.")
    return 0


def public_check(symbol: str) -> int:
    """Calls the unauthenticated endpoints the engine uses and prints raw responses (issue #3)."""
    now_ms = int(time.time() * 1000)
    calls = [
        ("order book", f"/api/2.0/public/order-book/{symbol}", {"limit": 3, "region": "EEA"}),
        ("tickers", "/api/1.0/public/tickers", {"symbols": symbol, "region": "EEA"}),
        ("candles 1m", f"/api/1.0/public/candles/{symbol}", {"interval": 1, "since": now_ms - 5 * 60_000, "region": "EEA"}),
        ("pairs", "/api/1.0/public/configuration/pairs", {"region": "EEA"}),
    ]
    for name, path, params in calls:
        r = requests.get(BASE + path, params=params, headers={"Accept": "application/json"}, timeout=15)
        print(f"{name}: GET {path} -> HTTP {r.status_code}, Retry-After={r.headers.get('Retry-After')}")
        text = r.text
        print("  " + (text[:700] + ("..." if len(text) > 700 else "")))
        time.sleep(1.2)  # public endpoints allow 1 request per second
    paging_check(symbol, now_ms)
    print("Paste this output into issue #3.")
    return 0


def paging_check(symbol: str, now_ms: int) -> None:
    """Asks for 24h of 1m candles to see how a long range is paged: the engine assumes oldest first, starting at `since`."""
    since = now_ms - 24 * 60 * 60_000
    path = f"/api/1.0/public/candles/{symbol}"
    r = requests.get(BASE + path, params={"interval": 1, "since": since, "region": "EEA"},
                     headers={"Accept": "application/json"}, timeout=15)
    print(f"candles paging: GET {path} (24h of 1m) -> HTTP {r.status_code}")
    rows = r.json().get("data", []) if r.ok else []
    if not rows:
        print("  no candles returned")
        return
    starts = [int(c["start"]) for c in rows]
    ascending = all(a < b for a, b in zip(starts, starts[1:]))
    print(f"  count={len(rows)} ascending={ascending}")
    print(f"  first start - since = {(starts[0] - since) / 60_000:.1f} min, now - last start = {(now_ms - starts[-1]) / 60_000:.1f} min")
    print("  oldest-first from since" if ascending and starts[0] - since < 120_000 else "  NOT oldest-first from since: report this")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--symbol", help="pair to check (default BTC-EUR; DOGE-EUR for --ioc)")
    ap.add_argument("--place-and-cancel", action="store_true",
                    help="place a tiny post-only limit buy 20%% below market, then cancel it")
    ap.add_argument("--list-active", action="store_true", help="list active orders and exit")
    ap.add_argument("--public", action="store_true", help="check public market-data endpoints (no keys) and exit")
    ap.add_argument("--cancel", metavar="VENUE_ORDER_ID", help="cancel one order by ID, confirm, and exit")
    ap.add_argument("--tickers", action="store_true",
                    help="signed tickers filter and timestamps (read-only); --symbols picks the pairs")
    ap.add_argument("--symbols", default="BTC-EUR,ETH-EUR,HYPE-EUR,INJ-EUR",
                    help="pairs for --tickers, comma-separated")
    ap.add_argument("--ioc", action="store_true",
                    help="immediate-or-cancel checks; --symbol defaults to DOGE-EUR here, a pair the live engine doesn't trade")
    ap.add_argument("--quote", default="2", help="size of the --ioc round trip in the quote currency (default 2)")
    args = ap.parse_args()
    if args.ioc and args.tickers:
        print("Run --tickers and --ioc one at a time.")
        return 2
    if not args.ioc:
        args.symbol = args.symbol or "BTC-EUR"
    if args.public:
        return public_check(args.symbol)

    api_key = os.environ.get("REVX_API_KEY")
    key_path = os.environ.get("REVX_PRIVATE_KEY_PATH")
    if not api_key or not key_path:
        print("Set REVX_API_KEY and REVX_PRIVATE_KEY_PATH first (see the docstring).")
        return 2
    revx = RevX(api_key, key_path)

    if args.tickers:
        return tickers_check([x.strip() for x in args.symbols.split(",") if x.strip()], revx)
    if args.ioc:
        return ioc_check(args.symbol or "DOGE-EUR", Decimal(args.quote), revx)

    if args.list_active or args.cancel:
        print("Active orders")
        for o in active_orders(revx):
            print(f"  {json.dumps(o)[:300]}")
        if args.cancel:
            print(f"Lookup {args.cancel}")
            order = get_order(revx, args.cancel, attempts=2)
            print(f"  order: {json.dumps(order)[:500] if order else 'not found by ID or in active list'}")
            return cancel_and_confirm(revx, args.cancel)
        return 0

    print("1. Balances")
    balances = revx.request("GET", "/api/1.0/balances")
    for b in balances if isinstance(balances, list) else []:
        if Decimal(str(b.get("total", "0"))) > 0:
            print(f"  {b['currency']}: total {b['total']}, available {b['available']}")

    print("2. Pair configuration")
    pairs = revx.request("GET", "/api/1.0/configuration/pairs")
    base_ccy, quote_ccy = args.symbol.split("-")
    pair = None
    items = pairs.values() if isinstance(pairs, dict) else pairs
    for p in items:
        if isinstance(p, dict) and p.get("base") == base_ccy and p.get("quote") == quote_ccy:
            pair = p
    print(f"  {args.symbol}: {json.dumps(pair) if pair else 'NOT FOUND - check symbol format'}")

    print("3. Candles (last 3 hours, 60-minute interval)")
    now_ms = int(time.time() * 1000)
    candles = revx.request("GET", f"/api/1.0/candles/{args.symbol}",
                           params={"interval": 60, "since": now_ms - 3 * 3600 * 1000})
    data = candles.get("data", []) if isinstance(candles, dict) else []
    for c in data[-3:]:
        print(f"  start {c.get('start')}: close {c.get('close')}")

    if not args.place_and_cancel:
        print("Read-only checks done. Re-run with --place-and-cancel to test trading.")
        return 0
    if not pair or not data:
        print("Cannot place a test order without pair configuration and a recent price.")
        return 1

    print("4. Place and cancel a test order")
    last = Decimal(str(data[-1]["close"]))
    price = step_round(last * Decimal("0.8"), Decimal(str(pair["quote_step"])))
    size = Decimal(str(pair["min_order_size"]))
    min_quote = Decimal(str(pair.get("min_order_size_quote") or "0"))
    if size * price < min_quote:
        size = step_round(min_quote / price + Decimal(str(pair["base_step"])), Decimal(str(pair["base_step"])))
    print(f"  limit BUY {size} {base_ccy} at {price} {quote_ccy} (about {size * price:.2f} {quote_ccy}, 20% below market)")

    rows = balances.get("data", []) if isinstance(balances, dict) else balances or []
    quote_row = next((b for b in rows if isinstance(b, dict) and b.get("currency") == quote_ccy), None)
    available_quote = Decimal(str((quote_row or {}).get("available") or "0"))
    needed = size * price
    if available_quote < needed:
        where = "no balance row" if quote_row is None else f"available {available_quote}"
        print(f"  Not enough {quote_ccy}: {where}, need {needed:.2f} (short by {needed - available_quote:.2f}).")
        print(f"  Move a little {quote_ccy} into this account (or sell some crypto to {quote_ccy}), then re-run.")
        return 1

    answer = input("  Type yes to place it (anything else skips): ").strip().lower()
    if answer != "yes":
        print(f"  Skipped (you typed {answer!r}).")
        return 0

    order = revx.request("POST", "/api/1.0/orders", body={
        "client_order_id": str(uuid.uuid4()),
        "symbol": args.symbol,
        "side": "BUY",
        "order_configuration": {"limit": {
            "base_size": str(size),
            "price": str(price),
            "execution_instructions": ["post_only"],
        }},
    })
    print(f"  response: {json.dumps(order)[:500]}")
    venue_id = (order.get("data") or {}).get("venue_order_id") or (order.get("data") or {}).get("id") \
        if isinstance(order, dict) else None
    if not venue_id:
        print("  Could not find the venue order ID in the response; cancel it in the web app and record the shape.")
        return 1

    try:
        order = get_order(revx, venue_id)
        print(f"  order: {json.dumps(order)[:500] if order else 'not visible yet by ID or in active list'}")
    finally:
        # Always attempt the cancel, even if the lookup failed, so no test order is left open.
        code = cancel_and_confirm(revx, venue_id)
    print("Done. Record the results in issue #1.")
    return code


if __name__ == "__main__":
    sys.exit(main())
