import importlib.util
import sys
import tempfile
import unittest
from calendar import timegm
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("backup_tools", ROOT / "scripts/backup_tools.py")
bt = importlib.util.module_from_spec(SPEC)
sys.modules["backup_tools"] = bt
SPEC.loader.exec_module(bt)

NOW = timegm((2026, 11, 18, 12, 0, 0))


def make(directory: Path, stamps, extra=()):
    for i, stamp in enumerate(stamps):
        (directory / f"{stamp}-{i:08x}.tar.age").write_bytes(b"x")
    for name in extra:
        (directory / name).write_bytes(b"x")


class BackupToolsTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

    def test_prune_keeps_newest_and_ignores_other_files(self):
        make(self.dir, ["20261101T030000Z", "20261102T030000Z", "20261103T030000Z", "20261104T030000Z"],
             extra=["20261001T030000Z-deadbeef.tar.age.partial", "notes.txt"])
        gone = bt.prune(self.dir, 2)
        self.assertEqual([p.name[:8] for p in gone], ["20261101", "20261102"])
        self.assertEqual([p.name[:8] for p in bt.backups(self.dir)], ["20261103", "20261104"])
        self.assertTrue((self.dir / "20261001T030000Z-deadbeef.tar.age.partial").exists())
        self.assertTrue((self.dir / "notes.txt").exists())

    def test_prune_with_fewer_than_keep_removes_nothing(self):
        make(self.dir, ["20261101T030000Z"])
        self.assertEqual(bt.prune(self.dir, 5), [])

    def test_prune_refuses_to_keep_zero(self):
        make(self.dir, ["20261101T030000Z"])
        with self.assertRaises(ValueError):
            bt.prune(self.dir, 0)
        self.assertEqual(len(bt.backups(self.dir)), 1)

    def test_check_ok_uses_filename_time(self):
        make(self.dir, ["20261118T020000Z"])  # 10 h abans de NOW
        self.assertIn("10.0 h", bt.check(self.dir, 36, now=NOW))

    def test_check_fails_when_latest_is_too_old(self):
        make(self.dir, ["20261028T155957Z"])
        with self.assertRaises(RuntimeError):
            bt.check(self.dir, 36, now=NOW)

    def test_check_fails_without_backups(self):
        with self.assertRaises(RuntimeError):
            bt.check(self.dir, 36, now=NOW)


if __name__ == "__main__":
    unittest.main()
