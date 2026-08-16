# -*- coding: utf-8 -*-
"""Feed Pyright and IronPython 2.7 findings back after Claude edits a script."""
import json
import os
import shutil
import subprocess
import sys


def is_ironpython_script(path, root):
    extension = os.path.join(root, "revit-pyrevit-extention", "RevitMCP.extension")
    if os.path.normcase(os.path.abspath(path)).startswith(os.path.normcase(os.path.abspath(extension)) + os.sep):
        return True
    settings_path = os.path.join(os.environ.get("LOCALAPPDATA", ""), "RevitMcp", "settings.json")
    tools_root = os.path.join(os.environ.get("LOCALAPPDATA", ""), "RevitMcp", "tools")
    try:
        with open(settings_path, "r") as handle:
            tools_root = json.load(handle).get("saved_tools_root") or tools_root
    except Exception:
        pass
    return os.path.normcase(os.path.abspath(path)).startswith(os.path.normcase(os.path.abspath(tools_root)) + os.sep)


def run(command, root):
    result = subprocess.run(command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return result.returncode, result.stdout.decode("utf-8", "replace")


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0
    path = (payload.get("tool_input") or {}).get("file_path") or ""
    if not path.endswith(".py") or not os.path.isfile(path):
        return 0

    root = os.environ.get("CLAUDE_PROJECT_DIR") or os.getcwd()
    findings = []
    pyright = shutil.which("pyright")
    if pyright:
        code, output = run([pyright, path], root)
        if code:
            findings.append("[pyright]\n" + output.strip())
    if is_ironpython_script(path, root):
        checker = os.path.join(root, ".lint", "check_ipy27.py")
        code, output = run([sys.executable, checker, path], root)
        if code:
            findings.append("[ironpython-2.7]\n" + output.strip())
    if not findings:
        return 0
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PostToolUse",
            "additionalContext": "Fix these findings before finishing:\n" + "\n".join(findings),
        }
    }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
