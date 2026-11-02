// SPDX-License-Identifier: GPL-3.0-or-later
//
// Development overlay for the world view: dark translucent panels, small monospace text, one accent
// colour. It holds the status readout, layer toggles, a render-mode switch (normal / wireframe /
// unshaded / overdraw) and the click-to-inspect readout that M7a work leans on ("which model, mesh
// and material is this magenta wall?"). WorldView owns the scene; this class only draws controls and
// calls back. It is a debug build's tool, not the game HUD (that is M8; see docs/roadmap.md).

using Godot;

namespace EodClient;

public partial class DevOverlay : CanvasLayer
{
    public static readonly Color Accent = new(0.74f, 0.9f, 0.36f);
    private static readonly Color Text = new(0.86f, 0.88f, 0.84f);
    private static readonly Color Dim = new(0.55f, 0.58f, 0.54f);

    private static readonly Viewport.DebugDrawEnum[] RenderModes =
    {
        Viewport.DebugDrawEnum.Disabled,
        Viewport.DebugDrawEnum.Wireframe,
        Viewport.DebugDrawEnum.Unshaded,
        Viewport.DebugDrawEnum.Overdraw,
    };

    private readonly SystemFont _mono = new()
    {
        FontNames = new[] { "Menlo", "SF Mono", "Consolas", "DejaVu Sans Mono", "monospace" },
    };

    private Label _status = null!;
    private Label _inspect = null!;
    private Button _renderButton = null!;
    private VBoxContainer _layers = null!;
    private VBoxContainer _statusBox = null!;
    private Control _root = null!;
    private int _renderMode;

    /// <summary>The status readout (top left). WorldView rewrites it every frame.</summary>
    public string Status { set => _status.Text = value; }

    /// <summary>Hidden panels still exist; F1 just stops drawing them.</summary>
    public bool PanelsVisible
    {
        get => _root.Visible;
        set => _root.Visible = value;
    }

    public override void _Ready()
    {
        // Wireframe debug draw only works for meshes created after this is switched on, so do it
        // before WorldView loads terrain and zone models (it adds this overlay first).
        RenderingServer.SetDebugGenerateWireframes(true);

        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // Top left: status.
        _statusBox = Panel("STATUS", out PanelContainer statusPanel);
        statusPanel.Position = new Vector2(16, 16);
        _status = SmallLabel("", Text);
        _statusBox.AddChild(_status);

        // Top right: layers + render mode.
        _layers = Panel("LAYERS", out PanelContainer layersPanel);
        layersPanel.AnchorLeft = layersPanel.AnchorRight = 1f;
        layersPanel.OffsetLeft = -236;
        layersPanel.OffsetRight = -16;
        layersPanel.OffsetTop = 16;
        layersPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _renderButton = new Button { FocusMode = Control.FocusModeEnum.None, Flat = true, Alignment = HorizontalAlignment.Left };
        StyleFont(_renderButton, Accent);
        _renderButton.Pressed += CycleRenderMode;
        _layers.AddChild(_renderButton);
        ShowRenderMode();

        // Bottom right: inspect readout.
        VBoxContainer inspectBox = Panel("INSPECT", out PanelContainer inspectPanel);
        inspectPanel.AnchorLeft = inspectPanel.AnchorRight = 1f;
        inspectPanel.AnchorTop = inspectPanel.AnchorBottom = 1f;
        inspectPanel.OffsetLeft = -436;
        inspectPanel.OffsetRight = -16;
        inspectPanel.OffsetBottom = -16;
        inspectPanel.GrowHorizontal = Control.GrowDirection.Begin;
        inspectPanel.GrowVertical = Control.GrowDirection.Begin;
        _inspect = SmallLabel("Left-click a surface to inspect it.", Dim);
        _inspect.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _inspect.CustomMinimumSize = new Vector2(400, 0);
        inspectBox.AddChild(_inspect);
    }

    /// <summary>Adds a checkbox to the layers panel; <paramref name="onChange"/> runs on toggle.</summary>
    public void AddToggle(string label, bool on, System.Action<bool> onChange)
    {
        var box = new CheckBox { Text = label, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None };
        StyleFont(box, Text);
        box.Toggled += pressed => onChange(pressed);
        // Keep the render-mode button last.
        _layers.AddChild(box);
        _layers.MoveChild(box, _renderButton.GetIndex());
    }

    /// <summary>Adds a button to the layers panel that cycles through <paramref name="options"/>;
    /// returns a function that advances it (for a hotkey).</summary>
    public System.Action AddChoice(string label, string[] options, int index, System.Action<int> onChange)
    {
        var button = new Button { FocusMode = Control.FocusModeEnum.None, Flat = true, Alignment = HorizontalAlignment.Left };
        StyleFont(button, Accent);
        int current = index;
        void Show() => button.Text = $"{label}: {options[current]}";
        void Next()
        {
            current = (current + 1) % options.Length;
            Show();
            onChange(current);
        }
        button.Pressed += Next;
        Show();
        _layers.AddChild(button);
        _layers.MoveChild(button, _renderButton.GetIndex() + 1);
        return Next;
    }

    /// <summary>Adds a control (e.g. the quit button) under the status text.</summary>
    public void AddToStatus(Control c) => _statusBox.AddChild(c);

    /// <summary>Adds a text line under the toggles (counts and similar).</summary>
    public Label AddInfo(string text)
    {
        Label l = SmallLabel(text, Dim);
        _layers.AddChild(l);
        return l;
    }

    public void SetInspect(string text, bool found)
    {
        _inspect.Text = text;
        _inspect.AddThemeColorOverride("font_color", found ? Text : Dim);
    }

    public void CycleRenderMode()
    {
        _renderMode = (_renderMode + 1) % RenderModes.Length;
        GetViewport().DebugDraw = RenderModes[_renderMode];
        ShowRenderMode();
    }

    private void ShowRenderMode() =>
        _renderButton.Text = $"F2 render: {RenderModes[_renderMode].ToString().ToLowerInvariant()}";

    // A titled panel: dark translucent box, accent title in small caps, content below.
    private VBoxContainer Panel(string title, out PanelContainer panel)
    {
        // Stop: a click on a panel must not fall through to the world as an inspect click.
        panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.05f, 0.8f),
            BorderColor = new Color(1f, 1f, 1f, 0.08f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        panel.AddChild(box);
        box.AddChild(SmallLabel(title, Accent));
        _root.AddChild(panel);
        return box;
    }

    private Label SmallLabel(string text, Color color)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        StyleFont(l, color);
        return l;
    }

    private void StyleFont(Control c, Color color)
    {
        c.AddThemeFontOverride("font", _mono);
        c.AddThemeFontSizeOverride("font_size", 12);
        c.AddThemeColorOverride("font_color", color);
    }
}
