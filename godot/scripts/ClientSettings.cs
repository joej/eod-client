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

    public const float MinFieldOfView = 60, MaxFieldOfView = 130, RetailFieldOfView = 79.6f;

    /// <summary>The camera's HORIZONTAL field of view in degrees; the vertical follows the window's shape, so a wider
    /// window sees the same width and less height. Retail has no such option: its camera is fixed at 79.6 degrees across
    /// (daoc-re RE/72, W29), our default; the slider is ours. A vertical angle saved before (field_of_view) is read as
    /// the same width on a 16:9 window, so a player's chosen view doesn't change.</summary>
    public static float FieldOfView
    {
        get
        {
            ConfigFile cfg = Load();
            float across = cfg.HasSectionKey("graphics", "field_of_view_across")
                ? (float)cfg.GetValue("graphics", "field_of_view_across").AsDouble()
                : cfg.HasSectionKey("graphics", "field_of_view")
                    ? AcrossFromVertical((float)cfg.GetValue("graphics", "field_of_view").AsDouble(), 16f / 9f)
                    : RetailFieldOfView;
            return System.Math.Clamp(across, MinFieldOfView, MaxFieldOfView);
        }
        set => Save("field_of_view_across", value);
    }

    /// <summary>The angle across for a vertical one on a window of the given width / height.</summary>
    public static float AcrossFromVertical(float verticalDegrees, float aspect) =>
        (float)(2 * System.Math.Atan(System.Math.Tan(verticalDegrees * System.Math.PI / 360) * aspect) * 180 / System.Math.PI);

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

    /// <summary>The compass strip over the quickbars (retail's Interface Elements → COMPASS). Ours starts shown; retail's
    /// default.ini starts it hidden.</summary>
    public static bool ShowCompass
    {
        get => Load().GetValue("game", "compass", true).AsBool();
        set => SaveIn("game", "compass", value);
    }

    /// <summary>The clock (retail's Interface Elements → CLOCK): the game time of day. Shown by default.</summary>
    public static bool ShowClock
    {
        get => Load().GetValue("game", "clock", true).AsBool();
        set => SaveIn("game", "clock", value);
    }

    /// <summary>The classic summary (retail's `summary`: Hit, End, Pow and the Exp bubbles), our default; off, the newer
    /// new_summary_window (level and realm rank, no XP).</summary>
    public static bool ClassicSummary
    {
        get => Load().GetValue("game", "classic_summary", true).AsBool();
        set => SaveIn("game", "classic_summary", value);
    }

    /// <summary>The floating Level Exp. window (more XP bubbles; the classic summary and retail's menu bar have them too).
    /// Hidden by default, as retail's default.ini has it.</summary>
    public static bool ShowLevelXp
    {
        get => Load().GetValue("game", "level_xp", false).AsBool();
        set => SaveIn("game", "level_xp", value);
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

    /// <summary>
    /// A character's camera home (retail's panels file [Camera]): distance and height in game units, the orbit angle
    /// and tilt in degrees (positive tilt = above, looking down). /setcamera saves it, /resetcamera sets it to retail's
    /// 240 / 10 / 0 / 0, the Reset Camera key and entering the world apply it.
    /// </summary>
    public readonly record struct CameraHome(float Distance, float Height, float Angle, float Tilt)
    {
        /// <summary>Retail's limits for home: distance 0-500, height +-50, tilt +-80 degrees.</summary>
        public CameraHome Clamped() => new(System.Math.Clamp(Distance, 0, 500), System.Math.Clamp(Height, -50, 50),
            Angle, System.Math.Clamp(Tilt, -80, 80));
    }

    private static string CameraSection(string character) => character.Length > 0 ? $"camera {character}" : "camera";

    /// <summary>The character's saved camera home, or null: never saved. Before homes were per character, /setcamera
    /// saved one distance and pitch for everyone ([controls] camera_distance in metres, camera_pitch in radians, our
    /// sign: negative = above); that is still read for a character with none of its own.</summary>
    public static CameraHome? CameraHomeOf(string character)
    {
        ConfigFile cfg = Load();
        string section = CameraSection(character);
        if (cfg.HasSectionKey(section, "distance"))
            return new CameraHome((float)cfg.GetValue(section, "distance").AsDouble(), (float)cfg.GetValue(section, "height", 0.0).AsDouble(),
                (float)cfg.GetValue(section, "angle", 0.0).AsDouble(), (float)cfg.GetValue(section, "tilt", 0.0).AsDouble());
        if (cfg.HasSectionKey("controls", "camera_distance"))
            return new CameraHome((float)cfg.GetValue("controls", "camera_distance").AsDouble() / 0.025f, 10, 0,
                -(float)(cfg.GetValue("controls", "camera_pitch", 0.0).AsDouble() * 180 / System.Math.PI));
        return null;
    }

    public static void SaveCameraHome(string character, CameraHome home)
    {
        ConfigFile cfg = Load();
        string section = CameraSection(character);
        cfg.SetValue(section, "distance", home.Distance);
        cfg.SetValue(section, "height", home.Height);
        cfg.SetValue(section, "angle", home.Angle);
        cfg.SetValue(section, "tilt", home.Tilt);
        cfg.Save(Path);
    }

    /// <summary>
    /// The scale of the game's windows (summary, target…): skin pixels to screen pixels, as retail's ui_scale
    /// (1.0–4.0; retail never goes below 1). 0 = not set: follow the screen's density (2 on a Retina display).
    /// </summary>
    public static float UiScale
    {
        get => (float)Load().GetValue("interface", "ui_scale", 0.0).AsDouble();
        set => SaveIn("interface", "ui_scale", value);
    }

    /// <summary>
    /// Classic terrain: the baked ground tiles instead of retail's default layered "Atlantis" ground (its own option has
    /// the same name). Off by default, as in retail.
    /// </summary>
    public static bool ClassicTerrain
    {
        get => Load().GetValue("graphics", "classic_terrain", false).AsBool();
        set => Save("classic_terrain", value);
    }

    /// <summary>Where a game window was last dragged (its top-left, in skin pixels), or null: its default.</summary>
    public static Vector2? WindowPosition(string window)
    {
        ConfigFile cfg = Load();
        return cfg.HasSectionKey("windows", window) ? cfg.GetValue("windows", window).AsVector2() : null;
    }

    public static void SaveWindowPosition(string window, Vector2 position) => SaveIn("windows", window, position);

    /// <summary>A game window's opacity (0.01-1), as the player set it from the window's right-click menu, or null: its
    /// default (retail's default.ini: 68%, the clock 38%, the chat tabs 58% / 38%).</summary>
    public static float? WindowAlpha(string window)
    {
        ConfigFile cfg = Load();
        return cfg.HasSectionKey("window_alpha", window) ? (float)cfg.GetValue("window_alpha", window).AsDouble() : null;
    }

    public static void SaveWindowAlpha(string window, float alpha) => SaveIn("window_alpha", window, alpha);

    /// <summary>Whether an optional window is shown (Interface Elements, the command window's buttons).</summary>
    public static bool WindowShown(string window, bool byDefault) =>
        Load().GetValue("window_shown", window, byDefault).AsBool();

    public static void SaveWindowShown(string window, bool shown) => SaveIn("window_shown", window, shown);

    /// <summary>What a character's quickbar slot holds ("bar.page.slot", 1-based), or "": see WorldView.Quickbar.</summary>
    public static string QuickbarSlot(string character, string key) =>
        Load().GetValue($"quickbar {character}", key, "").AsString();

    public static void SaveQuickbarSlot(string character, string key, string entry)
    {
        ConfigFile cfg = Load();
        if (entry.Length > 0)
            cfg.SetValue($"quickbar {character}", key, entry);
        else if (cfg.HasSectionKey($"quickbar {character}", key))
            cfg.EraseSectionKey($"quickbar {character}", key);
        cfg.Save(Path);
    }

    /// <summary>The page a character's quickbar shows (0-based).</summary>
    public static int QuickbarPage(string character, int bar) =>
        Load().GetValue($"quickbar {character}", $"page{bar + 1}", 0).AsInt32();

    public static void SaveQuickbarPage(string character, int bar, int page) =>
        SaveIn($"quickbar {character}", $"page{bar + 1}", page);

    /// <summary>Whether a quickbar shows (/quickbar): all three by default, as retail's default.ini (the 7th field of the
    /// QuickbarN= line is 1 in 50 of its 59 layouts for bar 3).</summary>
    public static bool QuickbarShown(string character, int bar) =>
        Load().GetValue($"quickbar {character}", $"shown{bar + 1}", true).AsBool();

    public static void SaveQuickbarShown(string character, int bar, bool shown) =>
        SaveIn($"quickbar {character}", $"shown{bar + 1}", shown);

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
