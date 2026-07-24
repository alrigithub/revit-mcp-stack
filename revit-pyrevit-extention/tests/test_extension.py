import pathlib
import unittest

ROOT = pathlib.Path(__file__).parents[1]
EXT = ROOT / "RevitMCP.extension"


class ExtensionTests(unittest.TestCase):
    def test_required_controls_exist(self):
        panel = EXT / "Revit MCP.tab" / "Runtime.panel"
        self.assertTrue((panel / "Python ON.pushbutton" / "script.py").is_file())
        self.assertTrue((panel / "Python OFF.pushbutton" / "script.py").is_file())

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

    def test_python_on_self_registers_in_persistent_engine(self):
        button = EXT / "Revit MCP.tab" / "Runtime.panel" / "Python ON.pushbutton"
        script = (button / "script.py").read_text(encoding="utf-8")
        config = (button / "bundle.yaml").read_text(encoding="utf-8")
        self.assertIn("register_provider(True)", script)
        self.assertIn("persistent: true", config)

    def test_startup_registers_before_optional_ui_refresh(self):
        startup = (EXT / "startup.py").read_text(encoding="utf-8")
        self.assertLess(startup.index("register_provider(False)"), startup.index("from revit_mcp_ui"))


if __name__ == "__main__":
    unittest.main()
