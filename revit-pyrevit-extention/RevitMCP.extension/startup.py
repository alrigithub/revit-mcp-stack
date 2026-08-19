# -*- coding: utf-8 -*-
"""Register a disabled, generation-pinned IronPython provider with the loaded bridge.

The extension has no ribbon UI of its own: the 3XN-RevitMCP tab (C# bridge)
hosts the Python toggle, which enables, disables, and reloads this provider
through the registered delegates.

Add-in load order is not guaranteed: pyRevit runs this script during its own
load, which can be before the bridge add-in has loaded (alphabetically it is).
In that case registration retries on Idling until the bridge is up.
"""
import os
import sys
import traceback

_root = os.path.dirname(__file__)
_lib = os.path.join(_root, "lib")
if _lib not in sys.path:
    sys.path.insert(0, _lib)

from revit_mcp_provider import register_provider

# The retry must run exactly once after the bridge appears. Idling fires
# continuously, so the handler needs a done-flag (a failed detach must never
# re-register) and must be subscribed as an explicit EventHandler delegate:
# IronPython silently no-ops "event -= plain_function" when the delegate it
# builds for removal is not the instance that was added.
_state = {"done": False, "left": 600, "handler": None}


def _log_failure():
    log_dir = os.path.join(os.environ.get("LOCALAPPDATA", _root), "RevitMcp")
    if not os.path.isdir(log_dir):
        os.makedirs(log_dir)
    with open(os.path.join(log_dir, "startup-error.log"), "w") as handle:
        handle.write(traceback.format_exc())


def _detach(sender):
    if _state["handler"] is None:
        return
    try:
        sender.Idling -= _state["handler"]
    except Exception:
        pass
    _state["handler"] = None


def _retry(sender, args):
    if _state["done"]:
        _detach(sender)
        return
    try:
        register_provider(False)
    except Exception:
        _state["left"] -= 1
        if _state["left"] > 0:
            return
        _log_failure()
    _state["done"] = True
    _detach(sender)


try:
    register_provider(False)
except Exception:
    try:
        from System import EventHandler
        from Autodesk.Revit.UI.Events import IdlingEventArgs
        from pyrevit import HOST_APP
        _state["handler"] = EventHandler[IdlingEventArgs](_retry)
        HOST_APP.uiapp.Idling += _state["handler"]
    except Exception:
        _log_failure()
        raise
