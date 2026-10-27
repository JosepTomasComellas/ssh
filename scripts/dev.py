#!/usr/bin/env python3
"""Entorn local de desenvolupament i proves amb Docker Desktop (Windows/macOS/Linux).

  dev.py up [--proxy nginx|traefik] [--monitoring]   construeix i aixeca l'entorn
  dev.py test [--proxy nginx|traefik]                proves de punta a punta (Playwright)
  dev.py logs [servei] [--errors]                    segueix logs (o només warnings/errors)
  dev.py down                                        atura (conserva dades)
  dev.py reset --yes                                 elimina volums i .dev/ LOCALS de desenvolupament
  dev.py fixture-keys                                copia les claus del servidor SSH de proves

No necessita domini, DNS, ACME, GHCR ni cap servidor SSH real. Secrets, claus i certificats es generen a
.dev/ (ignorat per Git) i mai s'imprimeixen. NO ÉS UN MODE DE PRODUCCIÓ.
"""
from __future__ import annotations

import argparse
import os
import secrets
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEV = ROOT / ".dev"
COMPOSE_FILE = ROOT / "deploy" / "dev" / "compose.yaml"
PROJECT = "webssh-dev"
ALL_PROFILES = ["nginx", "traefik", "monitoring", "e2e"]
FIXTURE_IMAGE = "webssh-dev-ssh-fixture:latest"


class DevError(Exception):
    pass


def run(cmd: list[str], *, env: dict[str, str] | None = None, check: bool = True, capture: bool = False,
        quiet: bool = False) -> subprocess.CompletedProcess:
    merged = {**os.environ, **(env or {})}
    result = subprocess.run(cmd, env=merged, cwd=ROOT, text=True, capture_output=capture or quiet)
    if check and result.returncode != 0:
        detail = (result.stderr or "").strip().splitlines()[-1:] if (capture or quiet) else []
        raise DevError(f"Ha fallat: {' '.join(cmd[:4])}… (codi {result.returncode}) {' '.join(detail)}".strip())
    return result


def compose_env() -> dict[str, str]:
    rev = run(["git", "rev-parse", "HEAD"], capture=True).stdout.strip()
    if len(rev) != 40:
        raise DevError("No s'ha pogut obtenir la revisió de Git (calen 40 caràcters).")
    return {"DEV_DIR": DEV.as_posix(), "REVISION": rev}


def compose(args: list[str], profiles: list[str] | None = None, **kw) -> subprocess.CompletedProcess:
    cmd = ["docker", "compose", "-p", PROJECT, "-f", str(COMPOSE_FILE)]
    for p in profiles or []:
        cmd += ["--profile", p]
    return run(cmd + args, env=compose_env(), **kw)


def require_docker() -> None:
    if shutil.which("docker") is None:
        raise DevError("No s'ha trobat 'docker'. Instal·la Docker Desktop i obre'l.")
    if run(["docker", "info"], check=False, quiet=True).returncode != 0:
        raise DevError("Docker no respon. Arrenca Docker Desktop i espera que digui 'Engine running'.")
    if run(["docker", "compose", "version"], check=False, quiet=True).returncode != 0:
        raise DevError("Cal Docker Compose v2 (inclòs a Docker Desktop).")


def write_secret(path: Path, value: str) -> None:
    if path.exists() and path.stat().st_size > 0:
        return  # no se sobreescriuen mai: canviar-los invalidaria dades locals
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8")
    try:
        path.chmod(0o600)
    except OSError:
        pass  # Windows


def ensure_secrets() -> None:
    sec = DEV / "secrets"
    write_secret(sec / "app_key", secrets.token_hex(32))
    write_secret(sec / "bootstrap_password", secrets.token_urlsafe(24))
    write_secret(sec / "ssh_credentials", "{}")
    write_secret(sec / "metrics_token", secrets.token_urlsafe(32))
    mon = DEV / "monitoring"
    write_secret(mon / "metrics_token", (sec / "metrics_token").read_text(encoding="utf-8"))
    write_secret(mon / "grafana_admin_password", secrets.token_urlsafe(18))
    (DEV / "test-results").mkdir(parents=True, exist_ok=True)


def ensure_certs() -> None:
    certs = DEV / "certs"
    if (certs / "dev.crt").exists() and (certs / "dev.key").exists():
        return
    certs.mkdir(parents=True, exist_ok=True)
    print("Generant certificat local autosignat (només desenvolupament)…")
    run(["docker", "run", "--rm", "-v", f"{certs.as_posix()}:/out", "alpine/openssl", "req", "-x509", "-newkey",
         "rsa:2048", "-nodes", "-days", "60", "-keyout", "/out/dev.key", "-out", "/out/dev.crt", "-subj", "/CN=localhost",
         "-addext", "subjectAltName=DNS:localhost,DNS:nginx,DNS:traefik"], quiet=True)


def write_known_hosts() -> None:
    """known_hosts amb la clau de host del fixture (cuita a la imatge), validada fora de banda de l'app."""
    compose(["build", "ssh-fixture"], quiet=True)
    pub = run(["docker", "run", "--rm", "--entrypoint", "cat", FIXTURE_IMAGE, "/etc/ssh/ssh_host_ed25519_key.pub"],
              capture=True).stdout.split()
    if len(pub) < 2 or pub[0] != "ssh-ed25519":
        raise DevError("No s'ha pogut llegir la clau de host del fixture.")
    (DEV / "secrets" / "known_hosts").write_text(f"ssh-fixture {pub[0]} {pub[1]}\n", encoding="utf-8")


def cmd_up(args: argparse.Namespace) -> None:
    require_docker()
    ensure_secrets()
    ensure_certs()
    write_known_hosts()
    other = "traefik" if args.proxy == "nginx" else "nginx"
    compose(["rm", "-sf", other], profiles=[other], check=False, quiet=True)  # només un proxy a :8443
    profiles = [args.proxy] + (["monitoring"] if args.monitoring else [])
    print(f"Construint i aixecant (proxy: {args.proxy})…")
    compose(["up", "-d", "--build", "--wait", "--wait-timeout", "240"], profiles=profiles)
    compose(["restart", args.proxy], profiles=profiles, quiet=True)  # rellegeix la configuració del proxy
    compose(["up", "-d", "--wait", "--wait-timeout", "60"], profiles=profiles, quiet=True)
    print("\nEntorn local llest.")
    print("  UI:           https://localhost:8443   (certificat autosignat: accepta l'avís del navegador)")
    print("  Usuari:       admin")
    print(f"  Contrasenya:  està a {DEV / 'secrets' / 'bootstrap_password'}")
    print("                (PowerShell: Get-Content .dev\\secrets\\bootstrap_password). Es demanarà canviar-la.")
    print("  Servidor SSH: contenidor efímer 'ssh-fixture' (comptes alice i bob). Claus: dev.py fixture-keys")
    if args.monitoring:
        print("  Prometheus:   http://127.0.0.1:9090      Grafana: http://127.0.0.1:3000 (usuari admin; contrasenya a "
              f"{DEV / 'monitoring' / 'grafana_admin_password'})")


def cmd_test(args: argparse.Namespace) -> None:
    require_docker()
    if run(["docker", "compose", "-p", PROJECT, "ps", "-q", "app"], capture=True).stdout.strip() == "":
        print("L'entorn no està aixecat; l'aixeco…")
        cmd_up(argparse.Namespace(proxy=args.proxy, monitoring=False))
    print(f"Executant proves e2e a través de {args.proxy}…")
    result = compose(["run", "--rm", "--build", "-e", f"PROXY_HOST={args.proxy}", "e2e"],
                     profiles=["e2e"], check=False)
    print(f"Resultats i captures: {DEV / 'test-results'}")
    if result.returncode != 0:
        raise DevError("Hi ha proves e2e fallides.")


def cmd_logs(args: argparse.Namespace) -> None:
    require_docker()
    service = args.service or "app"
    if not args.errors:
        compose(["logs", "-f", "--no-log-prefix", "--since", "10m", service], profiles=ALL_PROFILES, check=False)
        return
    out = compose(["logs", "--no-log-prefix", service], profiles=ALL_PROFILES, capture=True, check=False).stdout
    for line in out.splitlines():
        if any(f'"LogLevel":"{lvl}"' in line for lvl in ("Warning", "Error", "Critical")):
            print(line)


def cmd_down(_: argparse.Namespace) -> None:
    require_docker()
    compose(["down", "--remove-orphans"], profiles=ALL_PROFILES)
    print("Aturat. Les dades locals es conserven (dev.py reset --yes les elimina).")


def cmd_reset(args: argparse.Namespace) -> None:
    if not args.yes:
        raise DevError("Això elimina els volums i .dev/ LOCALS de desenvolupament. Repeteix amb --yes.")
    require_docker()
    compose(["down", "-v", "--remove-orphans"], profiles=ALL_PROFILES, check=False)
    if DEV.exists():
        shutil.rmtree(DEV)
    print("Entorn de desenvolupament local eliminat (projecte webssh-dev).")


def cmd_fixture_keys(_: argparse.Namespace) -> None:
    require_docker()
    target = DEV / "fixture-keys"
    target.mkdir(parents=True, exist_ok=True)
    for user in ("alice", "bob"):
        data = compose(["exec", "-T", "ssh-fixture", "cat", f"/keys/{user}"], capture=True).stdout
        (target / user).write_text(data, encoding="utf-8")
    print(f"Claus privades del fixture (NOMÉS de proves, no són secrets de producció) a {target}")
    print("Enganxa-les a Admin > Identitats per assignar-les a usuaris web.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)
    up = sub.add_parser("up")
    up.add_argument("--proxy", choices=["nginx", "traefik"], default="nginx")
    up.add_argument("--monitoring", action="store_true", help="aixeca Prometheus i Grafana privats (127.0.0.1)")
    up.set_defaults(fn=cmd_up)
    test = sub.add_parser("test")
    test.add_argument("--proxy", choices=["nginx", "traefik"], default="nginx")
    test.set_defaults(fn=cmd_test)
    logs = sub.add_parser("logs")
    logs.add_argument("service", nargs="?")
    logs.add_argument("--errors", action="store_true", help="només Warning/Error/Critical")
    logs.set_defaults(fn=cmd_logs)
    sub.add_parser("down").set_defaults(fn=cmd_down)
    reset = sub.add_parser("reset")
    reset.add_argument("--yes", action="store_true")
    reset.set_defaults(fn=cmd_reset)
    sub.add_parser("fixture-keys").set_defaults(fn=cmd_fixture_keys)

    args = parser.parse_args()
    try:
        args.fn(args)
    except DevError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        return 130
    return 0


if __name__ == "__main__":
    sys.exit(main())
