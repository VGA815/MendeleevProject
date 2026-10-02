#!/usr/bin/env python3
"""Connects through every host of a subscription with a real Xray client (docs/staging.md, step 12).

Fetches the subscription as Happ does, starts an Xray client per host from the Remnawave node image and reports
the exit IP and the speed of a download through it. Run on a Linux server with Docker, e.g. the management VPS:
a Russian DC shows whether the node and the transports work from Russia at all.

    python3 transport-check.py /tmp/sub-url

The subscription link is the user's secret: keep it in a file with mode 600 and delete it afterwards. The script
prints only transports, ports and results. With the HWID limit on, the request registers a device named by
--hwid on that user: delete it afterwards (the command is printed at the end).
"""
import argparse
import base64
import json
import os
import shutil
import subprocess
import tempfile
import time
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument("url_file", help="file with the subscription link https://<sub domain>/<shortUuid>")
parser.add_argument("--image", default="remnawave/node:3.4.1", help="any image with xray in PATH")
parser.add_argument("--hwid", default="transport-check", help="device id sent as x-hwid")
parser.add_argument("--bytes", type=int, default=5_000_000, help="size of the test download")
args = parser.parse_args()

url = open(args.url_file).read().strip()
request = urllib.request.Request(url, headers={
    # Without x-hwid the panel answers with the stub «Приложение не поддерживается» instead of the hosts.
    "User-Agent": "Happ/3.10.0", "x-hwid": args.hwid, "x-device-os": "Linux", "x-ver-os": "6",
    "x-device-model": "transport-check",
})
body = urllib.request.urlopen(request, timeout=20).read().decode()
links = [line for line in base64.b64decode(body + "==").decode().splitlines() if line.startswith("vless://")]


def outbound(link):
    parsed = urllib.parse.urlparse(link)
    q = {k: v[0] for k, v in urllib.parse.parse_qs(parsed.query).items()}
    user = {"id": parsed.username, "encryption": q.get("encryption", "none")}
    if q.get("flow"):
        user["flow"] = q["flow"]
    stream = {
        "network": q.get("type", "tcp"),
        "security": q.get("security", "none"),
        "realitySettings": {
            "serverName": q.get("sni", ""), "fingerprint": q.get("fp", "chrome"),
            "publicKey": q.get("pbk", ""), "shortId": q.get("sid", ""), "spiderX": q.get("spx", ""),
        },
    }
    if stream["network"] == "xhttp":
        stream["xhttpSettings"] = {"path": q.get("path", "/"), "mode": q.get("mode", "auto")}
        if q.get("host"):
            stream["xhttpSettings"]["host"] = q["host"]
    name = f"{stream['network']}:{parsed.port}" + (f" flow={q['flow']}" if q.get("flow") else "")
    remark = urllib.parse.unquote(parsed.fragment)
    return name, remark, {"protocol": "vless", "settings": {"vnext": [{"address": parsed.hostname, "port": parsed.port,
                                                                          "users": [user]}]},
                          "streamSettings": stream}


workdir = tempfile.mkdtemp(prefix="transport-check-")
try:
    if not links:
        print("no vless:// links in the subscription:", body[:100])
    for index, link in enumerate(links):
        name, remark, ob = outbound(link)
        path = os.path.join(workdir, f"{index}.json")
        with open(path, "w") as f:
            json.dump({"log": {"loglevel": "warning"},
                       "inbounds": [{"listen": "127.0.0.1", "port": 10808 + index, "protocol": "socks"}],
                       "outbounds": [ob]}, f)
        os.chmod(path, 0o600)  # the user's UUID; the container reads it as root
        container = f"transport-check-{index}"
        subprocess.run(["docker", "rm", "-f", container], capture_output=True)
        subprocess.run(["docker", "run", "-d", "--name", container, "--network", "host", "-v", f"{path}:/config.json:ro",
                        "--entrypoint", "xray", args.image, "run", "-c", "/config.json"], capture_output=True, check=True)
        time.sleep(2)
        proxy = f"socks5h://127.0.0.1:{10808 + index}"
        ip = subprocess.run(["curl", "-s", "--max-time", "15", "-x", proxy, "https://ifconfig.me"],
                            capture_output=True, text=True).stdout
        out = subprocess.run(["curl", "-s", "-o", "/dev/null", "--max-time", "40", "-x", proxy,
                              "-w", "%{size_download} %{time_total} %{speed_download}",
                              f"https://speed.cloudflare.com/__down?bytes={args.bytes}"],
                             capture_output=True, text=True).stdout.split()
        size, total, speed = (out + ["0", "0", "0"])[:3]
        print(f"{remark[:20]:20} {name:30} exit IP={ip or '-':16} download {int(float(size)) // 1024} KB "
              f"in {float(total):.1f}s ({float(speed) / 1048576:.1f} MB/s)")
        logs = subprocess.run(["docker", "logs", container], capture_output=True, text=True)
        for line in [l for l in (logs.stdout + logs.stderr).splitlines() if "rror" in l or "failed" in l][-3:]:
            print("   xray:", line[-160:])
        subprocess.run(["docker", "rm", "-f", container], capture_output=True)
finally:
    shutil.rmtree(workdir, ignore_errors=True)

print(f"\nThe check registered the HWID device '{args.hwid}' on this user: delete it (docs/staging.md, step 12).")
