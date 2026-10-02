#!/usr/bin/env python3
"""TCP reachability of addresses from Russian networks via Globalping probes (FR-NODE-02; docs/staging.md).

Checks a node hoster before buying and after: whether its IP opens from Russia. Probes are mostly in data
centers, so «reachable» means the address is not blocked wholesale; home and mobile providers filter harder —
test those from real home and mobile networks too. A fresh server listens only on SSH: use --port 22. Open from
--country DE but not from RU means the address is blocked on the Russian side, not by the hoster.

    python3 ru-reachability.py 203.0.113.10 198.51.100.7 --port 443
    python3 ru-reachability.py 203.0.113.10 --port 22 --country DE --limit 5
"""
import argparse
import json
import time
import urllib.request

API = "https://api.globalping.io/v1/measurements"

parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument("targets", nargs="+", help="IP addresses or host names")
parser.add_argument("--port", type=int, default=443)
parser.add_argument("--country", default="RU")
parser.add_argument("--limit", type=int, default=12, help="number of probes")
args = parser.parse_args()


def request(url, body=None):
    req = urllib.request.Request(url, data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"}, method="POST" if body else "GET")
    with urllib.request.urlopen(req, timeout=30) as response:
        return json.load(response)


ids = {target: request(API, {
    "type": "ping", "target": target,
    "locations": [{"country": args.country, "limit": args.limit}],
    "measurementOptions": {"protocol": "TCP", "port": args.port, "packets": 4},
})["id"] for target in args.targets}

for target, measurement_id in ids.items():
    for _ in range(40):
        result = request(f"{API}/{measurement_id}")
        if result["status"] == "finished":
            break
        time.sleep(3)
    reachable = 0
    print(f"== {target}:{args.port} TCP from {args.country}, {len(result['results'])} probes")
    for item in result["results"]:
        probe, stats = item["probe"], item["result"].get("stats") or {}
        ok = stats.get("loss") is not None and stats["loss"] < 100
        reachable += ok
        avg = f"{stats['avg']:.0f} ms" if stats.get("avg") else "-"
        print(f"   {probe.get('city', '')[:16]:16} {(probe.get('network') or '')[:30]:30} "
              f"{'OK  ' if ok else 'FAIL'} loss={stats.get('loss')}% avg={avg}")
    print(f"   reachable from {reachable} of {len(result['results'])}")
