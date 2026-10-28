import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("serverconfig", ROOT / "scripts/serverconfig.py")
sc = importlib.util.module_from_spec(SPEC)
sys.modules["serverconfig"] = sc
SPEC.loader.exec_module(sc)

AGE = "age1" + "q" * 58


class ServerConfigTests(unittest.TestCase):
    def test_parse_env_ignores_comments_and_strips_quotes(self):
        env = sc.parse_env("# c\nDOMAIN=ssh.sdslab.cat\nTZ='Europe/Madrid'\nX=\"y\"\n\nbad line\nEMPTY=\n")
        self.assertEqual(env, {"DOMAIN": "ssh.sdslab.cat", "TZ": "Europe/Madrid", "X": "y", "EMPTY": ""})

    def test_parse_env_does_not_expand_or_execute(self):
        self.assertEqual(sc.parse_env("A=$(id)\nB=$HOME")["A"], "$(id)")

    def test_parse_env_strips_inline_comments_but_keeps_quoted_hashes(self):
        env = sc.parse_env("WEBSSH_ROOT=/docker/ssh        # on s'instal·la (dades)\nTZ='Europe/Madrid' # zona\nP=\"a # b\"\nURL=http://x/#frag")
        self.assertEqual(env["WEBSSH_ROOT"], "/docker/ssh")
        self.assertEqual(env["TZ"], "Europe/Madrid")
        self.assertEqual(env["P"], "a # b")
        self.assertEqual(env["URL"], "http://x/#frag")  # # sense espai previ no és comentari

    def test_pick_subnet_falls_back_to_other_ranges_when_172_30_is_taken(self):
        picked = sc.pick_subnet(["172.30.0.0/16", "172.17.0.0/16"])
        self.assertEqual(picked, "172.29.50.0/24")
        everything_172 = sc.pick_subnet(["172.16.0.0/12"])
        self.assertTrue(everything_172.startswith("10.250."))

    def test_pick_subnet_skips_overlaps(self):
        self.assertEqual(sc.pick_subnet([]), "172.30.50.0/24")
        self.assertEqual(sc.pick_subnet(["172.30.50.0/24", "172.30.51.0/24"]), "172.30.52.0/24")
        self.assertEqual(sc.pick_subnet(["172.31.0.0/16"]), "172.30.50.0/24")
        with self.assertRaises(sc.ConfigError):
            sc.pick_subnet(["172.16.0.0/12", "10.0.0.0/8"])

    def test_gateway_is_first_host(self):
        self.assertEqual(sc.gateway("172.30.50.0/24"), "172.30.50.1")

    def test_build_config_requires_domain_and_recipient(self):
        with self.assertRaises(sc.ConfigError):
            sc.build_config({"DOMAIN": "ssh.sdslab.cat"}, "172.18.0.2", "172.30.50.0/24")

    def test_build_config_defaults_and_overrides(self):
        cfg = sc.build_config({"DOMAIN": "ssh.sdslab.cat", "BACKUP_RECIPIENT": AGE}, "172.18.0.2", "172.30.50.0/24")
        self.assertEqual(cfg["ssh_host"], "172.30.50.1")
        self.assertEqual(cfg["trusted_proxies"], ["172.18.0.2"])
        self.assertEqual((cfg["proxy"], cfg["proxy_network"], cfg["traefik_resolver"]), ("traefik", "traefik", "letsencrypt"))
        self.assertTrue(cfg["require_mfa"])
        over = sc.build_config({"DOMAIN": "a.b.cat", "BACKUP_RECIPIENT": AGE, "SSH_HOST": "10.0.0.5", "REQUIRE_MFA": "false",
                                "SSH_PORT": "2222"}, "172.18.0.2", "172.30.50.0/24")
        self.assertEqual((over["ssh_host"], over["ssh_port"], over["require_mfa"]), ("10.0.0.5", 2222, False))

    def test_config_passes_manage_py_validation(self):
        # Carrega manage.py sense fcntl (només Linux): el validador és pur.
        sys.modules.setdefault("fcntl", type(sys)("fcntl"))
        spec = importlib.util.spec_from_file_location("manage_mod", ROOT / "scripts/manage.py")
        manage = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(manage)
        cfg = sc.build_config({"DOMAIN": "ssh.sdslab.cat", "BACKUP_RECIPIENT": AGE}, "172.18.0.2", "172.30.50.0/24")
        manage.validate_config(cfg)

    def test_known_hosts_line_uses_exact_host_and_port_syntax(self):
        key = "ssh-ed25519 AAAAC3Nz root@host"
        self.assertEqual(sc.known_hosts_line("172.30.50.1", 22, key), "172.30.50.1 ssh-ed25519 AAAAC3Nz\n")
        self.assertEqual(sc.known_hosts_line("172.30.50.1", 2222, key), "[172.30.50.1]:2222 ssh-ed25519 AAAAC3Nz\n")
        with self.assertRaises(sc.ConfigError):
            sc.known_hosts_line("h", 22, "ssh-rsa AAAA x")

    def _render(self, own, used, existing_subnet=None):
        with tempfile.TemporaryDirectory() as d:
            env = Path(d, ".env")
            env.write_text(f"DOMAIN=ssh.sdslab.cat\nBACKUP_RECIPIENT={AGE}\n")
            existing = Path(d, "config.json")
            if existing_subnet:
                existing.write_text(json.dumps({"backend_subnet": existing_subnet}))
            out = Path(d, "out.json")
            sc.traefik_ip = lambda c, n: "172.18.0.9"
            sc.own_network_subnet = lambda *a, **k: own
            sc.used_subnets = lambda: used
            sc.cmd_render(type("A", (), {"env": str(env), "existing": str(existing), "out": str(out)}))
            return json.loads(out.read_text())["backend_subnet"]

    def test_render_keeps_the_subnet_of_the_apps_real_network(self):
        # La xarxa de l'app ja existeix (172.30.77.0/24) i figura entre les ocupades: es manté.
        self.assertEqual(self._render("172.30.77.0/24", ["172.30.77.0/24"]), "172.30.77.0/24")

    def test_render_ignores_sample_subnet_from_copied_example_config(self):
        # Regressió: init copia l'exemple (172.30.50.0/24) i aquesta rang està ocupada per un altre projecte.
        picked = self._render(None, ["172.30.0.0/16"], existing_subnet="172.30.50.0/24")
        self.assertNotEqual(picked, "172.30.50.0/24")
        self.assertEqual(picked, "172.29.50.0/24")

if __name__ == "__main__":
    unittest.main()
