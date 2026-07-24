# -*- coding: utf-8 -*-
"""Best-effort actual-state projection onto the two pyRevit ribbon buttons."""
import json

import clr
clr.AddReference("WindowsBase")
clr.AddReference("PresentationCore")
clr.AddReference("PresentationFramework")

from System import AppDomain
from System.Windows import Point
from System.Windows.Media import Brushes, Color, DrawingGroup, DrawingImage, EllipseGeometry, GeometryDrawing, Pen, SolidColorBrush


def _status():
    for assembly in AppDomain.CurrentDomain.GetAssemblies():
        if assembly.GetName().Name == "RevitMcp.Bridge":
            service = assembly.GetType("RevitMcp.Bridge.PythonRegistrationService")
            method = service.GetMethod("GetStatusJson")
            return json.loads(str(method.Invoke(None, None)))
    return {"capability": "not_installed"}


def _icon(red, green, blue, size):
    group = DrawingGroup()
    color = Color.FromRgb(red, green, blue)
    geometry = EllipseGeometry(Point(size / 2.0, size / 2.0), size * 0.38, size * 0.38)
    group.Children.Add(GeometryDrawing(SolidColorBrush(color), Pen(Brushes.White, 2.0 if size > 16 else 1.0), geometry))
    group.Freeze()
    image = DrawingImage(group)
    image.Freeze()
    return image


def _button(name):
    try:
        from pyrevit.coreutils import ribbon
        ui = ribbon.get_current_ui()
        return ui.get_tab("RevitMCP").get_panel("Runtime").get_item(name)
    except Exception:
        return None


def refresh_controls():
    state = _status()
    available = state.get("capability") == "available"
    for name, is_on in (("Python ON", True), ("Python OFF", False)):
        wrapper = _button(name)
        if wrapper is None:
            continue
        try:
            item = wrapper._rvtapi_object
            valid = (is_on and not available) or ((not is_on) and available)
            item.Enabled = valid
            item.ToolTip = "%s. Actual Python capability: %s. Provider generation is read from the bridge." % (name, state.get("capability"))
            color = (24, 160, 72) if is_on else ((190, 45, 45) if available else (110, 110, 110))
            item.Image = _icon(color[0], color[1], color[2], 16.0)
            item.LargeImage = _icon(color[0], color[1], color[2], 32.0)
        except Exception:
            pass
    return state
