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

    private static ConfigFile Load()
    {
        var cfg = new ConfigFile();
        cfg.Load(Path);   // missing file = defaults
        return cfg;
    }
}
