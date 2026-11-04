// SPDX-License-Identifier: GPL-3.0-or-later
//
// The player's client settings, saved between sessions in Godot's per-user data folder
// (user://settings.cfg). Small for now; the settings UI comes with the HUD work (roadmap M9).

using Godot;

namespace EodClient;

public static class ClientSettings
{
    private const string Path = "user://settings.cfg";

    /// <summary>View distance as a multiple of the zone's own (SECTOR.DAT [fog] clip). "Classic" (1×)
    /// is what the original client drew; the longer ones are ours.</summary>
    public static readonly (string Label, float Multiple)[] ViewDistances =
        [("classic", 1f), ("x1.5", 1.5f), ("x2", 2f), ("x4", 4f)];

    public static int ViewDistance
    {
        get => System.Math.Clamp(Load().GetValue("graphics", "view_distance", 0).AsInt32(), 0, ViewDistances.Length - 1);
        set
        {
            ConfigFile cfg = Load();
            cfg.SetValue("graphics", "view_distance", value);
            cfg.Save(Path);
        }
    }

    /// <summary>Fullscreen (F11), else a window of <see cref="WindowSize"/>, else a maximized window.</summary>
    public static bool Fullscreen
    {
        get => Load().GetValue("graphics", "fullscreen", false).AsBool();
        set => Save("fullscreen", value);
    }

    /// <summary>Window size in pixels (what is rendered), e.g. 1600×900; null = maximized.
    /// Set with --window WxH (tools/play.sh play --window 1600x900); --window max clears it.</summary>
    public static Vector2I? WindowSize
    {
        get => ParseSize(Load().GetValue("graphics", "window_size", "").AsString());
        set => Save("window_size", value is Vector2I v ? $"{v.X}x{v.Y}" : "");
    }

    /// <summary>"1600x900" → (1600, 900); anything else (including "max") → null.</summary>
    public static Vector2I? ParseSize(string text)
    {
        string[] wh = text.ToLowerInvariant().Split('x');
        return wh.Length == 2 && int.TryParse(wh[0], out int w) && int.TryParse(wh[1], out int h) && w >= 320 && h >= 240
            ? new Vector2I(w, h) : null;
    }

    /// <summary>The sun casts real shadows (buildings, trees). Ours: the original drew fixed ones.</summary>
    public static bool SunShadows
    {
        get => Load().GetValue("graphics", "sun_shadows", true).AsBool();
        set => Save("sun_shadows", value);
    }

    /// <summary>Puts the window in the saved mode (no-op without a display, e.g. headless checks).</summary>
    public static void ApplyWindowMode()
    {
        if (DisplayServer.GetName() == "headless")
            return;
        if (Fullscreen)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
        }
        else if (WindowSize is Vector2I size)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            int screen = DisplayServer.WindowGetCurrentScreen();
            Rect2I usable = DisplayServer.ScreenGetUsableRect(screen);
            size = new Vector2I(System.Math.Min(size.X, usable.Size.X), System.Math.Min(size.Y, usable.Size.Y));
            DisplayServer.WindowSetSize(size);
            DisplayServer.WindowSetPosition(usable.Position + (usable.Size - size) / 2);
        }
        else
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
        }
    }

    /// <summary>The login and character screen: a form-sized window, centred, whatever the world uses.</summary>
    public static void ApplyLoginWindow()
    {
        if (DisplayServer.GetName() == "headless")
            return;
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        Rect2I usable = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        var size = new Vector2I(System.Math.Min(900, usable.Size.X), System.Math.Min(700, usable.Size.Y));
        DisplayServer.WindowSetSize(size);
        DisplayServer.WindowSetPosition(usable.Position + (usable.Size - size) / 2);
    }

    private static void Save(string key, Variant value)
    {
        ConfigFile cfg = Load();
        cfg.SetValue("graphics", key, value);
        cfg.Save(Path);
    }

    private static ConfigFile Load()
    {
        var cfg = new ConfigFile();
        cfg.Load(Path);   // missing file = defaults
        return cfg;
    }
}
