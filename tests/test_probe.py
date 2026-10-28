import importlib.util
import os
import ssl
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.modules.setdefault("fcntl", type(sys)("fcntl"))  # només Linux; el sondeig no el necessita
SPEC = importlib.util.spec_from_file_location("manage_probe", ROOT / "scripts/manage.py")
m = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m)


class FakeResponse:
    def __init__(self, url, body):
        self._url, self._body = url, body

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False

    def geturl(self):
        return self._url

    def read(self, n):
        return self._body


class ProbeTests(unittest.TestCase):
    def setUp(self):
        self.c = m.Controller.__new__(m.Controller)  # probe() no usa l'estat de la instància
        self.item = {"domain": "ssh.example.test", "commit": "a" * 40}

    def test_retries_until_the_certificate_appears(self):
        url = "https://ssh.example.test/health/ready"
        ok = FakeResponse(url, ('{"status": "ok", "revision": "' + "a" * 40 + '"}').encode())
        calls = [ssl.SSLCertVerificationError("self-signed certificate"), ssl.SSLCertVerificationError("x"), ok]

        def fake(*a, **k):
            r = calls.pop(0)
            if isinstance(r, Exception):
                raise r
            return r

        with patch.dict(os.environ, {"WEBSSH_PROBE_SECONDS": "60"}), patch.object(m.urllib.request, "urlopen", fake), \
                patch.object(m.time, "sleep"):
            self.c.probe(self.item)
        self.assertEqual(calls, [])

    def test_gives_up_with_an_actionable_hint_for_certificate_errors(self):
        def always(*a, **k):
            raise ssl.SSLCertVerificationError("CERTIFICATE_VERIFY_FAILED self-signed certificate")

        with patch.dict(os.environ, {"WEBSSH_PROBE_SECONDS": "0"}), patch.object(m.urllib.request, "urlopen", always), \
                patch.object(m.time, "sleep"):
            with self.assertRaises(m.Error) as ctx:
                self.c.probe(self.item)
        self.assertIn("default certificate", str(ctx.exception))
        self.assertIn("acme", str(ctx.exception).lower())

    def test_wrong_revision_is_never_accepted(self):
        url = "https://ssh.example.test/health/ready"
        bad = FakeResponse(url, ('{"status": "ok", "revision": "' + "b" * 40 + '"}').encode())
        with patch.dict(os.environ, {"WEBSSH_PROBE_SECONDS": "0"}), patch.object(m.urllib.request, "urlopen", lambda *a, **k: bad), \
                patch.object(m.time, "sleep"):
            with self.assertRaises(m.Error) as ctx:
                self.c.probe(self.item)
        self.assertIn("revision mismatch", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
