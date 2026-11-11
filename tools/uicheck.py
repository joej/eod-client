#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
#
# uicheck.py: checks the game windows as the client builds them, from the JSON the client writes with --ui-dump (see
# tools/uicheck.sh, which runs the client at several interface sizes). Reports, per window:
#   ERROR   buttons in the same part of a window overlapping (rows of buttons drawn on top of each other);
#   ERROR   a window's size differing from the skin's XML (the owner's install, ui/atlantis/*.xml);
#   WARN    a control spilling out of its window;
#   WARN    a control drawn smaller than its own minimum (Godot will grow it as soon as anything changes).
# Rectangles are in skin pixels, so a fault that only shows at one interface size is caught at that size.
# Exit status 1 when there is any ERROR.
#
#   uicheck.py DUMP.json [DUMP.json ...] [--skin DIR]

import json
import os
import sys
import xml.etree.ElementTree as ET

# Our window key -> the skin's WindowTemplate name (None: drawn by the client itself, or ours alone).
SKIN = {
    "summary": "summary", "target": "float_target_window", "clock": "clock", "mini_info": "mini_info", "info": "info",
    "main_menu": "main_menu_window", "menu_bar": "menu_bar_window", "level_xp": "float_level_exp_window",
    "compass": None, "stats": None, "dialog": None,
}
# Windows whose height grows with their content, as retail's do (mini_info grows to fit its buttons).
GROWS = {"mini_info"}


def skin_sizes(skin_dir):
    sizes = {}
    if not skin_dir or not os.path.isdir(skin_dir):
        return sizes
    for f in os.listdir(skin_dir):
        if not f.endswith(".xml"):
            continue
        try:
            root = ET.parse(os.path.join(skin_dir, f)).getroot()
        except ET.ParseError:
            continue
        for w in root.iter("WindowTemplate"):
            try:
                sizes[w.findtext("Name")] = (float(w.findtext("Width")), float(w.findtext("Height")))
            except (TypeError, ValueError):
                pass
    return sizes


def overlap(a, b):
    return max(0.0, min(a["x"] + a["w"], b["x"] + b["w"]) - max(a["x"], b["x"])) * \
           max(0.0, min(a["y"] + a["h"], b["y"] + b["h"]) - max(a["y"], b["y"]))


def check(dump, skins):
    errors, warns = [], []
    scale = dump["scale"]
    for win in dump["windows"]:
        key, ww, wh = win["key"], win["w"], win["h"]
        tag = f"[{scale:g}] {key}"
        name = SKIN.get(key)
        if name and name in skins:
            sw, sh = skins[name]
            if abs(sw - ww) > 0.5 or (abs(sh - wh) > 0.5 and not (key in GROWS and wh > sh)):
                errors.append(f"{tag}: {ww:g} x {wh:g}, the skin's {name} is {sw:g} x {sh:g}")
        shown = [c for c in win["controls"] if c["shown"] and c["w"] > 0 and c["h"] > 0]
        buttons = [c for c in shown if c["type"] == "Button"]
        for i, a in enumerate(buttons):
            for b in buttons[i + 1:]:
                if a["parent"] == b["parent"] and overlap(a, b) > 2:
                    errors.append(f"{tag}: buttons \"{a['text']}\" and \"{b['text']}\" overlap "
                                  f"({a['x']:.0f},{a['y']:.0f} {a['w']:.0f}x{a['h']:.0f} / {b['x']:.0f},{b['y']:.0f} {b['w']:.0f}x{b['h']:.0f})")
        for c in shown:
            if c["x"] < -1 or c["y"] < -1 or c["x"] + c["w"] > ww + 1 or c["y"] + c["h"] > wh + 1:
                warns.append(f"{tag}: {c['type']} \"{c['text']}\" at {c['x']:.0f},{c['y']:.0f} {c['w']:.0f}x{c['h']:.0f} spills out of {ww:g} x {wh:g}")
            if c["type"] == "Button" and c["minH"] > c["h"] + 0.5:
                warns.append(f"{tag}: button \"{c['text']}\" is {c['h']:.1f} high but needs {c['minH']:.1f}")
    return errors, warns


def main(argv):
    skin = None
    if "--skin" in argv:
        i = argv.index("--skin")
        skin = argv[i + 1]
        argv = argv[:i] + argv[i + 2:]
    skins = skin_sizes(skin)
    all_errors = []
    for path in argv:
        errors, warns = check(json.load(open(path)), skins)
        for e in errors:
            print("ERROR", e)
        for w in warns:
            print("WARN ", w)
        all_errors += errors
    print(f"{len(all_errors)} error(s)")
    return 1 if all_errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
