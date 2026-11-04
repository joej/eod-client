// SPDX-License-Identifier: GPL-3.0-or-later
//
// The player's settings window (login screen button; F10 or the Settings button in the world):
// display (fullscreen, a maximized window, or a common window size that fits this screen) and field
// of view. Changes are saved at once (ClientSettings) and raise ClientSettings.Changed, so the world
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

    private const int FullscreenItem = 0, MaximizedItem = 1;
    private readonly List<Vector2I> _sizes = [];

    /// <summary>Opens the window over <paramref name="parent"/>'s scene (one at a time).</summary>
    public static void Open(Node parent)
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
    }

    public override void _Ready()
    {
        Title = "Settings";
        OkButtonText = "Close";
        Exclusive = false;
        Confirmed += QueueFree;
        Canceled += QueueFree;

        // AcceptDialog lays out one child: a column holding the settings grid and a note.
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        AddChild(column);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 10);
        column.AddChild(grid);

        grid.AddChild(new Label { Text = "Display" });
        var display = new OptionButton { CustomMinimumSize = new Vector2(260, 0) };
        display.AddItem("Fullscreen (F11)", FullscreenItem);
        display.AddItem("Maximized (fit the screen)", MaximizedItem);
        Rect2I usable = DisplayServer.GetName() == "headless"
            ? new Rect2I(0, 0, 3840, 2160)
            : DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        Vector2I? saved = ClientSettings.WindowSize;
        foreach (Vector2I s in CommonSizes.Where(s => s.X <= usable.Size.X && s.Y <= usable.Size.Y))
            _sizes.Add(s);
        if (saved is Vector2I custom && !_sizes.Contains(custom))
            _sizes.Add(custom);   // set with --window: keep it choosable
        foreach (Vector2I s in _sizes)
            display.AddItem($"{s.X} × {s.Y}" + (CommonSizes.Contains(s) ? "" : " (custom)"), 2 + _sizes.IndexOf(s));
        display.Select(ClientSettings.Fullscreen ? FullscreenItem
            : saved is Vector2I v ? 2 + _sizes.IndexOf(v) : MaximizedItem);
        display.ItemSelected += index =>
        {
            int id = display.GetItemId((int)index);
            ClientSettings.Fullscreen = id == FullscreenItem;
            if (id != FullscreenItem)
                ClientSettings.WindowSize = id == MaximizedItem ? null : _sizes[id - 2];
            ClientSettings.NotifyChanged(display: true);
        };
        grid.AddChild(display);

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
            Text = "The display setting is for the game world; the login screen stays a small window.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0),
        });
    }
}
