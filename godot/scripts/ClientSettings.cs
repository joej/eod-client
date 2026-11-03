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

    /// <summary>Fullscreen (F11) or a maximized window.</summary>
    public static bool Fullscreen
    {
        get => Load().GetValue("graphics", "fullscreen", false).AsBool();
        set => Save("fullscreen", value);
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
        DisplayServer.WindowSetMode(Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Maximized);
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
