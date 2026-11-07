// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: Jump points (roadmap M8). Each zone's doorways (city gates, dungeon entrances, portals; the
// zone's jumps.csv, from zonejump.csv) are lines on the ground. Walking through one sends its zone point
// (C->S 0x90); the server then moves the player, which arrives like any other move: 0x04 in the region,
// 0xB7 and a new world entry for another region (CharacterScreen shows a loading screen meanwhile).

using DaocAssets.Zone;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private Vector3? _lastJumpCheck;      // where the character was at the last check (Godot metres)
    private double _jumpQuietUntil;       // no second jump request until then (the server is moving us)
    private bool _jumpsVisible;           // the dev overlay's "Jump points" switch

    // A zone's doorways, and (hidden unless the dev switch is on) a glowing pane for each.
    private static System.Collections.Generic.IEnumerable<bool> JumpSteps(ZoneParts parts)
    {
        string csv = parts.Dir.Length == 0 ? "" : parts.Dir.PathJoin("jumps.csv");
        if (csv.Length == 0 || !Godot.FileAccess.FileExists(csv))
            yield break;
        parts.Jumps = ZoneJumps.ReadCsv(Godot.FileAccess.GetFileAsString(csv));
        if (parts.Jumps.Count == 0)
            yield break;
        var root = new Node3D { Name = "Jumps", Visible = false };
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.2f, 0.9f, 1f, 0.35f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        foreach (ZoneJump j in parts.Jumps)
        {
            Vector3 a = ToGodot(j.X1, j.Y1, j.Z1), b = ToGodot(j.X2, j.Y2, j.Z1);
            float height = Mathf.Max((j.Z2 - j.Z1) * RegionMetresPerUnit, 1f);
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            Vector3 up = new(0, height, 0);
            foreach (Vector3 v in new[] { a, b, b + up, a, b + up, a + up })
                st.AddVertex(v);
            var pane = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = material };
            pane.AddChild(new Label3D
            {
                Text = $"{j.Name} → zone point {j.ZonePoint}", Position = (a + b) / 2 + up + new Vector3(0, 0.5f, 0),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 48, NoDepthTest = true,
            });
            root.AddChild(pane);
        }
        parts.Root.AddChild(root);
        parts.JumpMarkers = root;
        yield return true;
    }

    // Called each physics frame after the character moved: a step through a doorway asks for its jump.
    private void CheckJumpPoints()
    {
        Vector3 now = _body.Position;
        Vector3? before = _lastJumpCheck;
        _lastJumpCheck = now;
        // A teleport (the server moved us) isn't a step through anything.
        if (before is not Vector3 from || from.DistanceTo(now) > 20f || _seconds < _jumpQuietUntil)
            return;
        (float ax, float ay, _) = ToRegion(from);
        (float bx, float by, float bz) = ToRegion(now);
        foreach (ZoneParts zone in _zones.Values)
            foreach (ZoneJump j in zone.Jumps)
                if (ZoneJumps.Crosses(j, ax, ay, bx, by, bz))
                {
                    UseJump(j);
                    return;
                }
    }

    private void UseJump(ZoneJump j)
    {
        _jumpQuietUntil = _seconds + 3;
        GD.Print($"[world] through jump point {j.Name} (zone point {j.ZonePoint})");
        AppendChat($"Through {j.Name}…", new Color(0.6f, 0.8f, 1f));
        if (_session is not null)
            _ = _session.UseJumpPointAsync(j.ZonePoint);
    }

    private void ShowJumpMarkers(bool on)
    {
        _jumpsVisible = on;
        foreach (ZoneParts z in _zones.Values)
            if (z.JumpMarkers is not null)
                z.JumpMarkers.Visible = on;
    }
}
