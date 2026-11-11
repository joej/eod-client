// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: --ui-dump PATH writes every game window's controls as JSON (rectangles in skin pixels, relative to the
// window) a few seconds after the view opens, then quits. tools/uicheck.py reads it: buttons that overlap, controls that
// spill out of their window, and window sizes against the skin's XML. Run at several --ui-scale values: a control that
// is right at one size and wrong at another (buttons held to Godot's default minimum at 1.15, 2026-11-11) only shows so.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private void ScheduleUiDump()
    {
        string[] args = OS.GetCmdlineUserArgs();
        int at = System.Array.IndexOf(args, "--ui-dump");
        if (at < 0 || at + 1 >= args.Length)
            return;
        string path = args[at + 1];
        GetTree().CreateTimer(3).Timeout += () =>
        {
            var windows = new List<object>();
            foreach (HudWindow w in GetTree().Root.FindChildren("*", "", true, false).OfType<HudWindow>())
            {
                float s = w.SkinScale;
                Vector2 origin = w.GetGlobalRect().Position;
                var controls = new List<object>();
                foreach (Control c in w.FindChildren("*", "Control", true, false).OfType<Control>())
                {
                    Rect2 r = c.GetGlobalRect();
                    controls.Add(new
                    {
                        type = c.GetClass(),
                        text = c switch { Button b => b.Text, Label l => l.Text, _ => "" },
                        parent = w.GetPathTo(c.GetParent()).ToString(),
                        x = (r.Position.X - origin.X) / s, y = (r.Position.Y - origin.Y) / s, w = r.Size.X / s, h = r.Size.Y / s,
                        minH = c.GetCombinedMinimumSize().Y / s,
                        shown = c.IsVisibleInTree() || !w.IsVisibleInTree() && c.Visible,
                    });
                }
                windows.Add(new { key = w.Key, scale = s, w = w.Size.X / s, h = w.Size.Y / s, controls });
            }
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(new { scale = UiScale, windows }, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print($"[ui] {windows.Count} window(s) -> {path}");
            GetTree().Quit();
        };
    }
}
