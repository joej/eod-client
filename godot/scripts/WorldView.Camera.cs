// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the chase camera's home, as retail keeps it (camera note "Commands", "Saved settings"). Each character has
// one: a distance and height (game units), an orbit angle and a tilt (degrees, positive = above, looking down).
//   /setcamera       saves the current distance, height, angle and tilt as home
//   /resetcamera     sets home to 240 / height 10 / angle 0 / tilt 0, and applies it
//   Reset Camera key applies home (Home; D in the classic layout)
// Home is applied on entering the world and when the free-fly camera (Tab) comes back. A character that never saved one
// starts at 500 / 10 / 0 / 0 (retail's new-character home; inferred by daoc-re), unless an older, shared /setcamera
// was saved (ClientSettings.CameraHomeOf).
//
// First person (camera note "Zoom", "First-person eye"; open-answers): zooming in past 50 units switches to it, zooming
// out returns to 50; a home distance under 50 starts in it. The eye is 68 units above the feet (not scaled by the model;
// +30 mounted, when mounts come), 12 above the water while swimming. Your whole body, its name plate and its shadow are
// hidden; the torch and anything else attached stays.
// Not yet: retail's Camera Toggle key (first → third → frozen view, inferred) and the eye under water (diving, W51).

using Godot;

namespace EodClient;

public partial class WorldView
{
    private static readonly ClientSettings.CameraHome ResetHome = new(240, 10, 0, 0);
    private static readonly ClientSettings.CameraHome NewCharacterHome = new(500, 10, 0, 0);   // inferred (daoc-re)

    // This character's home: saved, else a new character's.
    private ClientSettings.CameraHome CameraHomeNow => ClientSettings.CameraHomeOf(PlayerName) ?? NewCharacterHome;

    // Where the camera is now, as a home.
    private ClientSettings.CameraHome CurrentCameraHome() => new ClientSettings.CameraHome(
        _firstPerson ? 0 : Round(_camDistance / RegionMetresPerUnit), Round(_camHeight / RegionMetresPerUnit),
        Round(Mathf.RadToDeg(_camOrbit)), Round(-Mathf.RadToDeg(_camPitch))).Clamped();

    private static float Round(float v) => System.MathF.Round(v, 1) + 0f;   // + 0 turns -0 into 0

    private void ApplyCameraHome(ClientSettings.CameraHome home)
    {
        home = home.Clamped();
        SetFirstPerson(home.Distance < 50);
        _camDistance = Mathf.Clamp(home.Distance * RegionMetresPerUnit, CamMin, CamMax);
        _camHeight = home.Height * RegionMetresPerUnit;
        _camOrbit = Mathf.Wrap(Mathf.DegToRad(home.Angle), -Mathf.Pi, Mathf.Pi);
        _camPitch = -Mathf.DegToRad(home.Tilt);   // our pitch is negative above
        GD.Print($"[camera] home: distance {home.Distance:0}, height {home.Height:0}, angle {home.Angle:0}, tilt {home.Tilt:0}");
    }

    private void SetCameraHome()
    {
        ClientSettings.CameraHome home = CurrentCameraHome();
        ClientSettings.SaveCameraHome(PlayerName, home);
        Reply($"Camera saved: distance {home.Distance:0}, height {home.Height:0}, angle {home.Angle:0}, tilt {home.Tilt:0}.");
    }

    private void ResetCameraHome()
    {
        ClientSettings.SaveCameraHome(PlayerName, ResetHome);
        ApplyCameraHome(ResetHome);
        Reply("Camera reset.");
    }

    private const float ZoomStep = 50 * RegionMetresPerUnit;   // a wheel notch (retail)
    private const float TrackpadNotches = 0.5f;                // a trackpad scroll unit, in notches (ours)
    private const float FirstPersonEyeUnits = 68, SwimEyeUnits = 12;

    private bool _firstPerson;
    private bool _ownCharacterShown = true;   // the dev overlay's "Own character" switch

    // A zoom to <paramref name="metres"/>: in past 50 units is first person, out of first person comes back at 50.
    private void ZoomTo(float metres)
    {
        if (_firstPerson)
        {
            if (metres > _camDistance - 0.001f && metres > 0)
            {
                _camDistance = CamMin;
                SetFirstPerson(false);
            }
            return;
        }
        if (metres < CamMin - 0.001f)
        {
            SetFirstPerson(true);
            return;
        }
        _camDistance = Mathf.Clamp(metres, CamMin, CamMax);
    }

    private void SetFirstPerson(bool on)
    {
        if (on == _firstPerson)
            return;
        _firstPerson = on;
        if (!on && _camArm is not null)
            _camArm.SpringLength = _camDistance;   // straight back out, no easing (retail)
        GD.Print($"[camera] {(on ? "first person" : "third person")}");
        ShowOwnBody();
    }

    // The eye above the feet, metres: 68 units, or 12 units above the water while swimming.
    private float FirstPersonEye()
    {
        float eye = FirstPersonEyeUnits * RegionMetresPerUnit;
        return _swimming ? _swimSurface + SwimEyeUnits * RegionMetresPerUnit - _body.Position.Y : eye;
    }

    // Shows or hides what draws your own character (figure, capsule, plate), keeping the camera, the torch and the
    // colliders: hidden in first person and by the dev switch.
    private void ShowOwnBody()
    {
        if (_body is null || !IsInstanceValid(_body))
            return;
        bool show = _ownCharacterShown && !_firstPerson;
        foreach (Node n in _body.GetChildren())
            if (n is Node3D n3 && n != _camPivot && n != _torch && n is not CollisionObject3D and not CollisionShape3D
                && n != _ownNameplate)
                n3.Visible = show;
        if (_ownNameplate is not null && IsInstanceValid(_ownNameplate))
        {
            StyleNameplate(_ownNameplate);   // the names settings decide when it shows at all
            _ownNameplate.Visible &= show;
        }
    }
}
