#!/usr/bin/env python3
"""Staging panel setup through the Remnawave API (docs/staging.md, step 6). Runs on the panel VPS.

Creates the Xray config profile from infra/xray/config-profile.example.json with fresh Reality keys, the squads
basic and premium, the node on this same VPS with its two hosts, and the subscription settings (HWID limit,
stubs, headers with the Happ routing profile); then writes the node SECRET_KEY into the node's Ansible host_vars.
Authenticates with a temporary full-access API token read from a file. Prints only names, UUIDs and statuses:
the Reality private key and the node SECRET_KEY never leave the server.

    python3 staging-panel-setup.py --token-file /opt/remnawave/panel/setup.token --public-ip <node IP> \
        --sni <site> --support-url https://t.me/<bot> --host-vars /root/mendeleev-ansible/host_vars/node-stg1.yml
    python3 staging-panel-setup.py --token-file … status

Steps (positional, default: profile subscription node-key): profile, subscription, node-key, status; and sni,
which moves the Reality inbounds of an existing profile to another site (--sni) when the current one stops
answering the node: Reality takes every handshake from that site, so a site that blocks the node breaks all of
them.
"""
import argparse
import base64
import json
import os
import secrets
import subprocess
import sys
import urllib.error
import urllib.request

INFRA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
STEPS = ("profile", "subscription", "node-key", "sni", "status")

parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument("steps", nargs="*", help=f"any of {', '.join(STEPS)}")
parser.add_argument("--api", default="http://127.0.0.1:3000/api",
                    help="the panel API through Caddy's loopback listener, which adds the reverse-proxy headers")
parser.add_argument("--token-file", required=True, help="a full-access API token, one line")
parser.add_argument("--public-ip", help="the address clients connect to (step profile)")
parser.add_argument("--sni", help="Reality target: a real site with TLS 1.3 and HTTP/2 in the node's country")
parser.add_argument("--node-name", default="STG1", help="also the host tag that replace-node.sh looks for")
parser.add_argument("--remark", help="host name shown in Happ (default: the node name); the XHTTP host adds ' XHTTP'")
parser.add_argument("--node-address", default="172.17.0.1", help="the node as the panel container sees it")
parser.add_argument("--node-port", type=int, default=2222)
parser.add_argument("--country", default="FR")
parser.add_argument("--profile-name", default="stg")
parser.add_argument("--profile-title", default="Mendeleev staging")
parser.add_argument("--support-url", help="support-url header (step subscription)")
parser.add_argument("--device-limit", type=int, default=3, help="HWID limit for users without their own")
parser.add_argument("--template", default=os.path.join(INFRA, "xray", "config-profile.example.json"))
parser.add_argument("--routing", default=os.path.join(INFRA, "routing", "happ-routing.json"))
parser.add_argument("--host-vars", help="Ansible host_vars of the node (step node-key)")
args = parser.parse_args()
steps = args.steps or ["profile", "subscription", "node-key"]
if unknown := [s for s in steps if s not in STEPS]:
    parser.error(f"unknown steps {unknown}; known: {', '.join(STEPS)}")

TOKEN = open(args.token_file).read().strip()


def call(method, path, body=None):
    request = urllib.request.Request(
        args.api.rstrip("/") + path,
        method=method,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Authorization": f"Bearer {TOKEN}", "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)["response"]
    except urllib.error.HTTPError as error:
        sys.exit(f"{method} {path}: HTTP {error.code} {error.read().decode()[:600]}")


def require(*names):
    if missing := [n for n in names if not getattr(args, n.replace("-", "_"))]:
        parser.error(f"steps {steps} need --{', --'.join(missing)}")


if "profile" in steps:
    require("public-ip", "sni")
    if any(p["name"] == args.profile_name for p in call("GET", "/config-profiles")["configProfiles"]):
        sys.exit(f"config profile '{args.profile_name}' already exists: nothing created")

    # The panel generates the Reality key pair itself; the private key stays in this process.
    private_key = call("GET", "/system/tools/x25519/generate")["keypairs"][0]["privateKey"]
    short_id = secrets.token_hex(8)
    config = json.load(open(args.template))
    for inbound in config["inbounds"]:
        reality = inbound["streamSettings"]["realitySettings"]
        reality.update(privateKey=private_key, shortIds=[short_id], target=f"{args.sni}:443", serverNames=[args.sni])

    profile = call("POST", "/config-profiles", {"name": args.profile_name, "config": config})
    inbounds = {i["tag"]: i["uuid"] for i in call("GET", f"/config-profiles/{profile['uuid']}/inbounds")["inbounds"]}
    print(f"profile {args.profile_name}: {profile['uuid']}, inbounds {sorted(inbounds)}")

    for name in ("basic", "premium"):
        squad = call("POST", "/internal-squads", {"name": name, "inbounds": list(inbounds.values())})
        print(f"squad {name}: {squad['uuid']}  -> Remnawave__Squads__{name}")

    node = call("POST", "/nodes", {
        "name": args.node_name,
        "address": args.node_address,
        "port": args.node_port,
        "countryCode": args.country,
        "configProfile": {"activeConfigProfileUuid": profile["uuid"], "activeInbounds": list(inbounds.values())},
    })
    print(f"node {args.node_name}: {node['uuid']}")

    remark = args.remark or args.node_name
    for tag, port, suffix in (("VLESS_REALITY", 443, ""), ("VLESS_XHTTP", 8443, " XHTTP")):
        host = call("POST", "/hosts", {
            "inbound": {"configProfileUuid": profile["uuid"], "configProfileInboundUuid": inbounds[tag]},
            "remark": f"{remark}{suffix}",
            "address": args.public_ip,
            "port": port,
            "tags": [args.node_name],
        })
        print(f"host {remark}{suffix}: {host['uuid']} ({args.public_ip}:{port})")

if "subscription" in steps:
    require("support-url")
    settings = call("GET", "/subscription-settings")
    routing = json.load(open(args.routing))
    # As infra/routing/build-routing.sh: compact JSON, base64, the happ://routing/add/ prefix.
    routing_header = "happ://routing/add/" + base64.b64encode(
        json.dumps(routing, ensure_ascii=False, separators=(",", ":")).encode()).decode()
    updated = call("PATCH", "/subscription-settings", {
        "uuid": settings["uuid"],
        "hwidSettings": {"enabled": True, "fallbackDeviceLimit": args.device_limit, "maxDevicesAnnounce": None},
        "isShowCustomRemarks": True,
        "customRemarks": {
            "expiredUsers": ["Подписка истекла — продлите в боте"],
            "limitedUsers": ["Трафик закончился — продлите в боте"],
            "disabledUsers": ["Доступ отключён — напишите в поддержку"],
            "emptyHosts": ["Нет доступных серверов"],
            "HWIDMaxDevicesExceeded": ["Превышен лимит устройств — сбросьте их в боте"],
            "HWIDNotSupported": ["Приложение не поддерживается — установите Happ"],
        },
        "customResponseHeaders": {
            "profile-title": args.profile_title,
            "profile-update-interval": "2",
            "support-url": args.support_url,
            "routing": routing_header,
        },
    })
    print(f"subscription settings: HWID {updated['hwidSettings']}, headers {sorted(updated['customResponseHeaders'])}")

if "node-key" in steps:
    require("host-vars")
    secret_key = call("GET", "/keygen")["secretKey"]
    try:
        current = open(args.host_vars).read()
    except PermissionError:
        current = subprocess.run(["sudo", "cat", args.host_vars], capture_output=True, text=True, check=True).stdout
    lines = [f'remnawave_node_secret_key: "{secret_key}"' if line.startswith("remnawave_node_secret_key:") else line
             for line in current.splitlines()]
    if lines == current.splitlines():
        sys.exit(f"no remnawave_node_secret_key line in {args.host_vars}")
    content = "\n".join(lines) + "\n"
    try:
        with open(args.host_vars, "w") as f:
            f.write(content)
    except PermissionError:
        subprocess.run(["sudo", "tee", args.host_vars], input=content, text=True, check=True, stdout=subprocess.DEVNULL)
    print(f"node SECRET_KEY written to {args.host_vars} ({len(secret_key)} chars); now run the node playbook again")

if "sni" in steps:
    require("sni")
    profile = next((p for p in call("GET", "/config-profiles")["configProfiles"] if p["name"] == args.profile_name), None)
    if profile is None:
        sys.exit(f"no config profile '{args.profile_name}'")
    config = profile["config"]
    moved = 0
    for inbound in config["inbounds"]:
        reality = inbound.get("streamSettings", {}).get("realitySettings")
        if reality is not None:
            reality.update(target=f"{args.sni}:443", serverNames=[args.sni])
            moved += 1
    call("PATCH", "/config-profiles", {"uuid": profile["uuid"], "config": config})
    print(f"profile {args.profile_name}: {moved} Reality inbound(s) now use {args.sni}; clients get it with the next "
          f"subscription update")

if "status" in steps:
    for node in call("GET", "/nodes"):
        print(f"node {node['name']}: {node['address']}:{node.get('port')}, connected={node.get('isConnected')}, "
              f"disabled={node.get('isDisabled')}")
    for host in call("GET", "/hosts"):
        print(f"host {host['remark']}: {host['address']}:{host['port']}, tags={host.get('tags')}, "
              f"disabled={host.get('isDisabled')}")
    for squad in call("GET", "/internal-squads")["internalSquads"]:
        print(f"squad {squad['name']}: {squad['uuid']}")
