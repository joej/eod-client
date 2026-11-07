// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: Click-to-inspect: the model, mesh, material and shadow under the cursor.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    // Drops the inspect highlight and readout (Esc, or clicking the selected object again).
    private void ClearInspect()
    {
        if (_selected is not null && IsInstanceValid(_selected))
            _selected.MaterialOverlay = null;
        _selected = null;
        _dev.SetInspect("Left-click a surface to inspect it.", false);
    }

    // Casts from the camera through the clicked pixel and reports what it hit: for a fixture, its
    // .glb and zone.csv placement, the mesh node, and every surface's material (texture stem,
    // textured or not, UVs or not, colour, transparency) with the hit surface marked. That is the
    // question M7a keeps asking about magenta walls, flat-tan stone and opaque foliage.
    // What stands between a point and the sun: a ray from just above the point toward the light,
    // against everything that has collision (zone models, terrain). Names the first model it meets, or
    // says the point is in sunlight. Figures cast shadows too but have no collision, so they don't show.
    private string ShadowCaster(Vector3 point)
    {
        Vector3 towardSun = _sun.GlobalTransform.Basis.Z.Normalized();   // a DirectionalLight3D shines along -Z
        var ray = PhysicsRayQueryParameters3D.Create(point + towardSun * 0.05f, point + towardSun * 400f);
        ray.HitBackFaces = true;
        ray.CollisionMask = ~EntityLayer;
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider)
            return "shadow  none (in sunlight)";
        Node? n = collider.HasMeta("terrain_mesh") ? null : collider.GetParent();
        while (n is not null && !n.HasMeta("glb"))
            n = n.GetParent();
        float distance = point.DistanceTo(hit["position"].AsVector3());
        (float x, float y, float z) = ToRegion(hit["position"].AsVector3());
        string what = n is not null ? n.GetMeta("glb").AsString() : collider.HasMeta("terrain_mesh") ? "terrain" : collider.Name;
        string mesh = collider.GetParent() is MeshInstance3D mi ? $" {mi.Name}" : "";
        return $"shadow  from {what}{mesh}, {distance:0} m toward the sun (x {x:0} y {y:0} z {z:0})";
    }

    private void Inspect(Vector2 screen)
    {
        Vector3 from = _camera.ProjectRayOrigin(screen);
        var ray = PhysicsRayQueryParameters3D.Create(from, from + _camera.ProjectRayNormal(screen) * 4000f);
        ray.HitBackFaces = true;
        uint mask = 0;
        if (_fixturesOn) mask |= ~TerrainLayer & ~EntityLayer;
        if (_terrainOn) mask |= TerrainLayer;
        ray.CollisionMask = mask;
        Godot.Collections.Dictionary hit = mask == 0
            ? new Godot.Collections.Dictionary()
            : GetWorld3D().DirectSpaceState.IntersectRay(ray);

        MeshInstance3D? previous = _selected;
        ClearInspect();

        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider)
        {
            _dev.SetInspect("Nothing hit (sky, or a hidden layer).", false);
            return;
        }

        MeshInstance3D? mesh = collider.HasMeta("terrain_mesh")
            ? collider.GetMeta("terrain_mesh").AsGodotObject() as MeshInstance3D
            : collider.GetParent() as MeshInstance3D;
        (float hx, float hy, float hz) = ToRegion(hit["position"].AsVector3());
        int face = hit.ContainsKey("face_index") ? hit["face_index"].AsInt32() : -1;

        // Clicking the highlighted object again deselects it.
        if (mesh is not null && mesh == previous)
            return;

        var sb = new System.Text.StringBuilder();
        Node? placed = mesh;
        while (placed is not null && !placed.HasMeta("glb"))
            placed = placed.GetParent();
        if (placed is not null)
        {
            sb.AppendLine($"model   {placed.GetMeta("glb").AsString()}");
            sb.AppendLine($"placed  {placed.GetMeta("placement").AsString()}");
        }
        else if (collider.HasMeta("terrain_mesh"))
        {
            sb.AppendLine("model   terrain");
        }
        sb.AppendLine($"hit     x {hx:0} y {hy:0} z {hz:0}" + (face >= 0 ? $"  face {face}" : ""));
        sb.AppendLine(ShadowCaster(hit["position"].AsVector3()));

        if (mesh is null || mesh.Mesh is null)
        {
            sb.Append("(no mesh behind this collider)");
        }
        else
        {
            sb.AppendLine($"mesh    {mesh.Name}");
            int hitSurface = SurfaceForFace(mesh.Mesh, face);
            for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
                sb.AppendLine((s == hitSurface ? "> " : "  ") + DescribeSurface(mesh, s));
            mesh.MaterialOverlay = _highlight;
            _selected = mesh;
        }

        string text = sb.ToString().TrimEnd();
        _dev.SetInspect(text, true);
        GD.Print("[inspect]\n" + text);
    }

    // The trimesh collider lists triangles surface by surface, in order, so a face index maps back
    // to its surface by cumulative triangle counts. -1 when unknown (no index, not an ArrayMesh).
    private static int SurfaceForFace(Mesh mesh, int face)
    {
        if (face < 0 || mesh is not ArrayMesh am)
            return -1;
        int start = 0;
        for (int s = 0; s < am.GetSurfaceCount(); s++)
        {
            int indices = am.SurfaceGetArrayIndexLen(s);
            int tris = (indices > 0 ? indices : am.SurfaceGetArrayLen(s)) / 3;
            if (face < start + tris)
                return s;
            start += tris;
        }
        return -1;
    }

    private static string DescribeSurface(MeshInstance3D mi, int s)
    {
        bool uv = mi.Mesh is ArrayMesh am && (am.SurfaceGetFormat(s) & Mesh.ArrayFormat.FormatTexUV) != 0;
        if (mi.GetActiveMaterial(s) is not BaseMaterial3D m)
            return $"s{s}  (no material)  {(uv ? "uv" : "NO UV")}";
        string name = string.IsNullOrEmpty(m.ResourceName) ? "(unnamed)" : m.ResourceName;
        string tex = m.AlbedoTexture is null ? "NO TEXTURE" : "textured";
        string alpha = m.Transparency == BaseMaterial3D.TransparencyEnum.Disabled ? "" : $"  {m.Transparency}".ToLowerInvariant();
        return $"s{s}  {name}  {tex}  {(uv ? "uv" : "NO UV")}  #{m.AlbedoColor.ToHtml(false)}{alpha}";
    }
}
