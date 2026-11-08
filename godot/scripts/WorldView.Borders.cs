// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: zone borders (dev overlay, roadmap M8; the owner's request, 2026-11-07). Each zone of the region
// is a rectangle in region units (World/zones.tsv); the "Zone borders" switch draws a translucent wall along
// each edge, set half a metre inside its own zone (so two neighbours' walls stand side by side), with the
// zone's name and number on it. The server learns the zone only from each 0xA9's zone field, so this shows
// where the client will start reporting the next one. Gaps between zones (region 1 has some) show as walls
// with nothing behind them.

using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private static bool _bordersVisible;     // kept across region changes, like the jump points switch
    private Node3D? _borders;
    private System.Action? _flipBordersToggle;

    private void ShowZoneBorders(bool on)
    {
        _bordersVisible = on;
        _borders?.QueueFree();
        _borders = null;
        if (!on)
            return;
        _borders = new Node3D { Name = "ZoneBorders" };
        _borderLabels.Clear();
        float eye = _body.Position.Y;
        var wall = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.55f, 0.1f, 0.18f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var edge = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.7f, 0.2f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        int zones = 0;
        foreach (Zone z in Zones.All.Where(z => z.RegionId == _world.Position.RegionId))
        {
            zones++;
            const float inset = 0.5f;
            Vector3 a = ToGodot(z.X, z.Y, 0) + new Vector3(inset, 0, inset);
            Vector3 c = ToGodot(z.X + z.Width, z.Y + z.Height, 0) - new Vector3(inset, 0, inset);
            Vector3 b = new(c.X, 0, a.Z), d = new(a.X, 0, c.Z);
            foreach ((Vector3 from, Vector3 to) in new[] { (a, b), (b, c), (c, d), (d, a) })
            {
                // A wall from 60 m below to 120 m above where you stand, and a bright line at eye height.
                var st = new SurfaceTool();
                st.Begin(Mesh.PrimitiveType.Triangles);
                Vector3 lo = new(0, eye - 60, 0), hi = new(0, eye + 120, 0);
                foreach (Vector3 v in new[] { from + lo, to + lo, to + hi, from + lo, to + hi, from + hi })
                    st.AddVertex(v);
                _borders.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = wall, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                Vector3 mid = (from + to) / 2;
                float length = from.DistanceTo(to);
                _borders.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(length, 0.15f, 0.15f) },
                    MaterialOverride = edge,
                    Position = new Vector3(mid.X, eye + 1.5f, mid.Z),
                    Rotation = new Vector3(0, Mathf.Atan2(-(to.Z - from.Z), to.X - from.X), 0),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
                // The name a few metres inside, so each side of a shared border reads as its own zone; it follows
                // you along the edge (UpdateBorderLabels), since an edge is 1.6 km long.
                Vector3 inward = (((a + c) / 2) - mid) with { Y = 0 };
                var label = new Label3D
                {
                    Text = $"{z.Name} ({z.Id})",
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FixedSize = true, PixelSize = 0.0012f, FontSize = 32,
                    OutlineSize = 8, Modulate = new Color(1f, 0.8f, 0.4f), VisibilityRangeEnd = 150, NoDepthTest = true,   // a debug label: through hills
                };
                _borders.AddChild(label);
                _borderLabels.Add((label, from, to, inward.Normalized() * 3f));
            }
        }
        AddChild(_borders);
        GD.Print($"[world] zone borders: {zones} zones of region {_world.Position.RegionId}");
    }

    private readonly System.Collections.Generic.List<(Label3D Label, Vector3 From, Vector3 To, Vector3 Inward)> _borderLabels = [];

    // Each frame while shown: every edge's name at the point of the edge nearest the character, at eye height.
    private void UpdateBorderLabels()
    {
        if (_borders is null)
            return;
        Vector3 at = _body.Position;
        foreach ((Label3D label, Vector3 from, Vector3 to, Vector3 inward) in _borderLabels)
        {
            Vector3 edge = to - from;
            float t = Mathf.Clamp((at - from).Dot(edge) / edge.LengthSquared(), 0, 1);
            Vector3 p = from + edge * t + inward;
            label.Position = new Vector3(p.X, at.Y + 3f, p.Z);
        }
    }
}
