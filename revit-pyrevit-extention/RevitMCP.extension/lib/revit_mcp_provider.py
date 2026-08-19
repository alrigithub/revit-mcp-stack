# -*- coding: utf-8 -*-
"""IronPython 2.7 compatible bridge ABI. No third-party imports."""
import json
import os
import platform
import hashlib

import clr
from System import Action, AppDomain, Guid

ABI = "revit-mcp.python/1"
BUILD_HASH = "revit-mcp-v0.9.0"
_compiled = {}
_compiled_order = []


def _load_bridge():
    bridge = None
    for assembly in AppDomain.CurrentDomain.GetAssemblies():
        if assembly.GetName().Name == "RevitMcp.Bridge":
            bridge = assembly
            break
    if bridge is None:
        raise RuntimeError("RevitMcp.Bridge is not loaded. Install and restart Revit first.")
    clr.AddReference(bridge)
    from RevitMcp.Bridge import PythonCompileDelegate
    from RevitMcp.Bridge import PythonExecuteDelegate
    from RevitMcp.Bridge import PythonProviderDescriptor
    from RevitMcp.Bridge import PythonRegistrationService
    return PythonCompileDelegate, PythonExecuteDelegate, PythonProviderDescriptor, PythonRegistrationService


def _json_safe(value, depth=0):
    if depth > 20:
        raise ValueError("result nesting exceeds 20")
    if value is None or isinstance(value, (bool, int, long, float, basestring)):
        return value
    if isinstance(value, (list, tuple)):
        if len(value) > 1000:
            raise ValueError("result list exceeds 1000 items")
        return [_json_safe(item, depth + 1) for item in value]
    if isinstance(value, dict):
        if len(value) > 1000:
            raise ValueError("result object exceeds 1000 fields")
        return dict((str(key), _json_safe(item, depth + 1)) for key, item in value.items())
    raise TypeError("Live Revit/API result objects are forbidden: " + type(value).__name__)


def _compile(source):
    try:
        return compile(source, "agent.py", "exec")
    except SyntaxError as error:
        payload = {
            "error": {
                "code": "python_compile_error",
                "message": str(error),
                "line": getattr(error, "lineno", None),
                "column": getattr(error, "offset", None),
                "engine": "IronPython",
                "dialect": "2.7"
            }
        }
        raise ValueError(json.dumps(payload, separators=(",", ":")))


def _prepare(source):
    try:
        key = hashlib.sha256(source.encode("utf-8")).hexdigest()
        if key not in _compiled:
            _compiled[key] = _compile(source)
            _compiled_order.append(key)
            while len(_compiled_order) > 64:
                del _compiled[_compiled_order.pop(0)]
        return "ok"
    except Exception as error:
        return str(error)


def _execute(uiapp, doc, uidoc, request_json):
    request = json.loads(request_json)
    source = request.get("source")
    if not isinstance(source, basestring):
        raise ValueError("source must be a string")
    key = hashlib.sha256(source.encode("utf-8")).hexdigest()
    code = _compiled.get(key)
    if code is None:
        raise RuntimeError("python source was not compiled before the transaction")
    scope = {"uiapp": uiapp, "doc": doc, "uidoc": uidoc, "request": request.get("request") or {}, "_result": None}
    exec code in scope
    return json.dumps(_json_safe(scope.get("_result")), separators=(",", ":"))


def _self_test():
    try:
        code = _compile("_result = {'ok': True, 'engine': 'IronPython'}")
        scope = {"_result": None}
        exec code in scope
        return scope.get("_result", {}).get("ok") is True, "persistent delegate self-test passed"
    except Exception as error:
        return False, str(error)


def _pyrevit_version():
    try:
        import pyrevit
        with open(os.path.join(os.path.dirname(pyrevit.__file__), "version")) as handle:
            return handle.read().strip()
    except Exception:
        return ""


def register_provider(enabled):
    PythonCompileDelegate, PythonExecuteDelegate, PythonProviderDescriptor, Registration = _load_bridge()
    passed, message = _self_test()
    descriptor = PythonProviderDescriptor()
    descriptor.AbiVersion = ABI
    descriptor.CompanionBuildHash = BUILD_HASH
    descriptor.EngineName = "IronPython"
    descriptor.EngineVersion = platform.python_version()
    # Older bridge builds have no PyRevitVersion field; registration must still work.
    if hasattr(descriptor, "PyRevitVersion"):
        descriptor.PyRevitVersion = _pyrevit_version()
    descriptor.ProviderGeneration = Guid.NewGuid().ToString("N")
    descriptor.Enabled = bool(enabled and passed)
    descriptor.SelfTestPassed = passed
    descriptor.SelfTestMessage = message

    def reload_delegate():
        # The persistent engine caches modules; re-read the on-disk source so the
        # ribbon Python toggle (and reload_python_provider) pick up provider edits.
        import revit_mcp_provider
        reload(revit_mcp_provider)
        revit_mcp_provider.register_provider(True)

    Registration.Register(descriptor, PythonCompileDelegate(_prepare), PythonExecuteDelegate(_execute), Action(reload_delegate))
    return descriptor.ProviderGeneration
