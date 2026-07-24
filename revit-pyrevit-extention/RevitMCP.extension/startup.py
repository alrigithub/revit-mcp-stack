# -*- coding: utf-8 -*-
"""Register a disabled, generation-pinned IronPython provider with the loaded bridge."""
import os
import sys

_root = os.path.dirname(__file__)
_lib = os.path.join(_root, "lib")
if _lib not in sys.path:
    sys.path.insert(0, _lib)

from revit_mcp_provider import register_provider

# Provider registration is the critical startup action.  UI synchronization is
# best-effort and must not prevent the persistent IronPython delegates from
# being registered with the C# bridge.
register_provider(False)

try:
    from revit_mcp_ui import refresh_controls
    refresh_controls()
except Exception as error:
    print("RevitMCP UI refresh skipped: " + str(error))
