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
// Not as retail: distances under 50 units would be first person there; ours stops at 50 (no first person yet).

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
        Round(_camDistance / RegionMetresPerUnit), Round(_camHeight / RegionMetresPerUnit),
        Round(Mathf.RadToDeg(_camOrbit)), Round(-Mathf.RadToDeg(_camPitch))).Clamped();

    private static float Round(float v) => System.MathF.Round(v, 1) + 0f;   // + 0 turns -0 into 0

    private void ApplyCameraHome(ClientSettings.CameraHome home)
    {
        home = home.Clamped();
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
}
