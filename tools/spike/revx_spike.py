#!/usr/bin/env python3
"""Revolut X API spike for CIX (issue #1).

Checks, in order:
  1. Authenticated read: balances.
  2. Trading pairs and step sizes for the target symbol.
  3. Recent candles.
  4. Optional (--place-and-cancel): place a tiny post-only limit buy far below the market,
     read it back, cancel it, and confirm it is cancelled.

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

    def request(self, method: str, path: str, params: dict | None = None, body: dict | None = None,
                allow: tuple[int, ...] = ()):
        query = "&".join(f"{k}={v}" for k, v in (params or {}).items())
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
        r = requests.request(method, url, headers=headers, data=body_text or None, timeout=15)
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


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--symbol", default="BTC-EUR")
    ap.add_argument("--place-and-cancel", action="store_true",
                    help="place a tiny post-only limit buy 20%% below market, then cancel it")
    ap.add_argument("--list-active", action="store_true", help="list active orders and exit")
    ap.add_argument("--cancel", metavar="VENUE_ORDER_ID", help="cancel one order by ID, confirm, and exit")
    args = ap.parse_args()

    api_key = os.environ.get("REVX_API_KEY")
    key_path = os.environ.get("REVX_PRIVATE_KEY_PATH")
    if not api_key or not key_path:
        print("Set REVX_API_KEY and REVX_PRIVATE_KEY_PATH first (see the docstring).")
        return 2
    revx = RevX(api_key, key_path)

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
