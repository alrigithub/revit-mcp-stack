import ast
import unittest
from pathlib import Path


class ToolGuidanceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = Path(__file__).parents[1] / "src" / "revit_mcp" / "server.py"
        cls.text = source.read_text(encoding="utf-8")
        cls.tree = ast.parse(cls.text)

    def test_global_instructions_teach_runtime_constraints(self):
        for phrase in ("ONE run_python or run_csharp", "IronPython 2.7", "revit_busy", "transaction_mode"):
            self.assertIn(phrase, self.text)

    def test_dynamic_tools_teach_batching(self):
        functions = {node.name: ast.get_docstring(node) for node in self.tree.body if isinstance(node, ast.FunctionDef)}
        self.assertIn("ONE batched", functions["run_python"])
        self.assertIn("ONE C#", functions["run_csharp"])
        self.assertIn("single run_python/run_csharp", functions["execute_batch"])


if __name__ == "__main__":
    unittest.main()
