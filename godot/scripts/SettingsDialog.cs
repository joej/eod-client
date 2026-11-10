// SPDX-License-Identifier: GPL-3.0-or-later
//
// The player's settings window (login screen button; F10 or the Settings button in the world):
// a Display tab (window mode: window, borderless, fullscreen; window size: fit the screen, or a common size that
// fits it; frame rate cap; field of view) and a Keyboard tab (KeyboardPanel: layout and key bindings). Changes are saved at once (ClientSettings) and raise ClientSettings.Changed, so the world
// applies them live.

using Godot;

namespace EodClient;

public partial class SettingsDialog : AcceptDialog
{
    // Common 16:9 and 16:10 sizes; only those that fit the screen are offered.
    private static readonly Vector2I[] CommonSizes =
    [
        new(1280, 720), new(1280, 800), new(1366, 768), new(1440, 900), new(1600, 900), new(1680, 1050),
        new(1920, 1080), new(1920, 1200), new(2560, 1440), new(2560, 1600), new(3840, 2160),
    ];

    private const int FitItem = 0;
    // 0 = the monitor's refresh rate (vsync), -1 = unlimited.
    private static readonly int[] FrameCaps = [0, 30, 60, 75, 90, 120, 144, 165, 240, -1];
    private readonly List<Vector2I> _sizes = [];
    private TabContainer _tabs = null!;

    /// <summary>Opens the window over <paramref name="parent"/>'s scene (one at a time).</summary>
    public static void Open(Node parent) => Open(parent, 0);

    /// <summary>Opens the window on tab <paramref name="tab"/> (0 Display, 1 Keyboard, 2 Game).</summary>
    public static void Open(Node parent, int tab)
    {
        foreach (Node n in parent.GetTree().Root.GetChildren())
            if (n is SettingsDialog open)
            {
                open.GrabFocus();
                return;
            }
        var dialog = new SettingsDialog();
        parent.GetTree().Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(460, 0));
        dialog._tabs.CurrentTab = tab;
    }

    public override void _Ready()
    {
        Title = "Settings";
        OkButtonText = "Close";
        Exclusive = false;
        Confirmed += QueueFree;
        Canceled += QueueFree;

        // AcceptDialog lays out one child: tabs, Display (a column holding the settings grid and a note) and Keyboard.
        var tabs = _tabs = new TabContainer();
        AddChild(tabs);
        var column = new VBoxContainer { Name = "Display" };
        column.AddThemeConstantOverride("separation", 14);
        tabs.AddChild(column);
        tabs.AddChild(new KeyboardPanel());
        tabs.AddChild(GamePanel());
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 10);
        column.AddChild(grid);

        // Window mode, then size (not used in fullscreen, which is always the screen's own resolution).
        grid.AddChild(new Label { Text = "Window mode" });
        var mode = new OptionButton { CustomMinimumSize = new Vector2(260, 0) };
        mode.AddItem("Window", (int)ClientSettings.WindowKind.Windowed);
        mode.AddItem("Borderless window", (int)ClientSettings.WindowKind.Borderless);
        mode.AddItem("Fullscreen (F11)", (int)ClientSettings.WindowKind.Fullscreen);
        mode.Select((int)ClientSettings.WindowMode);
        grid.AddChild(mode);

        grid.AddChild(new Label { Text = "Window size" });
        var size = new OptionButton { CustomMinimumSize = new Vector2(260, 0) };
        size.AddItem("Fit the screen", FitItem);
        Rect2I usable = DisplayServer.GetName() == "headless"
            ? new Rect2I(0, 0, 3840, 2160)
            : DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        Vector2I? saved = ClientSettings.WindowSize;
        foreach (Vector2I s in CommonSizes.Where(s => s.X <= usable.Size.X && s.Y <= usable.Size.Y))
            _sizes.Add(s);
        if (saved is Vector2I custom && !_sizes.Contains(custom))
            _sizes.Add(custom);   // set with --window: keep it choosable
        GD.Print($"[display] usable {usable.Size}; window sizes offered: {string.Join(", ", _sizes.Select(s => $"{s.X}x{s.Y}"))}");
        foreach (Vector2I s in _sizes)
            size.AddItem($"{s.X} × {s.Y}" + (CommonSizes.Contains(s) ? "" : " (custom)"), 1 + _sizes.IndexOf(s));
        size.Select(saved is Vector2I v ? 1 + _sizes.IndexOf(v) : FitItem);
        size.Disabled = ClientSettings.Fullscreen;
        size.ItemSelected += index =>
        {
            int id = size.GetItemId((int)index);
            ClientSettings.WindowSize = id == FitItem ? null : _sizes[id - 1];
            ClientSettings.NotifyChanged(display: true);
        };
        mode.ItemSelected += index =>
        {
            ClientSettings.WindowMode = (ClientSettings.WindowKind)mode.GetItemId((int)index);
            size.Disabled = ClientSettings.Fullscreen;
            ClientSettings.NotifyChanged(display: true);
        };
        grid.AddChild(size);

        grid.AddChild(new Label { Text = "Frame rate" });
        var frames = new OptionButton { CustomMinimumSize = new Vector2(260, 0) };
        for (int k = 0; k < FrameCaps.Length; k++)
            frames.AddItem(FrameCaps[k] switch { 0 => "Monitor refresh (vsync)", -1 => "Unlimited", int f => $"{f} fps" }, k);
        frames.Select(Math.Max(0, Array.IndexOf(FrameCaps, ClientSettings.FrameCap)));
        frames.ItemSelected += index =>
        {
            ClientSettings.FrameCap = FrameCaps[(int)index];
            ClientSettings.ApplyFrameCap();
        };
        grid.AddChild(frames);

        // Retail's ground option: its layered "Atlantis" terrain (the default) or the classic baked tiles. Zones built
        // after the change use it (the next zone you enter, or a restart for the one you're in).
        grid.AddChild(new Label { Text = "Terrain" });
        var classic = new CheckBox
        {
            Text = "Classic terrain (baked tiles)", ButtonPressed = ClientSettings.ClassicTerrain, FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Off: retail's default layered ground (Atlantis Terrain). Applies to zones loaded after the change.",
        };
        classic.Toggled += on => ClientSettings.ClassicTerrain = on;
        grid.AddChild(classic);

        // Retail's ui_scale (1.0–4.0): every game window and its text together. Auto follows the screen (1.5 at 1080p).
        grid.AddChild(new Label { Text = "Interface size" });
        var uiRow = new HBoxContainer();
        var ui = new HSlider
        {
            MinValue = 1, MaxValue = 4, Step = 0.05, Value = WorldView.UiScale, CustomMinimumSize = new Vector2(150, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var uiValue = new Label { Text = $"{WorldView.UiScale:0.00}", CustomMinimumSize = new Vector2(40, 0) };
        var auto = new Button { Text = "Auto", FocusMode = Control.FocusModeEnum.None, TooltipText = "Follow the screen's size (1.5 at 1080p)" };
        // Applied when the handle is let go, or at once for a click or key (rebuilding the windows on every step of a drag
        // would stutter).
        bool dragging = false;
        void Apply()
        {
            ClientSettings.UiScale = (float)ui.Value;
            ClientSettings.NotifyChanged(display: false);
        }
        ui.DragStarted += () => dragging = true;
        ui.DragEnded += changed =>
        {
            dragging = false;
            if (changed)
                Apply();
        };
        ui.ValueChanged += value =>
        {
            uiValue.Text = $"{value:0.00}";
            if (!dragging)
                Apply();
        };
        auto.Pressed += () =>
        {
            ClientSettings.UiScale = 0;
            ui.SetValueNoSignal(WorldView.UiScale);
            uiValue.Text = $"{WorldView.UiScale:0.00}";
            ClientSettings.NotifyChanged(display: false);
        };
        uiRow.AddChild(ui);
        uiRow.AddChild(uiValue);
        uiRow.AddChild(auto);
        grid.AddChild(uiRow);

        grid.AddChild(new Label { Text = "Field of view" });
        var fovRow = new HBoxContainer();
        var fov = new HSlider
        {
            MinValue = ClientSettings.MinFieldOfView, MaxValue = ClientSettings.MaxFieldOfView, Step = 1,
            Value = ClientSettings.FieldOfView, CustomMinimumSize = new Vector2(200, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var fovValue = new Label { Text = $"{ClientSettings.FieldOfView:0}°", CustomMinimumSize = new Vector2(48, 0) };
        fov.ValueChanged += value =>
        {
            ClientSettings.FieldOfView = (float)value;
            fovValue.Text = $"{value:0}°";
            ClientSettings.NotifyChanged(display: false);
        };
        fovRow.AddChild(fov);
        fovRow.AddChild(fovValue);
        grid.AddChild(fovRow);

        column.AddChild(new Label
        {
            Text = "Window mode and size are for the game world; this login screen stays a small window.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0),
        });
    }

    // Game: retail's Game Options "Names" choice and its "Display Names" switches, in its two columns.
    private static Control GamePanel()
    {
        var column = new VBoxContainer { Name = "Game" };
        column.AddThemeConstantOverride("separation", 10);
        var namesRow = new HBoxContainer();
        namesRow.AddChild(new Label { Text = "Names", CustomMinimumSize = new Vector2(160, 0) });
        var colour = new OptionButton { CustomMinimumSize = new Vector2(160, 0) };
        colour.AddItem("Colored");
        colour.AddItem("White");
        colour.Select(ClientSettings.ColouredNames ? 0 : 1);
        colour.ItemSelected += index =>
        {
            ClientSettings.ColouredNames = index == 0;
            ClientSettings.NotifyChanged(display: false);
        };
        namesRow.AddChild(colour);
        column.AddChild(namesRow);

        var compass = new CheckBox { Text = "Compass", ButtonPressed = ClientSettings.ShowCompass, FocusMode = Control.FocusModeEnum.None };
        compass.Toggled += on =>
        {
            ClientSettings.ShowCompass = on;
            ClientSettings.NotifyChanged(display: false);
        };
        column.AddChild(compass);

        column.AddChild(new Label { Text = "Display Names" });
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 30);
        (string Label, ClientSettings.NameKinds Kind)[] order =
        [
            ("Monsters/NPCs", ClientSettings.NameKinds.Monsters), ("Groupmates", ClientSettings.NameKinds.Groupmates),
            ("Gravestones", ClientSettings.NameKinds.Gravestones), ("Guildmates", ClientSettings.NameKinds.Guildmates),
            ("Players", ClientSettings.NameKinds.Players), ("Enemy Players", ClientSettings.NameKinds.Enemies),
            ("Your Name", ClientSettings.NameKinds.Self),
        ];
        foreach ((string label, ClientSettings.NameKinds kind) in order)
        {
            var box = new CheckBox { Text = label, ButtonPressed = ClientSettings.ShowNames.HasFlag(kind), FocusMode = Control.FocusModeEnum.None };
            box.Toggled += on =>
            {
                ClientSettings.ShowNames = on ? ClientSettings.ShowNames | kind : ClientSettings.ShowNames & ~kind;
                ClientSettings.NotifyChanged(display: false);
            };
            grid.AddChild(box);
        }
        column.AddChild(grid);
        column.AddChild(new Label
        {
            Text = "As retail's Game Options. Groupmates and guildmates count as players until groups and guilds are shown.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0), Modulate = new Color(1, 1, 1, 0.7f),
        });
        return column;
    }
}
