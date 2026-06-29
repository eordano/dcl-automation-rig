#!/usr/bin/env python3
import json
import os
import re
import sys
import time

import requests
from eth_account import Account
from eth_account.messages import encode_defunct

API = "https://auth-api.decentraland.org"
URL_LOG = os.environ.get("URL_LOG", os.path.expanduser("~/.dcl-rig/urls.log"))
WALLET_FILE = os.environ.get("WALLET_FILE", os.path.expanduser("~/.dcl-rig/throwaway-wallet.json"))
URL_PATTERN = re.compile(r"https://decentraland\.org/auth/requests/([a-f0-9-]{36})")


def get_or_make_wallet():
    if os.path.exists(WALLET_FILE):
        with open(WALLET_FILE) as f:
            data = json.load(f)
        acct = Account.from_key(data["privateKey"])
        print(f"[wallet] reused: {acct.address}")
        return acct
    acct = Account.create()
    with open(WALLET_FILE, "w") as f:
        json.dump({"address": acct.address, "privateKey": acct.key.hex()}, f)
    print(f"[wallet] CREATED throwaway: {acct.address}")
    print(f"[wallet] private key persisted at {WALLET_FILE}")
    return acct


def watch_for_request_id(timeout=120):
    print(f"[watch] tailing {URL_LOG} for {timeout}s...")
    start = time.time()
    seen = set()
    if os.path.exists(URL_LOG):
        with open(URL_LOG) as f:
            for line in f:
                seen.add(line.rstrip())
    while time.time() - start < timeout:
        if os.path.exists(URL_LOG):
            with open(URL_LOG) as f:
                for line in f:
                    line = line.rstrip()
                    if line in seen:
                        continue
                    seen.add(line)
                    print(f"[watch] new line: {line}")
                    m = URL_PATTERN.search(line)
                    if m:
                        return m.group(1)
        time.sleep(0.5)
    raise RuntimeError("timed out waiting for auth URL")


def fetch_request(request_id):
    url = f"{API}/v2/requests/{request_id}"
    r = requests.get(url, timeout=15)
    r.raise_for_status()
    body = r.json()
    print(f"[fetch] {url}\n  -> method={body['method']}\n  -> params[0]={body['params'][0][:120]!r}")
    return body


def sign_personal(message_text, account):
    msg = encode_defunct(text=message_text)
    return account.sign_message(msg).signature.hex()


def post_outcome(request_id, sender, signature_hex):
    url = f"{API}/v2/requests/{request_id}/outcome"
    payload = {
        "sender": sender,
        "result": signature_hex if signature_hex.startswith("0x") else "0x" + signature_hex,
    }
    print(f"[post] {url}\n  body: {payload}")
    r = requests.post(url, json=payload, timeout=15)
    print(f"  -> {r.status_code} {r.text[:300]}")
    r.raise_for_status()


def main():
    acct = get_or_make_wallet()
    request_id = sys.argv[1] if len(sys.argv) > 1 else watch_for_request_id()
    print(f"[id] {request_id}")
    body = fetch_request(request_id)
    if body["method"] != "dcl_personal_sign":
        print(f"[warn] unexpected method {body['method']}; signing params[0] as personal anyway")
    message = body["params"][0]
    print(f"[sign] message ({len(message)} chars):\n{message}")
    sig = sign_personal(message, acct)
    print(f"[sign] signature: 0x{sig}")
    post_outcome(request_id, acct.address, sig)
    print("[done] signature submitted")


if __name__ == "__main__":
    main()
