#!/usr/bin/env python3
"""Linux deployment controller. Python 3.10+, Docker Compose >=2.20, git, age."""
import argparse
import contextlib
import fcntl
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import signal
import stat
import subprocess
import sys
import time
import urllib.request
import uuid

REPO = "https://github.com/JosepTomasComellas/ssh.git"
SOURCE = "https://github.com/JosepTomasComellas/ssh"
IMAGE = r"ghcr\.io/joseptomascomellas/ssh@sha256:[0-9a-f]{64}"
TAG = r"v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?"
HERE = Path(__file__).resolve().parents[1]
UID = 1654


class Error(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise Error(message)


def run(*args, capture=False, **kwargs):
    result = subprocess.run([str(x) for x in args], check=True,
                            stdout=subprocess.PIPE if capture else None,
                            text=True, **kwargs)
    return result.stdout.strip() if capture else ""


def read_json(path):
    with Path(path).open(encoding="utf-8") as stream:
        return json.load(stream)


def atomic_json(path, value):
    path = Path(path)
    temp = path.with_name(path.name + ".tmp")
    with temp.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    temp.chmod(0o600)
    os.replace(temp, path)
    fd = os.open(path.parent, os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def application(repo, release=False):
    manifest = read_json(repo / "deploy/application.json")
    require(manifest.get("status") == "ready",
            "Application is not implemented/release-ready (status=scaffold). Deployment blocked.")
    require(manifest.get("project") == "src/WebSshTerminal/WebSshTerminal.csproj",
            "Unsupported application project contract")
    require((repo / manifest["project"]).is_file(), "Application project is missing")
    gates = ["authentication_and_authorization", "ssh_host_key_validation",
             "websocket_origin_and_session_isolation", "secrets_and_persistent_data",
             "healthcheck_and_graceful_shutdown", "backup_restore_and_backward_compatibility",
             "tuteapps_and_source_link"]
    require(all(manifest.get("release_checks", {}).get(k) is True for k in gates),
            "Application release acceptance checks are incomplete")
    require(type(manifest.get("data_epoch")) is int and manifest["data_epoch"] > 0,
            "Invalid data compatibility epoch")
    if release:
        for key in ("sdk_image", "runtime_image"):
            require(re.fullmatch(r"mcr\.microsoft\.com/dotnet/[a-z]+:[^@\s]+@sha256:[0-9a-f]{64}",
                                 manifest.get(key) or ""), "Pin .NET build images by digest")
        require((repo / "src/WebSshTerminal/packages.lock.json").is_file(), "NuGet lockfile missing")
    return manifest


def validate_config(cfg):
    require(isinstance(cfg, dict), "Config must be a JSON object")
    expected = {"domain", "proxy", "http_port", "proxy_network", "traefik_resolver",
                "trusted_proxies", "backend_subnet", "backup_recipient", "verify_signed_tags",
                "ssh_host", "ssh_port", "timezone", "require_mfa"}
    require(set(cfg) == expected, "Unknown or missing configuration keys")
    domain = cfg["domain"]
    require(isinstance(domain, str) and len(domain) <= 253 and
            re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+", domain),
            "Invalid DNS domain")
    require(cfg["proxy"] in ("nginx", "traefik"), "Select nginx or traefik")
    require(type(cfg["http_port"]) is int and 1024 <= cfg["http_port"] <= 65535, "Invalid loopback port")
    for key in ("proxy_network", "traefik_resolver"):
        require(isinstance(cfg[key], str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,62}", cfg[key]), "Invalid " + key)
    require(isinstance(cfg["trusted_proxies"], list) and cfg["trusted_proxies"], "Set exact trusted proxy IPs")
    for value in cfg["trusted_proxies"]:
        ipaddress.ip_address(value)
    subnet = ipaddress.ip_network(cfg["backend_subnet"])
    require(subnet.version == 4 and 16 <= subnet.prefixlen <= 28, "Use an IPv4 backend subnet /16../28")
    require(type(cfg["verify_signed_tags"]) is bool, "verify_signed_tags must be boolean")
    require(isinstance(cfg["ssh_host"], str) and re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?", cfg["ssh_host"]),
            "Invalid ssh_host (plain host name or IP, no scheme/user/port)")
    require(type(cfg["require_mfa"]) is bool, "require_mfa must be boolean")
    require(type(cfg["ssh_port"]) is int and 1 <= cfg["ssh_port"] <= 65535, "Invalid ssh_port")
    require(isinstance(cfg["timezone"], str) and re.fullmatch(r"[A-Za-z0-9_+/-]{1,64}", cfg["timezone"]), "Invalid timezone")
    require(isinstance(cfg["backup_recipient"], str) and
            re.fullmatch(r"age1[0-9a-z]{58}", cfg["backup_recipient"]), "Set an age public backup recipient")
    return cfg


def safe_file(path, owner, mode):
    require(path.is_file() and not path.is_symlink(), "Missing regular file: " + str(path))
    info = path.stat()
    require(info.st_uid == owner and stat.S_IMODE(info.st_mode) == mode,
            f"Incorrect owner/mode for {path}; expected UID {owner}, mode {mode:o}")
    require(info.st_size > 0, "Empty file: " + str(path))


class Controller:
    def __init__(self, root):
        self.root = Path(root).absolute()
        require(str(self.root) == str(self.root.resolve()) and self.root != Path("/"),
                "Root must be an absolute path without symlinks")
        require(not any(c in str(self.root) for c in "\n\r$'\""), "Unsafe installation path")
        self.statefile = self.root / "state.json"
        self.journal = self.root / "transaction.json"

    @contextlib.contextmanager
    def locked(self):
        require(os.geteuid() == 0, "Run as root on Linux")
        require(self.root.is_dir(), "Run init first")
        info = self.root.stat()
        require(info.st_uid == 0 and stat.S_IMODE(info.st_mode) == 0o700,
                "Installation root must be root-owned, mode 0700")
        with (self.root / ".lock").open("a") as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError as exc:
                raise Error("Another deployment/backup is running") from exc
            yield

    def init(self):
        require(os.geteuid() == 0, "Run as root on Linux")
        require(not self.root.exists(), "Installation directory exists; init never overwrites it")
        self.root.mkdir(parents=True, mode=0o700)
        for name in ("releases", "secrets", "data", "backups"):
            (self.root / name).mkdir(mode=0o700)
        os.chown(self.root / "data", UID, UID)
        shutil.copyfile(HERE / "deploy/config.example.json", self.root / "config.json")
        (self.root / "config.json").chmod(0o600)
        for name in ("app_key", "bootstrap_password", "metrics_token"):
            target = self.root / "secrets" / name
            target.write_text(secrets.token_hex(32) + "\n", encoding="ascii")
            os.chown(target, UID, UID)
            target.chmod(0o400)
        print("Initialized. Edit config.json; provision ssh_credentials and verified known_hosts. No containers started.")

    def config(self):
        safe_file(self.root / "config.json", 0, 0o600)
        return validate_config(read_json(self.root / "config.json"))

    def preflight(self):
        for command in ("docker", "git", "age", "tar"):
            require(shutil.which(command), "Missing prerequisite: " + command)
        for name in ("app_key", "bootstrap_password", "metrics_token", "ssh_credentials", "known_hosts"):
            safe_file(self.root / "secrets" / name, UID, 0o400)
        info = (self.root / "data").stat()
        require(info.st_uid == UID and stat.S_IMODE(info.st_mode) == 0o700, "Data directory must be UID 1654 mode 0700")
        require(isinstance(read_json(self.root / "secrets/ssh_credentials"), dict), "SSH credentials must be a JSON object")
        version = run("docker", "compose", "version", "--short", capture=True).lstrip("v").split(".")
        require(tuple(int(x) for x in version[:2]) >= (2, 20), "Docker Compose >=2.20 required")
        require(run("docker", "info", "--format", "{{.OSType}}", capture=True) == "linux", "Linux containers required")

    def state(self):
        return read_json(self.statefile) if self.statefile.exists() else {"current": None, "previous": None}

    def compose(self, item, *args):
        directory = self.root / "releases" / item["id"]
        # Compose interpolation must not inherit APP_IMAGE/COMPOSE_FILE/etc.
        env = {k: os.environ[k] for k in ("PATH", "HOME", "DOCKER_CONFIG", "DOCKER_HOST", "DOCKER_CONTEXT", "XDG_RUNTIME_DIR") if k in os.environ}
        return run("docker", "compose", "--project-name", "web-ssh-terminal",
                   "--env-file", directory / "compose.env",
                   "-f", directory / "source/deploy/compose.yaml",
                   "-f", directory / f"source/deploy/compose.{item['proxy']}.yaml", *args, env=env)

    def checkout(self, tag, commit, target, signed):
        cache = self.root / "repository.git"
        if not cache.exists():
            run("git", "init", "--bare", cache)
            run("git", "-C", cache, "remote", "add", "origin", REPO)
        require(run("git", "-C", cache, "remote", "get-url", "origin", capture=True) == REPO, "Unexpected repository origin")
        # No force: an already-seen tag cannot silently change.
        run("git", "-C", cache, "fetch", "--no-tags", "origin", f"refs/tags/{tag}:refs/tags/{tag}")
        require(run("git", "-C", cache, "cat-file", "-t", f"refs/tags/{tag}", capture=True) == "tag", "Use annotated release tags")
        actual = run("git", "-C", cache, "rev-parse", f"refs/tags/{tag}^{{commit}}", capture=True)
        require(actual == commit, "Tag does not match the expected commit")
        if signed:
            run("git", "-C", cache, "verify-tag", tag)
        run("git", "clone", "--no-hardlinks", "--no-checkout", cache, target)
        run("git", "-C", target, "checkout", "--detach", commit)

    def prepare(self, tag, commit, image, cfg):
        require(re.fullmatch(TAG, tag), "Use an explicit vMAJOR.MINOR.PATCH release tag")
        require(re.fullmatch(r"[0-9a-f]{40}", commit), "Use the full expected commit SHA")
        require(re.fullmatch(IMAGE, image), "Use the project GHCR image pinned by sha256 digest")
        identity = tag + "-" + commit[:12] + "-" + uuid.uuid4().hex[:8]
        folder = self.root / "releases" / identity
        folder.mkdir(mode=0o700)
        self.checkout(tag, commit, folder / "source", cfg["verify_signed_tags"])
        app = application(folder / "source")
        item = dict(id=identity, tag=tag, commit=commit, image=image,
                    data_epoch=app["data_epoch"], proxy=cfg["proxy"], domain=cfg["domain"])
        variables = {
            "APP_IMAGE": image, "DOMAIN": cfg["domain"], "HTTP_PORT": cfg["http_port"],
            "PROXY_NETWORK": cfg["proxy_network"], "TRAEFIK_RESOLVER": cfg["traefik_resolver"],
            "TRUSTED_PROXIES": ",".join(cfg["trusted_proxies"]), "BACKEND_SUBNET": cfg["backend_subnet"],
            "DATA_DIR": self.root / "data", "SECRETS_DIR": self.root / "secrets",
            "SOURCE_URL": SOURCE + "/tree/" + commit, "SSH_HOST": cfg["ssh_host"], "SSH_PORT": cfg["ssh_port"],
            "TZ": cfg["timezone"], "REQUIRE_MFA": "true" if cfg["require_mfa"] else "false",
        }
        (folder / "compose.env").write_text("".join(f"{k}='{v}'\n" for k, v in variables.items()), encoding="utf-8")
        (folder / "compose.env").chmod(0o600)
        atomic_json(folder / "release.json", item)
        self.compose(item, "config", "--quiet")
        self.compose(item, "pull")
        revision = run("docker", "image", "inspect", image, "--format",
                       '{{index .Config.Labels "org.opencontainers.image.revision"}}', capture=True)
        require(revision == commit, "Image revision label does not match release source")
        return item

    def probe(self, item):
        url = "https://" + item["domain"] + "/health/ready"
        # A first deployment may have to wait for the proxy to obtain a certificate (ACME), so retry for a while.
        deadline = time.monotonic() + int(os.environ.get("WEBSSH_PROBE_SECONDS", "180"))
        last = None
        while True:
            try:
                # Default CA trust; redirects are rejected by checking the final URL.
                with urllib.request.urlopen(url, timeout=10) as response:
                    require(response.geturl() == url, "Readiness URL redirected")
                    value = json.loads(response.read(4096))
                    require(value == {"status": "ok", "revision": item["commit"]}, "Public readiness revision mismatch")
                return
            except (OSError, ValueError, Error) as exc:
                last = exc
            if time.monotonic() >= deadline:
                break
            time.sleep(3)
        hint = ""
        if "CERTIFICATE_VERIFY_FAILED" in str(last):
            hint = (" -- the proxy still serves its default certificate: check that the domain points to this server, that port 443"
                    " is reachable from the Internet, and the proxy's ACME logs (e.g. docker logs traefik | grep -i acme)")
        raise Error("HTTPS readiness failed: " + str(last) + hint)
    def start(self, item):
        self.compose(item, "up", "--detach", "--no-build", "--pull", "never", "--wait", "--wait-timeout", "120")
        self.probe(item)

    def backup_stopped(self, cfg):
        require(shutil.which("age"), "Install age for encrypted backups")
        filename = self.root / "backups" / (time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + uuid.uuid4().hex[:8] + ".tar.age")
        partial = filename.with_suffix(".age.partial")
        # Data and all secrets travel directly through a pipe to age, never a plain tar file.
        with partial.open("xb") as out:
            with subprocess.Popen(["tar", "-C", str(self.root), "-cf", "-", "data", "secrets", "config.json", "state.json"], stdout=subprocess.PIPE) as tar:
                result = subprocess.run(["age", "-r", cfg["backup_recipient"]], stdin=tar.stdout, stdout=out)
                tar.stdout.close()
                tar_code = tar.wait()
        require(result.returncode == 0 and tar_code == 0, "Encrypted backup failed; partial file retained")
        partial.chmod(0o600)
        os.replace(partial, filename)
        print("Encrypted backup: " + str(filename))

    def recover(self):
        require(self.journal.exists(), "No unfinished transaction")
        txn = read_json(self.journal)
        old = txn["before"]["current"]
        if old:
            # Always recreate: a failed candidate may have partially replaced the service.
            self.compose(old, "up", "--detach", "--force-recreate", "--no-build", "--pull", "never", "--wait", "--wait-timeout", "120")
            self.probe(old)
        elif txn.get("candidate"):
            self.compose(txn["candidate"], "down")
        atomic_json(self.statefile, txn["before"])
        self.journal.unlink()
        print("Previous deployment restored." if old else "Failed initial deployment stopped; data retained.")

    def transition(self, candidate, cfg, backup=True):
        require(not self.journal.exists(), "Unfinished transaction: run recover first")
        before = self.state()
        old = before["current"]
        if old:
            require(old["data_epoch"] == candidate["data_epoch"], "Data epoch changed: requires a separate migration and restore plan")
            require(old["proxy"] == candidate["proxy"] and old["domain"] == candidate["domain"], "Proxy/domain migration requires a maintenance plan")
        atomic_json(self.journal, {"before": before, "candidate": candidate})
        try:
            if old:
                self.compose(old, "stop", "app")
                if backup:
                    self.backup_stopped(cfg)
            self.start(candidate)
            atomic_json(self.statefile, {"current": candidate, "previous": old})
            self.journal.unlink()
        except BaseException:
            print("Deployment interrupted/failed; restoring previous version.", file=sys.stderr)
            try:
                self.recover()
            except BaseException:
                print("Recovery also failed. Transaction retained; fix the cause and run recover.", file=sys.stderr)
            raise
        print("Active release: " + candidate["tag"])

    def deploy(self, args):
        require(not self.journal.exists(), "Unfinished transaction: run recover first")
        cfg = self.config()
        self.preflight()
        candidate = self.prepare(args.tag, args.commit, args.image, cfg)
        self.transition(candidate, cfg)

    def rollback(self):
        cfg = self.config()
        self.preflight()
        previous = self.state()["previous"]
        require(previous, "No previous successful release")
        # Local retained image only: rollback remains possible during registry outage.
        self.transition(previous, cfg)

    def backup(self):
        require(not self.journal.exists(), "Unfinished transaction: run recover first")
        cfg = self.config()
        before = self.state()
        require(before["current"], "No active deployment to back up")
        atomic_json(self.journal, {"before": before, "candidate": before["current"]})
        try:
            self.compose(before["current"], "stop", "app")
            self.backup_stopped(cfg)
        finally:
            self.recover()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["init", "deploy", "rollback", "recover", "backup", "status", "render-nginx", "check-release"])
    parser.add_argument("--root", default="/opt/web-ssh-terminal")
    parser.add_argument("--tag")
    parser.add_argument("--commit")
    parser.add_argument("--image")
    args = parser.parse_args()
    os.umask(0o077)
    def interrupted(signum, frame):
        raise KeyboardInterrupt("Deployment interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    if args.command == "check-release":
        application(HERE, release=True)
        print("Release contract validated. Application tests are also required.")
        return
    controller = Controller(args.root)
    if args.command == "init":
        controller.init()
        return
    with controller.locked():
        if args.command == "deploy":
            require(args.tag and args.commit and args.image, "deploy requires --tag, --commit and --image")
            controller.deploy(args)
        elif args.command == "status":
            print(json.dumps({"deployment": controller.state(), "recovery_required": controller.journal.exists()}, indent=2))
        elif args.command == "render-nginx":
            cfg = controller.config()
            require(cfg["proxy"] == "nginx", "Config selects Traefik")
            template = (HERE / "deploy/nginx/webssh.conf.template").read_text()
            print(template.replace("@@DOMAIN@@", cfg["domain"]).replace("@@PORT@@", str(cfg["http_port"])))
        else:
            getattr(controller, args.command)()


if __name__ == "__main__":
    try:
        main()
    except (Error, OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        print("ERROR: " + str(error), file=sys.stderr)
        sys.exit(1)
