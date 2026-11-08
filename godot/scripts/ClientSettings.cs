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

    /// <summary>How the world's window is shown: a normal window, a window without a frame, or fullscreen.</summary>
    public enum WindowKind { Windowed, Borderless, Fullscreen }

    public static WindowKind WindowMode
    {
        get
        {
            ConfigFile cfg = Load();
            if (System.Enum.TryParse(cfg.GetValue("graphics", "window_mode", "").AsString(), out WindowKind k))
                return k;
            // Before window_mode there was only a fullscreen switch.
            return cfg.GetValue("graphics", "fullscreen", false).AsBool() ? WindowKind.Fullscreen : WindowKind.Windowed;
        }
        set => Save("window_mode", value.ToString());
    }

    /// <summary>Fullscreen (F11) or not; turning it off goes back to a normal window.</summary>
    public static bool Fullscreen
    {
        get => WindowMode == WindowKind.Fullscreen;
        set => WindowMode = value ? WindowKind.Fullscreen : WindowKind.Windowed;
    }

    /// <summary>Frames per second: 0 = the monitor's refresh rate (vsync), -1 = unlimited.</summary>
    public static int FrameCap
    {
        get => Load().GetValue("graphics", "frame_cap", 0).AsInt32();
        set => Save("frame_cap", value);
    }

    /// <summary>Applies <see cref="FrameCap"/> (vsync for 0; otherwise vsync off and the engine's cap).</summary>
    public static void ApplyFrameCap()
    {
        int cap = FrameCap;
        Engine.MaxFps = cap > 0 ? cap : 0;
        if (DisplayServer.GetName() != "headless")
            DisplayServer.WindowSetVsyncMode(cap == 0 ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
    }

    /// <summary>Window size in pixels (what is rendered), e.g. 1600×900; null = fit the screen.
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

    public const float MinFieldOfView = 50, MaxFieldOfView = 110;

    /// <summary>The camera's vertical field of view in degrees (retail has a Field of View option;
    /// its range and default are open question W29).</summary>
    public static float FieldOfView
    {
        get => System.Math.Clamp((float)Load().GetValue("graphics", "field_of_view", 70f).AsDouble(), MinFieldOfView, MaxFieldOfView);
        set => Save("field_of_view", value);
    }

    /// <summary>Raised when a setting changes; <c>display</c> is true when the window mode did.</summary>
    public static event System.Action<bool>? Changed;

    public static void NotifyChanged(bool display) => Changed?.Invoke(display);

    /// <summary>Whose names show over heads: retail's Game Options "Display Names" switches (and /shownames,
    /// /hidenames). Group and guild members count as players until the client tracks groups and guilds.</summary>
    [System.Flags]
    public enum NameKinds
    {
        None = 0, Monsters = 1, Gravestones = 2, Players = 4, Self = 8, Groupmates = 16, Guildmates = 32, Enemies = 64,
        Default = Monsters | Gravestones | Players | Groupmates | Guildmates | Enemies,   // retail: all but your own
    }

    public static NameKinds ShowNames
    {
        get => (NameKinds)Load().GetValue("game", "show_names", (int)NameKinds.Default).AsInt32();
        set => SaveIn("game", "show_names", (int)value);
    }

    /// <summary>Retail's "Names: Colored": names in con and realm colours; off, all white.</summary>
    public static bool ColouredNames
    {
        get => Load().GetValue("game", "coloured_names", true).AsBool();
        set => SaveIn("game", "coloured_names", value);
    }

    /// <summary>The sun casts real shadows (buildings, trees). Ours: the original drew fixed ones.</summary>
    public static bool SunShadows
    {
        get => Load().GetValue("graphics", "sun_shadows", true).AsBool();
        set => Save("sun_shadows", value);
    }

    /// <summary>Shift runs four times as fast (dev). Off by default: the server's speed check exempts only
    /// GMs, and snaps back and then disconnects a player who does it.</summary>
    public static bool Sprint
    {
        get => Load().GetValue("graphics", "sprint", false).AsBool();
        set => Save("sprint", value);
    }

    /// <summary>The key layout (KeyBindings.Presets): "rpg" (the default), "mmo", "fps" or "classic".</summary>
    public static string Controls
    {
        get => Load().GetValue("controls", "preset", "rpg").AsString();
        set => SaveIn("controls", "preset", value);
    }

    /// <summary>The chase camera's default distance (metres) and pitch (radians), as /setcamera saved them; null
    /// until then.</summary>
    public static (float Distance, float Pitch)? Camera
    {
        get
        {
            ConfigFile cfg = Load();
            return cfg.HasSectionKey("controls", "camera_distance")
                ? ((float)cfg.GetValue("controls", "camera_distance").AsDouble(), (float)cfg.GetValue("controls", "camera_pitch").AsDouble())
                : null;
        }
        set
        {
            if (value is { } c)
            {
                SaveIn("controls", "camera_distance", c.Distance);
                SaveIn("controls", "camera_pitch", c.Pitch);
            }
        }
    }

    /// <summary>The player's own key bindings (section "keys": action name = key codes, comma-separated; empty =
    /// no key). Actions not listed use the preset's keys.</summary>
    public static Dictionary<ControlAction, Key[]> LoadKeys()
    {
        ConfigFile cfg = Load();
        var keys = new Dictionary<ControlAction, Key[]>();
        if (!cfg.HasSection("keys"))
            return keys;
        foreach (string name in cfg.GetSectionKeys("keys"))
            if (System.Enum.TryParse(name, out ControlAction action))
                keys[action] = cfg.GetValue("keys", name).AsString().Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => long.TryParse(k, out long code) ? (Key)code : Key.None).Where(k => k != Key.None).ToArray();
        return keys;
    }

    public static void SaveKeys(Dictionary<ControlAction, Key[]> keys)
    {
        ConfigFile cfg = Load();
        if (cfg.HasSection("keys"))
            cfg.EraseSection("keys");
        foreach ((ControlAction action, Key[] k) in keys)
            cfg.SetValue("keys", action.ToString(), string.Join(",", k.Select(x => ((long)x).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        cfg.Save(Path);
    }

    /// <summary>Puts the window in the saved mode (no-op without a display, e.g. headless checks).</summary>
    public static void ApplyWindowMode()
    {
        if (DisplayServer.GetName() == "headless")
            return;
        WindowKind kind = WindowMode;
        if (kind == WindowKind.Fullscreen)
        {
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
            return;
        }
        int screen = DisplayServer.WindowGetCurrentScreen();
        if (kind == WindowKind.Borderless)
        {
            // A frameless window: the whole screen when no size is chosen, else that size, centred.
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true);
            var area = new Rect2I(DisplayServer.ScreenGetPosition(screen), DisplayServer.ScreenGetSize(screen));
            Vector2I s = WindowSize is Vector2I w ? new Vector2I(System.Math.Min(w.X, area.Size.X), System.Math.Min(w.Y, area.Size.Y)) : area.Size;
            DisplayServer.WindowSetSize(s);
            DisplayServer.WindowSetPosition(area.Position + (area.Size - s) / 2);
            return;
        }
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
        if (WindowSize is Vector2I size)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
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
        int screen = DisplayServer.WindowGetCurrentScreen();
        GD.Print($"[display] screen {screen}: {DisplayServer.ScreenGetSize(screen)}, usable {DisplayServer.ScreenGetUsableRect(screen).Size}, " +
                 $"scale {DisplayServer.ScreenGetScale(screen)}");
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        Rect2I usable = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        var size = new Vector2I(System.Math.Min(900, usable.Size.X), System.Math.Min(700, usable.Size.Y));
        DisplayServer.WindowSetSize(size);
        DisplayServer.WindowSetPosition(usable.Position + (usable.Size - size) / 2);
    }

    private static void SaveIn(string section, string key, Variant value)
    {
        ConfigFile cfg = Load();
        cfg.SetValue(section, key, value);
        cfg.Save(Path);
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
