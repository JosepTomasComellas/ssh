"""Validate actual Docker Compose rendering for both supported proxy modes."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory() as temp:
    env = dict(os.environ, APP_IMAGE="ghcr.io/joseptomascomellas/ssh@sha256:" + "a" * 64,
               DOMAIN="ssh.example.com", HTTP_PORT="18080", PROXY_NETWORK="web",
               TRAEFIK_RESOLVER="letsencrypt", TRUSTED_PROXIES="172.30.50.1",
               BACKEND_SUBNET="172.30.50.0/24", DATA_DIR=temp, SECRETS_DIR=temp,
               SOURCE_URL="https://github.com/JosepTomasComellas/ssh/tree/" + "b" * 40)
    for proxy in ("nginx", "traefik"):
        raw = subprocess.check_output(["docker", "compose", "--project-name", "webssh-test",
                                      "-f", str(root / "deploy/compose.yaml"),
                                      "-f", str(root / f"deploy/compose.{proxy}.yaml"),
                                      "config", "--format", "json"], env=env, text=True)
        config = json.loads(raw)
        app = config["services"]["app"]
        assert app["read_only"] and app["user"] == "1654:1654"
        assert app["cap_drop"] == ["ALL"]
        assert len(app["secrets"]) == 4
        assert "--healthcheck" in app["healthcheck"]["test"]
        if proxy == "nginx":
            assert app["ports"][0]["host_ip"] == "127.0.0.1"
        else:
            assert not app.get("ports")
            assert app["labels"]["traefik.http.routers.webssh.tls"] == "true"
            assert config["networks"]["proxy"]["external"] is True
        print(proxy + ": Compose model validated")
