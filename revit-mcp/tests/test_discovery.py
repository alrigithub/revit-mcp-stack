import json
import tempfile
import unittest
from pathlib import Path

from revit_mcp.discovery import list_instances, pyrevit_install


class DiscoveryTests(unittest.TestCase):
    def test_pid_reuse_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            payload = {"pid": 7, "process_start_utc_ticks": 123, "revit_year": "2026", "protocol_version": "0.1", "pipe_name": "p", "bridge_state": "on", "instance_nonce": "n", "written_utc": "now"}
            (root / "7.json").write_text(json.dumps(payload), encoding="utf-8")
            self.assertEqual([], list_instances(root, lambda _: 456))
            self.assertEqual(1, len(list_instances(root, lambda _: 123)))

    def test_bridge_off_record_is_ignored(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            payload = {"pid": 7, "process_start_utc_ticks": 123, "revit_year": "2026", "protocol_version": "0.1", "pipe_name": "p", "bridge_state": "off", "instance_nonce": "n", "written_utc": "now"}
            (root / "7.json").write_text(json.dumps(payload), encoding="utf-8")
            self.assertEqual([], list_instances(root, lambda _: 123))


class PyRevitInstallTests(unittest.TestCase):
    def test_nothing_installed(self):
        with tempfile.TemporaryDirectory() as folder:
            info = pyrevit_install(Path(folder))
            self.assertFalse(info["installed"])
            self.assertIsNone(info["version"])

    def test_version_read_from_configured_clone(self):
        with tempfile.TemporaryDirectory() as folder:
            appdata = Path(folder)
            clone = appdata / "clones" / "master"
            (clone / "pyrevitlib" / "pyrevit").mkdir(parents=True)
            (clone / "pyrevitlib" / "pyrevit" / "version").write_text("6.4.0.26100+0515", encoding="utf-8")
            (appdata / "pyRevit").mkdir()
            (appdata / "pyRevit" / "pyRevit_config.ini").write_text(
                '[environment]\nclones = {"master":%s}\n' % json.dumps(str(clone)), encoding="utf-8")
            info = pyrevit_install(appdata)
            self.assertTrue(info["installed"])
            self.assertEqual("6.4.0.26100+0515", info["version"])

    def test_default_master_clone_fallback(self):
        with tempfile.TemporaryDirectory() as folder:
            appdata = Path(folder)
            (appdata / "pyRevit-Master" / "pyrevitlib" / "pyrevit").mkdir(parents=True)
            (appdata / "pyRevit-Master" / "pyrevitlib" / "pyrevit" / "version").write_text("6.4.0", encoding="utf-8")
            self.assertEqual("6.4.0", pyrevit_install(appdata)["version"])


if __name__ == "__main__":
    unittest.main()
