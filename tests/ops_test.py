#!/usr/bin/env python3
"""Proves d'operació contra l'entorn local (cal `scripts/dev.py up` i haver executat `dev.py test` un cop).

Cobreixen: persistència en recrear el contenidor, falla tancada amb secrets absents o amb permisos dolents,
i còpia/restauració del volum de dades. Només toquen recursos del projecte Docker `webssh-dev` i volums
temporals propis; mai dades de desplegament.
"""
from __future__ import annotations

import hashlib
import re
import subprocess
import ssl
import sys
import unittest
import urllib.error
import urllib.parse
import urllib.request
from http.cookiejar import CookieJar
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEV = ROOT / ".dev"
PROJECT = "webssh-dev"
URL = "https://localhost:8443"
IMAGE = "webssh-app:dev"
COMPOSE = ["docker", "compose", "-p", PROJECT, "-f", str(ROOT / "deploy/dev/compose.yaml")]


def sh(*cmd: str, check: bool = True) -> subprocess.CompletedProcess:
    env = {**__import__("os").environ, "DEV_DIR": DEV.as_posix(),
           "REVISION": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()}
    r = subprocess.run(cmd, capture_output=True, text=True, env=env, cwd=ROOT)
    if check and r.returncode != 0:
        raise AssertionError(f"{' '.join(cmd[:5])}… ha fallat ({r.returncode}): {r.stderr.strip()[-300:]}")
    return r


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):  # noqa: D401
        return None


def admin_login_works() -> bool:
    """Intenta entrar com a admin amb la contrasenya determinista de l'e2e (sense imprimir-la)."""
    boot = (DEV / "secrets" / "bootstrap_password").read_text().strip()
    password = "E2e-" + hashlib.sha256(f"{boot}|admin".encode()).hexdigest()[:28]
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    jar = CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar), urllib.request.HTTPSHandler(context=ctx), NoRedirect)
    html = opener.open(f"{URL}/login", timeout=10).read().decode()
    token = re.search(r'__RequestVerificationToken[^>]*value="([^"]+)"', html).group(1)
    data = urllib.parse.urlencode({"username": "admin", "password": password, "__RequestVerificationToken": token}).encode()
    try:
        opener.open(f"{URL}/account/login", data, timeout=10)
    except urllib.error.HTTPError as e:
        return e.code == 302 and "error=1" not in (e.headers.get("Location") or "")
    return False


def wait_ready(seconds: int = 60) -> None:
    import time
    deadline = time.time() + seconds
    while time.time() < deadline:
        r = sh("docker", "inspect", "-f", "{{.State.Health.Status}}", f"{PROJECT}-app-1", check=False)
        if r.stdout.strip() == "healthy":
            return
        time.sleep(1)
    raise AssertionError("L'app no ha arribat a 'healthy'.")


def run_app_with_secrets(volume: str) -> subprocess.CompletedProcess:
    """Executa la imatge de l'app amb el volum de secrets indicat; sense xarxa ni publicar ports."""
    env = ["-e", "WebSsh__PublicUrl=https://localhost:8443", "-e", "WebSsh__SshHost=ssh-fixture",
           "-e", "WebSsh__DataDirectory=/tmp/d", "-e", "WebSsh__AppKeyFile=/run/secrets/app_key",
           "-e", "WebSsh__BootstrapPasswordFile=/run/secrets/bootstrap_password",
           "-e", "WebSsh__SshCredentialsFile=/run/secrets/ssh_credentials", "-e", "WebSsh__KnownHostsFile=/run/secrets/known_hosts"]
    return sh("docker", "run", "--rm", "--network", "none", "--read-only", "--tmpfs", "/tmp", "-v", f"{volume}:/run/secrets:ro",
              *env, IMAGE, check=False)


class OpsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if sh("docker", "ps", "-q", "-f", f"name={PROJECT}-app-1", check=False).stdout.strip() == "":
            raise unittest.SkipTest("L'entorn local no està aixecat (scripts/dev.py up).")
        if not admin_login_works():
            raise unittest.SkipTest("Cal haver executat `scripts/dev.py test` un cop (prepara l'admin determinista).")

    def test_data_survives_container_recreation(self) -> None:
        sh(*COMPOSE, "up", "-d", "--force-recreate", "--no-deps", "app")
        wait_ready()
        self.assertTrue(admin_login_works(), "Després de recrear el contenidor l'admin ja no entra: dades perdudes")

    def test_sigterm_shuts_down_gracefully_within_30_seconds(self) -> None:
        import time
        t0 = time.time()
        sh("docker", "stop", "-t", "30", f"{PROJECT}-app-1")
        elapsed = time.time() - t0
        info = sh("docker", "inspect", "-f", "{{.State.ExitCode}}", f"{PROJECT}-app-1").stdout.strip()
        logs = sh("docker", "logs", f"{PROJECT}-app-1", check=False)
        sh(*COMPOSE, "up", "-d", "--no-deps", "app")
        wait_ready()
        self.assertLess(elapsed, 30, "L'aturada ha trigat massa (SIGKILL?)")
        self.assertEqual(info, "0", "Sortida no neta")
        self.assertIn("Aturada completada", logs.stdout + logs.stderr)

    def _secrets_volume(self, name: str, files: dict[str, str], mode: str) -> str:
        vol = f"webssh-ops-{name}"
        sh("docker", "volume", "rm", "-f", vol, check=False)
        sh("docker", "volume", "create", vol)
        script = " && ".join([f"printf '%s' '{content}' > /s/{fname}" for fname, content in files.items()] +
                             ["chown 1654:1654 /s/* 2>/dev/null || true", f"chmod {mode} /s/* 2>/dev/null || true"])
        sh("docker", "run", "--rm", "-v", f"{vol}:/s", "alpine:3.22", "sh", "-c", script)
        self.addCleanup(lambda: sh("docker", "volume", "rm", "-f", vol, check=False))
        return vol

    def test_missing_secrets_fail_closed(self) -> None:
        vol = self._secrets_volume("missing", {"app_key": "a" * 64}, "0400")  # falten la resta
        r = run_app_with_secrets(vol)
        self.assertEqual(r.returncode, 78, r.stderr[-200:])
        self.assertIn("el fitxer no existeix", r.stdout + r.stderr)
        self.assertNotIn("a" * 64, r.stdout + r.stderr)

    def test_world_readable_secrets_are_rejected(self) -> None:
        files = {"app_key": "b" * 64, "bootstrap_password": "x" * 24, "ssh_credentials": "{}", "known_hosts": ""}
        vol = self._secrets_volume("perms", files, "0644")
        r = run_app_with_secrets(vol)
        self.assertEqual(r.returncode, 78, r.stderr[-200:])
        self.assertIn("permisos massa oberts", r.stdout + r.stderr)
        self.assertNotIn("b" * 64, r.stdout + r.stderr)

    def test_data_volume_backup_and_restore(self) -> None:
        data_volume = f"{PROJECT}_data"
        copy = "webssh-ops-restore"
        sh("docker", "volume", "rm", "-f", copy, check=False)
        self.addCleanup(lambda: sh("docker", "volume", "rm", "-f", copy, check=False))
        sh(*COMPOSE, "stop", "app")  # còpia consistent: l'app en manteniment
        try:
            sh("docker", "volume", "create", copy)
            sh("docker", "run", "--rm", "-v", f"{data_volume}:/from:ro", "-v", f"{copy}:/to", "alpine:3.22",
               "sh", "-c", "cd /from && tar cf - . | (cd /to && tar xf -) && chown -R 1654:1654 /to && chmod 700 /to")
        finally:
            sh(*COMPOSE, "start", "app")
            wait_ready()
        # La còpia és una instal·lació aïllada vàlida: conté la BD i les claus.
        listing = sh("docker", "run", "--rm", "-v", f"{copy}:/d:ro", "alpine:3.22", "ls", "/d").stdout
        self.assertIn("webssh.db", listing)
        self.assertIn("keys", listing)
        self.assertTrue(admin_login_works())


if __name__ == "__main__":
    sys.exit(0 if unittest.main(exit=False).result.wasSuccessful() else 1)
