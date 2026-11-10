// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the summary and target windows (roadmap M9; docs/ui-windows.md). Laid out as retail's skin lays
// out new_summary_window (160 x 90) and float_target_window (150 x 70): the same controls at the same
// positions, in skin pixels scaled by the UI scale (ClientSettings.UiScale; retail's ui_scale). Drawn in our
// own plain style until the skin loader draws retail's art. Both can be dragged by their top edge; where they
// were left is remembered.
//
// Summary: your name, level and realm rank; your health (red), endurance (green) and power (yellow) with their
// percentages (the power bar always, empty for a class without power); your target's name in its con colour and its health. Target: the target's name and health, and
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
    private Button? _inviteButton, _assistButton;

    private static void Dim(Button? b, bool off)
    {
        if (b is not null && b.Disabled != off)
            b.Disabled = off;
    }
    private HudBar _hpBar = null!, _endBar = null!, _powerBar = null!, _targetBar = null!, _floatTargetBar = null!;
    private Label _summaryTitle = null!, _summaryLevel = null!, _summaryTarget = null!, _floatTargetName = null!;
    private ushort _follow;          // follow or stick: the object followed (0: none)
    private float _followMetres;     // how close to keep

    /// <summary>The interface size in use: the player's, else the screen's (see ClientSettings.UiScale).</summary>
    internal static float UiScale
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
        // Defaults (until dragged) follow the window's size: the summary on the left edge below the dev panels and above
        // quickbar 3,
        // the target window centred low, where retail's default.ini puts FloatTarget (y 547 at 1024 x 768: 221 skin
        // pixels above the bottom, clear of the quickbars).
        _builtClassicSummary = ClientSettings.ClassicSummary;
        if (_builtClassicSummary)
            BuildClassicSummary(s);
        else
        {
            _summary = new HudWindow("summary", new Vector2(160, 90), s, screen => new Vector2(0, screen.Y * 0.29f));
            _summaryTitle = _summary.Label(new Vector2(11, 2), new Vector2(138, 14), 11, centred: true);
            _summaryLevel = _summary.Label(new Vector2(8, 16), new Vector2(144, 12), 9);
            _hpBar = _summary.Bar(new Vector2(26, 31), 125, new Color(0.85f, 0.15f, 0.12f), new Vector2(3, 28));
            _endBar = _summary.Bar(new Vector2(26, 42), 125, new Color(0.2f, 0.75f, 0.25f), new Vector2(3, 39));
            _powerBar = _summary.Bar(new Vector2(26, 52), 125, new Color(0.9f, 0.8f, 0.2f), new Vector2(3, 50));
            _summaryTarget = _summary.Label(new Vector2(5, 61), new Vector2(150, 14), 11, centred: true);
            _targetBar = _summary.Bar(new Vector2(26, 77), 125, new Color(0.85f, 0.15f, 0.12f), new Vector2(3, 75));
            (_summaryXp, _summaryXpFine) = (null, null);
        }
        overlay.AddChild(_summary);

        _targetWindow = new HudWindow("target", new Vector2(150, 70), s,
            screen => new Vector2(screen.X / 2 - 75, screen.Y - 221));
        _floatTargetName = _targetWindow.Label(new Vector2(15, 2), new Vector2(120, 13), 11, centred: true);
        _floatTargetBar = _targetWindow.Bar(new Vector2(28, 17), 115, new Color(0.85f, 0.15f, 0.12f), new Vector2(1, 14));
        _targetWindow.Button("FACE", new Vector2(4, 30), 44, FaceTarget);
        _targetWindow.Button("FOLLOW", new Vector2(50, 30), 48, () => Follow(5f));
        _targetWindow.Button("STICK", new Vector2(102, 30), 43, () => Follow(2f));
        _inviteButton = _targetWindow.Button("INVITE", new Vector2(4, 50), 44, () => TargetCommand("/invite"));
        _assistButton = _targetWindow.Button("ASSIST", new Vector2(50, 50), 48, () => TargetCommand("/assist"));
        _targetWindow.Button("ATTACK", new Vector2(102, 50), 43, () => _ = SendAttack(!_attacking));
        overlay.AddChild(_targetWindow);
        // Offline (no server: the fixed camera views) there's nothing to show, and they would hide the world.
        _summary.Visible = _session is not null;
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
            // Always drawn, as retail's: a class without power shows it empty (0), not missing.
            _powerBar.Set(v.MaxMana > 0 ? v.ManaPercent : 0);
        }
        if (_summaryXp is not null)
        {
            _summaryXp.Full = _points?.Bubbles ?? 0;
            _summaryXpFine!.Set(_points?.BubbleProgress ?? 0);
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
        // Retail's buttons are always live (float_target_window.xml has no rules); ours dim where the server would refuse:
        // INVITE only a player of your realm; ASSIST anyone of your realm, players and friendly NPCs (a guard: its target),
        // not monsters or enemies (OpenDAoC's /assist: "We cannot assist npc's or players of an enemy realm").
        bool ours = t is not null && t.Realm != 0 && (PlayerRealm == Realm.None || t.Realm == (int)PlayerRealm);
        Dim(_inviteButton, !(ours && t!.IsPlayer));
        Dim(_assistButton, !ours);
        // Retail keeps the target window open, empty without a target; we hide it then (less clutter).
        _targetWindow.Visible = t is not null && _session is not null;
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
        private Vector2? _home;                                      // where the player put it (skin pixels), or null: the default;
                                                                     // a negative coordinate counts from the right or bottom edge
        public bool DragAnywhere;                                    // dragged by any free spot, not just the top edge
        public System.Action<int>? Wheel;                            // the mouse wheel over it: -1 up, +1 down
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
            _home = ClientSettings.WindowPosition(key);
            Ready += () =>
            {
                Place();
                GetViewport().SizeChanged += Place;
            };
        }

        // Where the player put it, else its default, recomputed whenever the game window changes size; kept on screen. The
        // clamp never changes the home: the world first opens in the small login window, and clamping a remembered place to
        // that lost it once the window grew (owner, 2026-11-10: "quickbar placement is not remembered").
        private void Place()
        {
            Vector2 screen = GetViewportRect().Size / _scale;
            Position = (_home is Vector2 h ? FromEdges(h, screen) : _defaultAt(screen)) * _scale;
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

        public HudBar Bar(Vector2 at, float width, Color colour, Vector2 numberAt, float height = 7)
        {
            var bar = new HudBar(width * _scale, height * _scale, colour) { Position = at * _scale };
            AddChild(bar);
            bar.Number = Label(numberAt, new Vector2(22, 11), 9, centred: true);
            return bar;
        }

        public Button Button(string text, Vector2 at, float width, System.Action pressed, float height = 17)
        {
            var b = new Button
            {
                Text = text, Position = at * _scale, Size = new Vector2(width, height) * _scale,
                FocusMode = FocusModeEnum.None, ClipText = true,
            };
            b.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(System.Math.Min(8, height - 2) * _scale));
            // Retail's buttons are 17 skin pixels high: no padding, or Godot's default grows them out of the frame.
            b.AddThemeColorOverride("font_disabled_color", new Color(0.5f, 0.52f, 0.55f, 0.6f));   // dimmed: can't apply here
            foreach ((string state, float shade) in new[] { ("normal", 0.16f), ("hover", 0.24f), ("pressed", 0.10f), ("disabled", 0.08f) })
                b.AddThemeStyleboxOverride(state, new StyleBoxFlat
                {
                    BgColor = new Color(shade, shade + 0.02f, shade + 0.05f, 0.95f),
                    BorderColor = new Color(0.55f, 0.85f, 1f, 0.3f),
                    BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
                    CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2,
                });
            b.Pressed += pressed;
            AddChild(b);
            return b;
        }

        // A small clickable glyph (a page arrow): a label, not a button, so it fits a few skin pixels.
        public Label Arrow(string glyph, Vector2 at, Vector2 size, System.Action pressed)
        {
            var l = new Label
            {
                Text = glyph, Position = at * _scale, Size = size * _scale, MouseFilter = MouseFilterEnum.Stop,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                MouseDefaultCursorShape = CursorShape.PointingHand,
            };
            l.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(7 * _scale));
            l.AddThemeColorOverride("font_color", new Color(0.6f, 0.85f, 1f));
            l.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    pressed();
                    l.AcceptEvent();
                }
            };
            AddChild(l);
            return l;
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel && Wheel is not null)
            {
                Wheel(wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
                AcceptEvent();
                return;
            }
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            {
                if (mb.Pressed && (DragAnywhere || mb.Position.Y < 14 * _scale))
                    _dragFrom = mb.Position;
                else if (!mb.Pressed && _dragFrom is not null)
                {
                    _dragFrom = null;
                    _home = ToEdges(Position / _scale, GetViewportRect().Size / _scale);
                    ClientSettings.SaveWindowPosition(_key, _home.Value);
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

        // A window left in the right or bottom half of the screen keeps its distance from that edge (stored negative: its
        // far side's offset from the edge), so it stays in its corner when the interface size or the window changes. Retail
        // stores the top-left corner only; at a bigger size the bars near the bottom then piled up on each other.
        private Vector2 ToEdges(Vector2 at, Vector2 screen)
        {
            Vector2 skin = Size / _scale;
            float x = at.X + skin.X / 2 > screen.X / 2 ? -(screen.X - at.X - skin.X) - 1 : at.X;
            float y = at.Y + skin.Y / 2 > screen.Y / 2 ? -(screen.Y - at.Y - skin.Y) - 1 : at.Y;
            return new Vector2(x, y);
        }

        private Vector2 FromEdges(Vector2 home, Vector2 screen)
        {
            Vector2 skin = Size / _scale;
            return new Vector2(home.X < 0 ? screen.X - skin.X + home.X + 1 : home.X, home.Y < 0 ? screen.Y - skin.Y + home.Y + 1 : home.Y);
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
