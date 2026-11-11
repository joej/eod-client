// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the compass (retail's `compass` window; daoc-re RE/74, 2026-11-10). The art is the stock skin's compass.bmp,
// a 512 x 32 strip covering 360° with N centred at x 32 and a label every 64 px (45°). compass.xml has two controls, 64 x 16
// and 128 x 32; retail shows the small one (the owner's retail screen, 2026-11-11: 64 x 16 at UI scale 1, yellow letters on
// black), so ours draws 128 x 32 of the strip, about 90°, into 64 x 16, centred on the character's bearing. The black stays:
// retail draws it as a backing, not a colour key. Placed as retail's default.ini puts it at
// 1024 x 768 (476, 621): centred, just above quickbar 1. Settings → Game → Compass hides it (retail starts it hidden).
// The centring on the bearing is inferred from the art (the draw code wasn't traced).

using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int CompassStrip = 512, CompassView = 128, CompassHigh = 32;   // the art's strip and the slice shown
    private const int CompassWide = 64, CompassTall = 16;                        // the window (compass.xml's small control)
    private HudWindow? _compassWindow;
    private CompassBar? _compass;
    private bool _compassSubscribed;

    private void BuildCompass(CanvasLayer overlay)
    {
        string path = (_libraryPath ?? "").PathJoin("icons").PathJoin("compass.bmp");
        if (_libraryPath is null || !Godot.FileAccess.FileExists(path))
        {
            GD.Print("[hud] no compass art (make icons)");
            return;
        }
        var img = new Image();
        if (img.LoadBmpFromBuffer(Godot.FileAccess.GetFileAsBytes(path)) != Error.Ok)
            return;
        img.Convert(Image.Format.Rgba8);
        float s = UiScale;
        _compassWindow = new HudWindow("compass", new Vector2(CompassWide, CompassTall), s,
            screen => new Vector2(screen.X / 2 - CompassWide / 2f, screen.Y - 124 - CompassTall - 2))
        {
            DragAnywhere = true,
            SelfModulate = Colors.Transparent,   // no frame: the strip alone, as retail's
        };
        _compass = new CompassBar(ImageTexture.CreateFromImage(img)) { Size = new Vector2(CompassWide, CompassTall) * s, MouseFilter = Control.MouseFilterEnum.Ignore };
        _compassWindow.AddChild(_compass);
        _compassWindow.Visible = ClientSettings.ShowCompass;
        if (!_compassSubscribed)
        {
            _compassSubscribed = true;
            ClientSettings.Changed += OnCompassSetting;
            TreeExiting += () => ClientSettings.Changed -= OnCompassSetting;
        }
        overlay.AddChild(_compassWindow);
    }

    // Settings → Game → Compass: read when a setting changes, not every frame (each read opens the settings file).
    private void OnCompassSetting(bool display)
    {
        if (_compassWindow is not null && IsInstanceValid(_compassWindow))
            _compassWindow.Visible = ClientSettings.ShowCompass;
    }

    private void UpdateCompass()
    {
        if (_compassWindow is null || _compass is null)
            return;
        // Our heading 0 faces south (DAoC's +Y) and turns through west: the bearing from north is 180° on.
        _compass.Bearing = CompassBearing(HeadingFromYaw(_bodyYaw));
    }

    /// <summary>Degrees clockwise from north (0 N, 90 E) for a DAoC heading (0 south, 1024 west).</summary>
    internal static float CompassBearing(ushort heading) => ((heading & 0x0FFF) * 360f / 4096f + 180f) % 360f;

    // The strip, centred on the bearing and wrapping round.
    private sealed partial class CompassBar(Texture2D strip) : Control
    {
        private float _bearing = -1;

        public float Bearing
        {
            set
            {
                if (Mathf.Abs(value - _bearing) < 0.1f)
                    return;
                _bearing = value;
                QueueRedraw();
            }
        }

        public override void _Draw()
        {
            float scale = Size.Y / CompassHigh;
            // The strip pixel under the view's left edge; N (bearing 0) is centred at x 32.
            float left = Mathf.PosMod(32 + _bearing * CompassStrip / 360f - CompassView / 2f, CompassStrip);
            float first = Mathf.Min(CompassView, CompassStrip - left);
            DrawTextureRectRegion(strip, new Rect2(0, 0, first * scale, Size.Y), new Rect2(left, 0, first, CompassHigh));
            if (first < CompassView)
                DrawTextureRectRegion(strip, new Rect2(first * scale, 0, (CompassView - first) * scale, Size.Y),
                    new Rect2(0, 0, CompassView - first, CompassHigh));
            // A centre tick: where you face.
            DrawLine(new Vector2(Size.X / 2, 0), new Vector2(Size.X / 2, 4 * scale), new Color(1, 0.85f, 0.4f), Mathf.Max(1, 2 * scale));
        }
    }
}
