# -*- coding: utf-8 -*-
import os
import sys

lib = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", "lib"))
if lib not in sys.path:
    sys.path.insert(0, lib)

from revit_mcp_provider import _load_bridge
from revit_mcp_ui import refresh_controls

_, _, _, Registration = _load_bridge()
Registration.SetEnabled(False)
refresh_controls()
