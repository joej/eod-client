// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the summary and target windows (roadmap M9; docs/ui-windows.md). Laid out as retail's skin lays
// out new_summary_window (160 x 90) and float_target_window (150 x 70): the same controls at the same
// positions, in skin pixels scaled by the UI scale (ClientSettings.UiScale; retail's ui_scale). Drawn in our
// own plain style until the skin loader draws retail's art. Both can be dragged by their top edge; where they
// were left is remembered.
//
// Summary: your name, level and realm rank; your health (red), endurance (green) and power (yellow) with their
// percentages; your target's name in its con colour and its health. Target: the target's name and health, and
// FACE, FOLLOW, STICK, INVITE, ASSIST, ATTACK. Retail's /face, /follow and /stick are the client's own (the
// server has no such commands): face turns you toward the target; follow runs after it, stopping about 5 m
// away; stick keeps you within about 2 m and facing it. A movement key ends follow and stick. Invite and assist
// are server commands; attack toggles attack mode.

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private HudWindow _summary = null!, _targetWindow = null!;
    private HudBar _hpBar = null!, _endBar = null!, _powerBar = null!, _targetBar = null!, _floatTargetBar = null!;
    private Label _summaryTitle = null!, _summaryLevel = null!, _summaryTarget = null!, _floatTargetName = null!;
    private ushort _follow;          // follow or stick: the object followed (0: none)
    private float _followMetres;     // how close to keep

    private static float UiScale
    {
        get
        {
            float s = ClientSettings.UiScale;
            if (s <= 0)
            {
                // Not set: as if the skin were laid out for a 720-pixel-high screen (1.5 at 1080p, 2 at 1440p,
                // 3 at 2160p), times the screen's density (2 on a Retina display, whose sizes are in points).
                if (DisplayServer.GetName() == "headless")
                    return 1;
                int screen = DisplayServer.WindowGetCurrentScreen();
                float density = DisplayServer.ScreenGetScale(screen);
                s = Mathf.Max(1, DisplayServer.ScreenGetSize(screen).Y / density / 720f) * density;
            }
            return Mathf.Clamp(s, 0.5f, 4f);
        }
    }

    private void BuildHud(CanvasLayer overlay)
    {
        float s = UiScale;
        // Defaults (until dragged) follow the window's size: the summary on the left edge below the dev panels,
        // the target window centred low, where retail's default.ini puts FloatTarget (71% down).
        _summary = new HudWindow("summary", new Vector2(160, 90), s, screen => new Vector2(0, screen.Y * 0.42f));
        _summaryTitle = _summary.Label(new Vector2(11, 2), new Vector2(138, 14), 11, centred: true);
        _summaryLevel = _summary.Label(new Vector2(8, 16), new Vector2(144, 12), 9);
        _hpBar = _summary.Bar(new Vector2(26, 31), 125, new Color(0.85f, 0.15f, 0.12f), new Vector2(3, 28));
        _endBar = _summary.Bar(new Vector2(26, 42), 125, new Color(0.2f, 0.75f, 0.25f), new Vector2(3, 39));
        _powerBar = _summary.Bar(new Vector2(26, 52), 125, new Color(0.9f, 0.8f, 0.2f), new Vector2(3, 50));
        _summaryTarget = _summary.Label(new Vector2(5, 61), new Vector2(150, 14), 11, centred: true);
        _targetBar = _summary.Bar(new Vector2(26, 77), 125, new Color(0.85f, 0.15f, 0.12f), new Vector2(3, 75));
        overlay.AddChild(_summary);

        _targetWindow = new HudWindow("target", new Vector2(150, 70), s,
            screen => new Vector2(screen.X / 2 - 75, screen.Y * 0.71f));
        _floatTargetName = _targetWindow.Label(new Vector2(15, 2), new Vector2(120, 13), 11, centred: true);
        _floatTargetBar = _targetWindow.Bar(new Vector2(28, 17), 115, new Color(0.85f, 0.15f, 0.12f), new Vector2(1, 14));
        _targetWindow.Button("FACE", new Vector2(4, 30), 44, FaceTarget);
        _targetWindow.Button("FOLLOW", new Vector2(50, 30), 48, () => Follow(5f));
        _targetWindow.Button("STICK", new Vector2(102, 30), 43, () => Follow(2f));
        _targetWindow.Button("INVITE", new Vector2(4, 50), 44, () => TargetCommand("/invite"));
        _targetWindow.Button("ASSIST", new Vector2(50, 50), 48, () => TargetCommand("/assist"));
        _targetWindow.Button("ATTACK", new Vector2(102, 50), 43, () => _ = SendAttack(!_attacking));
        overlay.AddChild(_targetWindow);
    }

    // Each frame: the windows show what the client holds.
    private void UpdateHud()
    {
        if (_summary is null)
            return;
        _summaryTitle.Text = _info?.Name ?? "";
        _summaryLevel.Text = _info is PlayerInfo i ? $"Level {i.Level}    RR {i.RealmRank}" : "";
        if (_vitals is StatusUpdate v)
        {
            _hpBar.Set(v.HealthPercent);
            _endBar.Set(v.EndurancePercent);
            _powerBar.Set(v.MaxMana > 0 ? v.ManaPercent : -1);
        }
        bool has = _target != 0 && _entities.TryGetValue(_target, out _);
        Entity? t = has ? _entities[_target] : null;
        string name = t is null ? "" : t.Name.Length > 0 ? t.Name : $"#{_target}";
        _summaryTarget.Text = name;
        _floatTargetName.Text = name;
        if (t is not null)
        {
            _summaryTarget.AddThemeColorOverride("font_color", t.NameColor);
            _floatTargetName.AddThemeColorOverride("font_color", t.NameColor);
        }
        _targetBar.Set(t?.Dead == true ? 0 : t?.Health ?? -1);
        _floatTargetBar.Set(t?.Dead == true ? 0 : t?.Health ?? -1);
        // Retail keeps the target window open, empty without a target; we hide it then (less clutter).
        _targetWindow.Visible = t is not null;
    }

    private void FaceTarget()
    {
        if (_target == 0 || !_entities.TryGetValue(_target, out Entity? e))
            return;
        Vector3 d = e.Node.Position - _body.Position;
        if (d.LengthSquared() > 0.01f)
            _bodyYaw = Mathf.Atan2(-d.X, -d.Z);   // the body faces its -Z
    }

    private void Follow(float metres)
    {
        if (_target == 0)
            return;
        (_follow, _followMetres) = (_target, metres);
        AppendChat(metres > 3 ? "You follow your target." : "You stick to your target.", new Color(0.6f, 0.8f, 1f));
    }

    // Called with the walk-to code each physics frame: run after the followed entity, stop when close.
    private void FollowStep()
    {
        if (_follow == 0)
            return;
        if (!_entities.TryGetValue(_follow, out Entity? e) || e.Dead)
        {
            (_follow, _autoRun) = (0, false);
            return;
        }
        Vector3 d = e.Node.Position - _body.Position;
        d.Y = 0;
        if (d.LengthSquared() > 0.01f)
            _bodyYaw = Mathf.Atan2(-d.X, -d.Z);
        _autoRun = d.Length() > _followMetres;
    }

    private void TargetCommand(string command)
    {
        if (_session is null || _target == 0)
            return;
        _ = _session.SendCommandAsync(command);
    }

    // A small game window: a panel of skin-pixel size × scale, draggable by its top 14 skin pixels.
    private sealed partial class HudWindow : Panel
    {
        private readonly string _key;
        private readonly float _scale;
        private readonly System.Func<Vector2, Vector2> _defaultAt;   // screen size → top-left, in skin pixels
        private bool _placed;                                        // dragged now, or remembered: stays put
        private Vector2? _dragFrom;

        public HudWindow(string key, Vector2 skinSize, float scale, System.Func<Vector2, Vector2> defaultAt)
        {
            (_key, _scale, _defaultAt) = (key, scale, defaultAt);
            Size = skinSize * scale;
            MouseFilter = MouseFilterEnum.Stop;
            AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.06f, 0.07f, 0.09f, 0.78f), BorderColor = new Color(0.55f, 0.85f, 1f, 0.35f),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3, CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
            });
            if (ClientSettings.WindowPosition(key) is Vector2 saved)
                (Position, _placed) = (saved * scale, true);
            Ready += () =>
            {
                Place();
                GetViewport().SizeChanged += Place;
            };
        }

        // At its default (until the player moves it), recomputed when the game window changes size; kept on screen.
        private void Place()
        {
            if (!_placed)
                Position = _defaultAt(GetViewportRect().Size / _scale) * _scale;
            Clamp();
        }

        public Label Label(Vector2 at, Vector2 size, int fontSize, bool centred = false)
        {
            var l = new Label
            {
                Position = at * _scale, Size = size * _scale, ClipText = true,
                HorizontalAlignment = centred ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore,
            };
            l.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(fontSize * _scale));
            AddChild(l);
            return l;
        }

        public HudBar Bar(Vector2 at, float width, Color colour, Vector2 numberAt)
        {
            var bar = new HudBar(width * _scale, 7 * _scale, colour) { Position = at * _scale };
            AddChild(bar);
            bar.Number = Label(numberAt, new Vector2(22, 11), 9, centred: true);
            return bar;
        }

        public void Button(string text, Vector2 at, float width, System.Action pressed)
        {
            var b = new Button
            {
                Text = text, Position = at * _scale, Size = new Vector2(width, 17) * _scale,
                FocusMode = FocusModeEnum.None, ClipText = true,
            };
            b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(8 * _scale));
            // Retail's buttons are 17 skin pixels high: no padding, or Godot's default grows them out of the frame.
            foreach ((string state, float shade) in new[] { ("normal", 0.16f), ("hover", 0.24f), ("pressed", 0.10f) })
                b.AddThemeStyleboxOverride(state, new StyleBoxFlat
                {
                    BgColor = new Color(shade, shade + 0.02f, shade + 0.05f, 0.95f),
                    BorderColor = new Color(0.55f, 0.85f, 1f, 0.3f),
                    BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                    CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2,
                });
            b.Pressed += pressed;
            AddChild(b);
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            {
                if (mb.Pressed && mb.Position.Y < 14 * _scale)
                    _dragFrom = mb.Position;
                else if (!mb.Pressed && _dragFrom is not null)
                {
                    _dragFrom = null;
                    _placed = true;
                    ClientSettings.SaveWindowPosition(_key, Position / _scale);
                }
                AcceptEvent();
            }
            else if (e is InputEventMouseMotion mm && _dragFrom is Vector2 from)
            {
                Position += mm.Position - from;
                Clamp();
                AcceptEvent();
            }
        }

        // Kept wholly on screen, as retail clamps a dragged window.
        private void Clamp()
        {
            Vector2 screen = GetViewportRect().Size;
            Position = new Vector2(Mathf.Clamp(Position.X, 0, Mathf.Max(0, screen.X - Size.X)),
                Mathf.Clamp(Position.Y, 0, Mathf.Max(0, screen.Y - Size.Y)));
        }
    }

    // A status bar: a dark track and a coloured fill for 0–100%; -1 hides the fill and the number.
    private sealed partial class HudBar : Control
    {
        private readonly ColorRect _fill;
        private readonly float _width;
        public Label? Number;

        public HudBar(float width, float height, Color colour)
        {
            _width = width;
            Size = new Vector2(width, height);
            MouseFilter = MouseFilterEnum.Ignore;
            AddChild(new ColorRect { Size = Size, Color = new Color(0, 0, 0, 0.55f), MouseFilter = MouseFilterEnum.Ignore });
            _fill = new ColorRect { Size = Size, Color = colour, MouseFilter = MouseFilterEnum.Ignore };
            AddChild(_fill);
        }

        public void Set(int percent)
        {
            _fill.Visible = percent >= 0;
            _fill.Size = new Vector2(_width * Mathf.Clamp(percent, 0, 100) / 100f, Size.Y);
            if (Number is not null)
                Number.Text = percent >= 0 ? percent.ToString() : "";
        }
    }
}
