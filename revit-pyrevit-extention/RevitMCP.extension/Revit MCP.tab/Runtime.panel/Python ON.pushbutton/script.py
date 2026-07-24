# -*- coding: utf-8 -*-
import os
import sys

lib = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", "lib"))
if lib not in sys.path:
    sys.path.insert(0, lib)

import revit_mcp_provider
# The persistent engine caches modules, so re-read the on-disk provider source;
# otherwise provider updates would silently keep running the old code.
reload(revit_mcp_provider)
from revit_mcp_ui import refresh_controls

# Register from this persistent command engine as well as enabling Python.
# This makes the button self-healing if extension startup was skipped or failed.
revit_mcp_provider.register_provider(True)
refresh_controls()
