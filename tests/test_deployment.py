"""Failure-oriented tests; no real application, registry or SSH server is used."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("manage", Path(__file__).parents[1] / "scripts/manage.py")
m = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m)


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "installation"
        self.root.mkdir(mode=0o700)
        self.c = m.Controller(self.root)
        self.old = dict(id="old", tag="v1.0.0", commit="a" * 40, image="image-old", data_epoch=1, proxy="nginx", domain="ssh.example.com")
        self.new = dict(self.old, id="new", tag="v1.0.1", commit="b" * 40, image="image-new")
        self.before = {"current": self.old, "previous": None}
        m.atomic_json(self.c.statefile, self.before)
        self.cfg = json.loads((m.HERE / "deploy/config.example.json").read_text())
        self.cfg["backup_recipient"] = "age1" + "a" * 58

    def tearDown(self):
        self.temp.cleanup()

    def test_scaffold_cannot_release_or_deploy(self):
        (self.root / "deploy").mkdir()
        (self.root / "deploy/application.json").write_text('{"status":"scaffold"}')
        with self.assertRaisesRegex(m.Error, "not implemented"):
            m.application(self.root, release=True)

    def test_invalid_config_cannot_inject_proxy_or_environment(self):
        for key, value in [("domain", "example.com` || true"), ("proxy_network", "x\nAPP_IMAGE=bad"),
                           ("trusted_proxies", ["0.0.0.0/0"]), ("http_port", True), ("extra", "bad")]:
            with self.subTest(key=key), self.assertRaises((m.Error, ValueError)):
                m.validate_config(dict(self.cfg, **{key: value}))

    def test_mutable_image_branch_and_short_commit_rejected_before_checkout(self):
        for tag, commit, image in [("main", "a" * 40, "bad"), ("v1.0.0", "abc", "bad"),
                                   ("v1.0.0", "a" * 40, "ghcr.io/joseptomascomellas/ssh:latest")]:
            with self.assertRaises(m.Error), patch.object(self.c, "checkout") as checkout:
                self.c.prepare(tag, commit, image, self.cfg)
            checkout.assert_not_called()

    def test_success_updates_state_only_after_start_and_backup(self):
        events = []
        with patch.object(self.c, "compose", side_effect=lambda *a: events.append("stop")), \
             patch.object(self.c, "backup_stopped", side_effect=lambda *a: events.append("backup")), \
             patch.object(self.c, "start", side_effect=lambda *a: events.append("start")):
            self.c.transition(self.new, self.cfg)
        self.assertEqual(events, ["stop", "backup", "start"])
        self.assertEqual(self.c.state(), {"current": self.new, "previous": self.old})
        self.assertFalse(self.c.journal.exists())

    def test_unhealthy_update_restores_old_configuration_and_state(self):
        with patch.object(self.c, "compose") as compose, patch.object(self.c, "backup_stopped"), \
             patch.object(self.c, "start", side_effect=m.Error("unhealthy")), patch.object(self.c, "probe") as probe:
            with self.assertRaises(m.Error):
                self.c.transition(self.new, self.cfg)
        self.assertEqual(self.c.state(), self.before)
        self.assertIn("--force-recreate", compose.call_args.args)
        probe.assert_called_once_with(self.old)
        self.assertFalse(self.c.journal.exists())

    def test_backup_failure_restores_old_without_starting_candidate(self):
        with patch.object(self.c, "compose"), patch.object(self.c, "backup_stopped", side_effect=m.Error("disk full")), \
             patch.object(self.c, "probe"), patch.object(self.c, "start") as start:
            with self.assertRaises(m.Error):
                self.c.transition(self.new, self.cfg)
        start.assert_not_called()
        self.assertEqual(self.c.state(), self.before)

    def test_failed_recovery_retains_journal_for_manual_retry(self):
        with patch.object(self.c, "compose"), patch.object(self.c, "backup_stopped"), \
             patch.object(self.c, "start", side_effect=m.Error("bad")), patch.object(self.c, "probe", side_effect=m.Error("offline")):
            with self.assertRaises(m.Error):
                self.c.transition(self.new, self.cfg)
        self.assertTrue(self.c.journal.exists())
        with patch.object(self.c, "compose"), patch.object(self.c, "probe"):
            self.c.recover()
        self.assertFalse(self.c.journal.exists())

    def test_first_install_failure_stops_candidate_without_deleting_data(self):
        m.atomic_json(self.c.statefile, {"current": None, "previous": None})
        (self.root / "data").mkdir()
        sentinel = self.root / "data/sentinel"
        sentinel.write_text("keep")
        with patch.object(self.c, "start", side_effect=m.Error("bad")), patch.object(self.c, "compose") as compose:
            with self.assertRaises(m.Error):
                self.c.transition(self.new, self.cfg)
        compose.assert_called_once_with(self.new, "down")
        self.assertEqual(sentinel.read_text(), "keep")

    def test_incompatible_schema_and_proxy_changes_never_stop_current(self):
        for candidate in [dict(self.new, data_epoch=2), dict(self.new, proxy="traefik"), dict(self.new, domain="other.example.com")]:
            with patch.object(self.c, "compose") as compose, self.assertRaises(m.Error):
                self.c.transition(candidate, self.cfg)
            compose.assert_not_called()
            self.assertFalse(self.c.journal.exists())

    def test_crash_journal_blocks_new_deploy(self):
        m.atomic_json(self.c.journal, {"before": self.before, "candidate": self.new})
        with self.assertRaisesRegex(m.Error, "Unfinished"), patch.object(self.c, "start") as start:
            self.c.transition(self.new, self.cfg)
        start.assert_not_called()

    def test_rollback_uses_retained_release_and_saves_current(self):
        m.atomic_json(self.c.statefile, {"current": self.new, "previous": self.old})
        with patch.object(self.c, "config", return_value=self.cfg), patch.object(self.c, "preflight"), \
             patch.object(self.c, "compose"), patch.object(self.c, "backup_stopped"), patch.object(self.c, "start") as start:
            self.c.rollback()
        start.assert_called_once_with(self.old)
        self.assertEqual(self.c.state(), {"current": self.old, "previous": self.new})

    def test_compose_does_not_inherit_image_or_compose_file(self):
        with patch.dict(os.environ, {"APP_IMAGE": "evil", "COMPOSE_FILE": "evil.yaml"}), patch.object(m, "run") as run:
            self.c.compose(self.old, "config", "--quiet")
        self.assertNotIn("APP_IMAGE", run.call_args.kwargs["env"])
        self.assertNotIn("COMPOSE_FILE", run.call_args.kwargs["env"])

    def test_secret_permissions_and_symlinks_rejected(self):
        secret = self.root / "secret"
        secret.write_text("dummy")
        secret.chmod(0o644)
        with self.assertRaises(m.Error):
            m.safe_file(secret, os.getuid(), 0o400)
        secret.chmod(0o400)
        m.safe_file(secret, os.getuid(), 0o400)
        link = self.root / "link"
        link.symlink_to(secret)
        with self.assertRaises(m.Error):
            m.safe_file(link, os.getuid(), 0o400)

    def test_real_encrypted_backup_can_be_decrypted(self):
        self.assertIsNotNone(shutil.which("age"), "Install age to run backup integration test")
        key = self.root / "test-identity"
        subprocess.run(["age-keygen", "-o", str(key)], check=True, capture_output=True)
        recipient = subprocess.check_output(["age-keygen", "-y", str(key)], text=True).strip()
        for name in ("data", "secrets", "backups"):
            (self.root / name).mkdir()
        (self.root / "data/example.db").write_text("test database")
        (self.root / "secrets/app_key").write_text("test secret")
        (self.root / "config.json").write_text("{}")
        self.c.backup_stopped(dict(self.cfg, backup_recipient=recipient))
        backup = next((self.root / "backups").glob("*.tar.age"))
        raw = subprocess.check_output(["age", "-d", "-i", str(key), str(backup)])
        self.assertIn(b"test database", raw)
        self.assertIn(b"test secret", raw)
        self.assertNotIn(b"test secret", backup.read_bytes())
        self.assertFalse(list((self.root / "backups").glob("*.tar")))

    def test_git_tag_commit_mismatch_and_retag_are_rejected(self):
        remote = self.root / "test-origin"
        subprocess.run(["git", "init", str(remote)], check=True, capture_output=True)
        def git(*args):
            return subprocess.check_output(["git", "-C", str(remote), *args], text=True, stderr=subprocess.DEVNULL).strip()
        git("config", "user.email", "test@example.invalid")
        git("config", "user.name", "Test")
        (remote / "README").write_text("first")
        git("add", ".")
        git("commit", "-m", "first")
        commit = git("rev-parse", "HEAD")
        git("tag", "-a", "v1.0.0", "-m", "first")
        with patch.object(m, "REPO", str(remote)):
            with self.assertRaisesRegex(m.Error, "expected commit"):
                self.c.checkout("v1.0.0", "0" * 40, self.root / "bad", False)
            self.c.checkout("v1.0.0", commit, self.root / "good", False)
            (remote / "README").write_text("second")
            git("add", ".")
            git("commit", "-m", "second")
            git("tag", "-f", "-a", "v1.0.0", "-m", "retag")
            with self.assertRaises(subprocess.CalledProcessError):
                self.c.checkout("v1.0.0", git("rev-parse", "HEAD"), self.root / "retag", False)


if __name__ == "__main__":
    unittest.main()
