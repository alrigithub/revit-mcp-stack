import asyncio
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from revit_mcp.runtime_settings import RuntimeSettings, load_settings


class RuntimeSettingsTests(unittest.TestCase):
    def test_defaults_to_tools_below_base_root(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            settings = load_settings(base)
            self.assertEqual(base / "tools", settings.saved_tools_root)
            self.assertEqual(frozenset(), settings.disabled_mcp_tools)
            self.assertIsNone(settings.error)

    def test_custom_root_and_disabled_tools_are_loaded(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            custom = base / "custom"
            (base / "settings.json").write_text(json.dumps({
                "saved_tools_root": str(custom),
                "disabled_mcp_tools": ["run_csharp", "export_view"],
            }), encoding="utf-8")
            settings = load_settings(base)
            self.assertEqual(custom, settings.saved_tools_root)
            self.assertEqual(frozenset({"run_csharp", "export_view"}), settings.disabled_mcp_tools)

    def test_invalid_settings_fall_back_without_crashing_server(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            (base / "settings.json").write_text('{"saved_tools_root":"relative"}', encoding="utf-8")
            settings = load_settings(base)
            self.assertEqual(base / "tools", settings.saved_tools_root)
            self.assertIsNotNone(settings.error)

    def test_disabled_mcp_tools_are_hidden_and_rejected(self):
        from revit_mcp import server

        fake = RuntimeSettings(Path("settings.json"), Path("tools"), frozenset({"run_csharp"}))
        with patch.object(server, "load_settings", return_value=fake):
            names = {tool.name for tool in asyncio.run(server.mcp.list_tools())}
            self.assertNotIn("run_csharp", names)
            self.assertIn("run_python", names)
            with self.assertRaises(PermissionError):
                asyncio.run(server.mcp.call_tool("run_csharp", {}))


if __name__ == "__main__":
    unittest.main()
