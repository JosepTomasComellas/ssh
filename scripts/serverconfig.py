#!/usr/bin/env python3
"""Ajudant de scripts/server-deploy.sh: genera config.json i known_hosts a partir d'un .env mínim.

Detecta (via Docker) la IP de Traefik a la xarxa del proxy i una subxarxa lliure; la passarel·la d'aquesta subxarxa
(la IP del servidor dins la xarxa de l'app) és el destí SSH per defecte. No imprimeix mai secrets.
"""
from __future__ import annotations

import argparse
import ipaddress
import json
import re
import subprocess
import sys
from pathlib import Path

REQUIRED = ("DOMAIN", "BACKUP_RECIPIENT")
DEFAULTS = {
    "WEBSSH_ROOT": "/docker/ssh",
    "GHCR_IMAGE": "ghcr.io/joseptomascomellas/ssh",
    "PROXY_NETWORK": "traefik",
    "TRAEFIK_CONTAINER": "traefik",
    "TRAEFIK_RESOLVER": "letsencrypt",
    "SSH_PORT": "22",
    "TZ": "Europe/Madrid",
    "REQUIRE_MFA": "true",
}


class ConfigError(Exception):
    pass


def parse_env(text: str) -> dict[str, str]:
    """KEY=VALUE per línia; comentaris amb #; cometes simples/dobles opcionals. Sense expansió ni execució."""
    env: dict[str, str] = {}
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        value = value.strip()
        if value[:1] in ("'", "\""):
            end = value.find(value[0], 1)
            value = value[1:end] if end > 0 else value[1:]
        else:
            value = re.split(r"\s+#", value, maxsplit=1)[0].strip()  # comentari en línia
        env[key.strip()] = value
    return env


def _candidates():
    """172.30.X.0/24 primer (convenció del projecte), després la resta de 172.16/12 i, per últim, 10.250.X.0/24."""
    for second in (30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16):
        for third in range(50, 251):
            yield ipaddress.ip_network(f"172.{second}.{third}.0/24")
    for third in range(0, 256):
        yield ipaddress.ip_network(f"10.250.{third}.0/24")


def pick_subnet(used: list[str]) -> str:
    """Primera subxarxa /24 candidata que no se superposi amb cap xarxa Docker ni ruta del servidor."""
    taken = [ipaddress.ip_network(u, strict=False) for u in used if u]
    taken = [t for t in taken if t.version == 4]
    for candidate in _candidates():
        if not any(candidate.overlaps(t) for t in taken):
            return str(candidate)
    raise ConfigError("No hi ha cap subxarxa lliure; defineix BACKEND_SUBNET al .env")

def gateway(subnet: str) -> str:
    return str(next(ipaddress.ip_network(subnet).hosts()))


def build_config(env: dict[str, str], traefik_ip: str, subnet: str) -> dict:
    merged = {**DEFAULTS, **{k: v for k, v in env.items() if v != ""}}
    missing = [k for k in REQUIRED if not merged.get(k)]
    if missing:
        raise ConfigError("Falten al .env: " + ", ".join(missing))
    require_mfa = merged["REQUIRE_MFA"].lower()
    if require_mfa not in ("true", "false"):
        raise ConfigError("REQUIRE_MFA ha de ser true o false")
    return {
        "domain": merged["DOMAIN"],
        "proxy": "traefik",
        "http_port": 18080,  # no s'usa amb Traefik; el contracte de manage.py el demana
        "proxy_network": merged["PROXY_NETWORK"],
        "traefik_resolver": merged["TRAEFIK_RESOLVER"],
        "trusted_proxies": [traefik_ip],
        "backend_subnet": subnet,
        "backup_recipient": merged["BACKUP_RECIPIENT"],
        "verify_signed_tags": False,
        "ssh_host": merged.get("SSH_HOST") or gateway(subnet),
        "ssh_port": int(merged["SSH_PORT"]),
        "timezone": merged["TZ"],
        "require_mfa": require_mfa == "true",
    }


def known_hosts_line(host: str, port: int, pubkey_text: str) -> str:
    parts = pubkey_text.split()
    if len(parts) < 2 or parts[0] != "ssh-ed25519":
        raise ConfigError("La clau de host ha de ser ssh-ed25519")
    name = host if port == 22 else f"[{host}]:{port}"
    return f"{name} {parts[0]} {parts[1]}\n"


def docker_json(*args: str) -> list:
    out = subprocess.run(["docker", *args], capture_output=True, text=True)
    if out.returncode != 0:
        raise ConfigError(f"docker {args[0]} ha fallat: {out.stderr.strip()[-200:]}")
    return json.loads(out.stdout)


def used_subnets() -> list[str]:
    ids = subprocess.run(["docker", "network", "ls", "-q"], capture_output=True, text=True, check=True).stdout.split()
    subnets: list[str] = []
    for net in docker_json("network", "inspect", *ids):
        for cfg in (net.get("IPAM") or {}).get("Config") or []:
            if cfg.get("Subnet"):
                subnets.append(cfg["Subnet"])
    # Rutes del propi servidor (VPN, xarxes de contenidors, etc.): millor esforç, si «ip» no hi és s'ignora.
    try:
        routes = subprocess.run(["ip", "-o", "-4", "route"], capture_output=True, text=True, check=False).stdout
        for line in routes.splitlines():
            token = line.split()[0] if line.split() else ""
            if "/" in token:
                subnets.append(token)
    except OSError:
        pass
    return subnets


def own_network_subnet(name: str = "web-ssh-terminal_backend") -> str | None:
    """Subxarxa real de la xarxa de l'app si ja existeix (autoritat per mantenir-la entre desplegaments)."""
    out = subprocess.run(["docker", "network", "inspect", name], capture_output=True, text=True)
    if out.returncode != 0:
        return None
    for net in json.loads(out.stdout):
        for cfg in (net.get("IPAM") or {}).get("Config") or []:
            if cfg.get("Subnet"):
                return cfg["Subnet"]
    return None


def traefik_ip(container: str, network: str) -> str:
    info = docker_json("inspect", container)
    nets = info[0]["NetworkSettings"]["Networks"]
    if network not in nets:
        raise ConfigError(f"El contenidor «{container}» no és a la xarxa «{network}»")
    return nets[network]["IPAddress"]


def cmd_render(args: argparse.Namespace) -> None:
    env = parse_env(Path(args.env).read_text(encoding="utf-8"))
    merged = {**DEFAULTS, **env}
    # La subxarxa es manté entre desplegaments només si la xarxa REAL de l'app ja existeix (canviar-la obligaria a
    # recrear-la). Un config.json previ no és fiable: init el copia de l'exemple, amb una subxarxa de mostra.
    subnet = env.get("BACKEND_SUBNET") or own_network_subnet() or pick_subnet(used_subnets())
    ip = traefik_ip(merged["TRAEFIK_CONTAINER"], merged["PROXY_NETWORK"])
    cfg = build_config(env, ip, subnet)
    Path(args.out).write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8")


def cmd_get(args: argparse.Namespace) -> None:
    env = parse_env(Path(args.env).read_text(encoding="utf-8")) if Path(args.env).is_file() else {}
    print({**DEFAULTS, **{k: v for k, v in env.items() if v != ""}}.get(args.key, ""))


def cmd_field(args: argparse.Namespace) -> None:
    print(json.loads(Path(args.config).read_text(encoding="utf-8"))[args.key])


def cmd_known_hosts(args: argparse.Namespace) -> None:
    cfg = json.loads(Path(args.config).read_text(encoding="utf-8"))
    sys.stdout.write(known_hosts_line(cfg["ssh_host"], cfg["ssh_port"], Path(args.pubkey).read_text(encoding="utf-8")))


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("render"); r.add_argument("--env", required=True); r.add_argument("--out", required=True)
    r.add_argument("--existing"); r.set_defaults(fn=cmd_render)
    g = sub.add_parser("get"); g.add_argument("key"); g.add_argument("--env", required=True); g.set_defaults(fn=cmd_get)
    f = sub.add_parser("field"); f.add_argument("key"); f.add_argument("--config", required=True); f.set_defaults(fn=cmd_field)
    k = sub.add_parser("known-hosts"); k.add_argument("--config", required=True); k.add_argument("--pubkey", required=True)
    k.set_defaults(fn=cmd_known_hosts)
    args = p.parse_args()
    try:
        args.fn(args)
    except (ConfigError, OSError, KeyError, ValueError, subprocess.CalledProcessError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
