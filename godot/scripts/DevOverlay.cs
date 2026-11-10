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
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
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
        Resizable(inspectPanel, _inspect, () => 400, inspectPanel);
        // The status text wraps to stop short of the layers panel (whose width follows its toggles and stats).
        Resizable(statusPanel, _status, () => Mathf.Max(320, layersPanel.GlobalPosition.X - 16 - 16 - 24 - 12), layersPanel);
    }

    /// <summary>Adds a checkbox to the layers panel; <paramref name="onChange"/> runs on toggle.
    /// Returns a function that flips it (for a hotkey).</summary>
    public System.Action AddToggle(string label, bool on, System.Action<bool> onChange)
    {
        var box = new CheckBox { Text = label, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None };
        StyleFont(box, Text);
        box.Toggled += pressed => onChange(pressed);
        // Keep the render-mode button last.
        _layers.AddChild(box);
        _layers.MoveChild(box, _renderButton.GetIndex());
        return () => box.ButtonPressed = !box.ButtonPressed;
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

    // A panel whose text wraps to a width the player can change with the grip at its bottom-right corner; the width starts
    // at defaultWidth (re-applied when the game window resizes, until the player sets one).
    private static void Resizable(PanelContainer panel, Label text, System.Func<float> defaultWidth, Control follows)
    {
        bool chosen = false;
        void Fit()
        {
            if (!chosen)
                SetWidth(panel, text, defaultWidth());
        }
        // Fitted after layout, and again whenever the panel it keeps clear of moves or resizes (that covers the game
        // window resizing too).
        Callable.From(Fit).CallDeferred();
        follows.ItemRectChanged += Fit;
        var grip = new Label { Text = "◢", MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.Fdiagsize, Modulate = new Color(1, 1, 1, 0.35f) };
        grip.AddThemeFontSizeOverride("font_size", 12);
        // A PanelContainer lays out every child; the grip goes on a free layer over it, in its bottom-right corner (pushed
        // into the panel's margin).
        var layer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddChild(layer);
        layer.AddChild(grip);
        grip.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        grip.Position += new Vector2(10, 8);
        bool dragging = false;
        grip.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            {
                dragging = b.Pressed;
                grip.AcceptEvent();
            }
            else if (e is InputEventMouseMotion m && dragging)
            {
                chosen = true;
                SetWidth(panel, text, text.CustomMinimumSize.X + m.Relative.X);
                grip.AcceptEvent();
            }
        };
    }

    private static void SetWidth(PanelContainer panel, Label text, float width)
    {
        text.CustomMinimumSize = new Vector2(Mathf.Max(160, width), 0);
        panel.ResetSize();
    }

    private void ShowRenderMode() =>
        _renderButton.Text = $"F2 render: {RenderModes[_renderMode].ToString().ToLowerInvariant()}";

    // A titled panel: dark translucent box, accent title in small caps, content below.
    private VBoxContainer Panel(string title, out PanelContainer panel)
    {
        // Stop: a click on a panel must not fall through to the world as an inspect click.
        panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        Draggable(panel);
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

    // Moved by dragging anywhere on it (its controls keep their clicks). A panel anchored to the right or bottom edge is
    // re-anchored to the top left on its first move, keeping its place on screen.
    private static void Draggable(PanelContainer panel)
    {
        Vector2? from = null;
        panel.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            {
                from = b.Pressed ? b.GlobalPosition : null;
                if (b.Pressed)
                    foreach (Side side in new[] { Side.Left, Side.Top, Side.Right, Side.Bottom })
                        panel.SetAnchor(side, 0, keepOffset: true);
                panel.AcceptEvent();
            }
            else if (e is InputEventMouseMotion m && from is Vector2 start)
            {
                Vector2 screen = panel.GetViewportRect().Size;
                Vector2 to = panel.Position + (m.GlobalPosition - start);
                panel.Position = new Vector2(Mathf.Clamp(to.X, 0, Mathf.Max(0, screen.X - panel.Size.X)), Mathf.Clamp(to.Y, 0, Mathf.Max(0, screen.Y - panel.Size.Y)));
                from = m.GlobalPosition;
                panel.AcceptEvent();
            }
        };
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
