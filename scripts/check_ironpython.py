# -*- coding: utf-8 -*-
"""Reject common Python 3 constructs that crash in the IronPython 2.7 provider."""
import ast
import re
import sys

PY3_ONLY_IMPORTS = {
    "asyncio", "builtins", "concurrent", "configparser", "dataclasses",
    "graphlib", "queue", "secrets", "statistics", "typing", "venv", "zoneinfo",
}


def check(path):
    issues = []
    with open(path, "rb") as handle:
        source = handle.read().decode("utf-8", "replace")
    # The persistent provider itself uses Python 2's ``exec code in scope``.
    # Replace only that statement for the Python 3 AST pass while preserving lines.
    parse_source = re.sub(r"(?m)^(\s*)exec\s+.+\s+in\s+.+$", r"\1pass", source)
    try:
        tree = ast.parse(parse_source)
    except SyntaxError as error:
        return ["%s:%s syntax: %s" % (path, error.lineno, error.msg)]
    for node in ast.walk(tree):
        line = getattr(node, "lineno", "?")
        if isinstance(node, ast.JoinedStr):
            issues.append("%s:%s f-string; use .format()" % (path, line))
        elif isinstance(node, ast.NamedExpr):
            issues.append("%s:%s walrus operator is not supported" % (path, line))
        elif isinstance(node, ast.AnnAssign):
            issues.append("%s:%s variable annotations are not supported" % (path, line))
        elif isinstance(node, ast.AsyncFunctionDef):
            issues.append("%s:%s async def is not supported" % (path, line))
        elif isinstance(node, (ast.FunctionDef, ast.Lambda)):
            if getattr(node.args, "kwonlyargs", None):
                issues.append("%s:%s keyword-only arguments are not supported" % (path, line))
            if getattr(node.args, "posonlyargs", None):
                issues.append("%s:%s positional-only arguments are not supported" % (path, line))
        elif isinstance(node, ast.Import):
            for entry in node.names:
                if entry.name.split(".")[0] in PY3_ONLY_IMPORTS:
                    issues.append("%s:%s import %s is unavailable" % (path, line, entry.name))
        elif isinstance(node, ast.ImportFrom):
            if (node.module or "").split(".")[0] in PY3_ONLY_IMPORTS:
                issues.append("%s:%s import from %s is unavailable" % (path, line, node.module))
    return issues


def main(paths):
    issues = []
    for path in paths:
        issues.extend(check(path))
    for issue in issues:
        sys.stderr.write("IPY27: " + issue + "\n")
    return 1 if issues else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
