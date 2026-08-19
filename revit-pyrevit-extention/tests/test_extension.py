import pathlib
import unittest

ROOT = pathlib.Path(__file__).parents[1]
EXT = ROOT / "RevitMCP.extension"


class ExtensionTests(unittest.TestCase):
    def test_extension_is_ui_less(self):
        # The 3XN-RevitMCP ribbon (C# bridge) hosts the Python toggle; the
        # extension must not ship its own tab or buttons.
        self.assertFalse(list(EXT.glob("*.tab")))
        self.assertTrue((EXT / "startup.py").is_file())
        self.assertTrue((EXT / "lib" / "revit_mcp_provider.py").is_file())

    def test_no_routes_or_third_party_imports(self):
        text = "\n".join(path.read_text(encoding="utf-8") for path in EXT.rglob("*.py"))
        self.assertNotIn("pyrevit.routes", text.lower())
        self.assertNotIn("requests", text)
        self.assertIn("PythonRegistrationService", text)

    def test_ironpython_27_dialect_guard(self):
        provider = (EXT / "lib" / "revit_mcp_provider.py").read_text(encoding="utf-8")
        self.assertNotIn('f"', provider)
        self.assertIn("exec code in scope", provider)
        self.assertIn("compile(source", provider)

    def test_startup_registers_disabled(self):
        startup = (EXT / "startup.py").read_text(encoding="utf-8")
        self.assertIn("register_provider(False)", startup)

    def test_reload_delegate_rereads_module_from_disk(self):
        provider = (EXT / "lib" / "revit_mcp_provider.py").read_text(encoding="utf-8")
        self.assertIn("reload(revit_mcp_provider)", provider)
        self.assertIn("revit_mcp_provider.register_provider(True)", provider)


if __name__ == "__main__":
    unittest.main()
