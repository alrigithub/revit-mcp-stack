import json
import tempfile
import unittest
from pathlib import Path

from revit_mcp.saved_tools import list_saved_tools, load_saved_tool, validate_arguments


def write_tool(root: Path, name: str, manifest: dict, source: str = "_result = {}") -> None:
    (root / ("%s.json" % name)).write_text(json.dumps(manifest), encoding="utf-8")
    suffix = ".cs" if manifest.get("engine") == "csharp" else ".py"
    (root / (name + suffix)).write_text(source, encoding="utf-8")


def manifest(name: str, **overrides) -> dict:
    base = {"manifest_version": 1, "name": name, "description": "A test tool.", "engine": "python",
            "transaction_mode": "read", "timeout_ms": 30000, "params": []}
    base.update(overrides)
    return base


class SavedToolsTests(unittest.TestCase):
    def test_valid_tool_is_listed_and_loaded(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            write_tool(root, "list_levels", manifest("list_levels"), "_result = {'ok': True}")
            listing = list_saved_tools(root)
            self.assertEqual(["list_levels"], [t["name"] for t in listing["tools"]])
            self.assertEqual([], listing["invalid"])
            tool = load_saved_tool("list_levels", root)
            self.assertEqual("read", tool.transaction_mode)
            self.assertEqual("_result = {'ok': True}", tool.source)

    def test_invalid_manifest_is_reported_not_fatal(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            write_tool(root, "good", manifest("good"))
            write_tool(root, "bad", manifest("bad", transaction_mode="yolo"))
            (root / "broken.json").write_text("{not json", encoding="utf-8")
            listing = list_saved_tools(root)
            self.assertEqual(["good"], [t["name"] for t in listing["tools"]])
            self.assertEqual({"bad.json", "broken.json"}, {item["file"] for item in listing["invalid"]})

    def test_name_must_match_filename_stem(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            write_tool(root, "actual", manifest("declared"))
            self.assertEqual(1, len(list_saved_tools(root)["invalid"]))

    def test_missing_source_file_is_invalid(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "lonely.json").write_text(json.dumps(manifest("lonely")), encoding="utf-8")
            self.assertIn("missing source", list_saved_tools(root)["invalid"][0]["reason"])

    def test_unknown_tool_raises_lookup_error(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(LookupError):
                load_saved_tool("absent", Path(folder))

    def test_traversal_style_names_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            for bad in ("../evil", "UPPER", "has space", ""):
                with self.assertRaises(ValueError):
                    load_saved_tool(bad, Path(folder))

    def test_argument_validation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            params = [{"name": "limit", "type": "integer", "description": "Cap.", "required": False, "default": 100},
                      {"name": "prefix", "type": "string", "description": "Filter.", "required": True}]
            write_tool(root, "query", manifest("query", params=params))
            tool = load_saved_tool("query", root)
            self.assertEqual({"limit": 100, "prefix": "L"}, validate_arguments(tool, {"prefix": "L"}))
            self.assertEqual({"limit": 5, "prefix": "L"}, validate_arguments(tool, {"prefix": "L", "limit": 5}))
            with self.assertRaises(ValueError):
                validate_arguments(tool, {})  # missing required
            with self.assertRaises(ValueError):
                validate_arguments(tool, {"prefix": "L", "limit": "5"})  # wrong type
            with self.assertRaises(ValueError):
                validate_arguments(tool, {"prefix": "L", "limit": True})  # bool is not integer
            with self.assertRaises(ValueError):
                validate_arguments(tool, {"prefix": "L", "extra": 1})  # unknown param


if __name__ == "__main__":
    unittest.main()
