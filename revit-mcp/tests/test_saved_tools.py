import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from revit_mcp.runtime_settings import RuntimeSettings
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
            self.assertEqual(["list_levels"], [t["id"] for t in listing["tools"]])
            self.assertTrue(listing["tools"][0]["enabled"])
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

    def test_folder_groups_are_part_of_tool_id(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            group = root / "qa" / "model"
            group.mkdir(parents=True)
            write_tool(group, "list_levels", manifest("list_levels"))
            listing = list_saved_tools(root)
            self.assertEqual("qa/model/list_levels", listing["tools"][0]["id"])
            self.assertEqual("qa/model", listing["tools"][0]["group"])
            self.assertEqual("qa/model/list_levels", load_saved_tool("qa/model/list_levels", root).id)

    def test_group_marker_disables_every_tool_below_it(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            group = root / "qa"
            group.mkdir()
            write_tool(group, "one", manifest("one"))
            nested = group / "model"
            nested.mkdir()
            write_tool(nested, "two", manifest("two"))
            (group / ".disabled").write_text("", encoding="utf-8")
            listing = list_saved_tools(root)
            self.assertEqual([False, False], [tool["enabled"] for tool in listing["tools"]])
            with self.assertRaises(PermissionError):
                load_saved_tool("qa/one", root)
            self.assertEqual("qa/one", load_saved_tool("qa/one", root, allow_disabled=True).id)

    def test_tool_marker_disables_only_one_tool(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            write_tool(root, "one", manifest("one"))
            write_tool(root, "two", manifest("two"))
            (root / "one.disabled").write_text("", encoding="utf-8")
            listing = {tool["id"]: tool for tool in list_saved_tools(root)["tools"]}
            self.assertFalse(listing["one"]["enabled"])
            self.assertTrue(listing["two"]["enabled"])

    def test_multiple_roots_are_merged_first_root_wins(self):
        with tempfile.TemporaryDirectory() as folder:
            primary = Path(folder) / "primary"
            extra = Path(folder) / "extra"
            primary.mkdir()
            extra.mkdir()
            write_tool(primary, "shared", manifest("shared"), "_result = {'from': 'primary'}")
            write_tool(extra, "shared", manifest("shared"), "_result = {'from': 'extra'}")
            write_tool(extra, "extra_only", manifest("extra_only"))
            fake = RuntimeSettings(Path("settings.json"), primary, frozenset(), saved_tools_paths=(extra,))
            with patch("revit_mcp.saved_tools.load_settings", return_value=fake):
                listing = list_saved_tools()
                self.assertEqual([str(primary), str(extra)], listing["roots"])
                by_id = {tool["id"]: tool for tool in listing["tools"]}
                self.assertEqual({"shared", "extra_only"}, set(by_id))
                self.assertEqual(str(primary), by_id["shared"]["root"])
                self.assertEqual(str(extra), by_id["extra_only"]["root"])
                self.assertEqual([{"id": "shared", "root": str(extra), "shadowed_by": str(primary)}],
                                 listing["shadowed"])
                self.assertEqual("_result = {'from': 'primary'}", load_saved_tool("shared").source)
                self.assertEqual("extra_only", load_saved_tool("extra_only").id)

    def test_disabled_tool_does_not_fall_through_to_later_root(self):
        with tempfile.TemporaryDirectory() as folder:
            primary = Path(folder) / "primary"
            extra = Path(folder) / "extra"
            primary.mkdir()
            extra.mkdir()
            write_tool(primary, "shared", manifest("shared"))
            write_tool(extra, "shared", manifest("shared"))
            (primary / "shared.disabled").write_text("", encoding="utf-8")
            fake = RuntimeSettings(Path("settings.json"), primary, frozenset(), saved_tools_paths=(extra,))
            with patch("revit_mcp.saved_tools.load_settings", return_value=fake):
                with self.assertRaises(PermissionError):
                    load_saved_tool("shared")

    def test_disabled_path_disables_every_tool_in_that_root(self):
        import os
        with tempfile.TemporaryDirectory() as folder:
            primary = Path(folder) / "primary"
            extra = Path(folder) / "extra"
            primary.mkdir()
            extra.mkdir()
            write_tool(primary, "keeper", manifest("keeper"))
            write_tool(extra, "banned", manifest("banned"))
            disabled = frozenset({os.path.normcase(os.path.normpath(str(extra)))})
            fake = RuntimeSettings(Path("settings.json"), primary, frozenset(),
                                   saved_tools_paths=(extra,), disabled_tool_paths=disabled)
            with patch("revit_mcp.saved_tools.load_settings", return_value=fake):
                listing = {tool["id"]: tool for tool in list_saved_tools()["tools"]}
                self.assertTrue(listing["keeper"]["enabled"])
                self.assertFalse(listing["banned"]["enabled"])
                self.assertEqual("path disabled", listing["banned"]["disabled_reason"])
                with self.assertRaises(PermissionError):
                    load_saved_tool("banned")
                self.assertEqual("keeper", load_saved_tool("keeper").id)

    def test_missing_extra_root_is_skipped(self):
        with tempfile.TemporaryDirectory() as folder:
            primary = Path(folder) / "primary"
            primary.mkdir()
            write_tool(primary, "one", manifest("one"))
            absent = Path(folder) / "does_not_exist"
            fake = RuntimeSettings(Path("settings.json"), primary, frozenset(), saved_tools_paths=(absent,))
            with patch("revit_mcp.saved_tools.load_settings", return_value=fake):
                listing = list_saved_tools()
                self.assertEqual(["one"], [tool["id"] for tool in listing["tools"]])
                self.assertEqual([str(primary), str(absent)], listing["roots"])
                self.assertEqual([], listing["shadowed"])

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
