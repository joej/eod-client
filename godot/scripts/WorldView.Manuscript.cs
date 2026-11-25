// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the Manuscript look (Settings → Look; --look classic|manuscript overrides the setting, for paired shots).
// A quad held in front of the camera repaints the opaque scene with pigments, ink outlines and vellum
// (shaders/manuscript.gdshader). It can be switched at any time: the quad shows or hides, and cut-outs already in the world
// change their edges (ManuscriptLook.SetCutOutEdges).

using System.Collections.Generic;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private MeshInstance3D? _manuscript;
    private bool? _lookArg;

    private void StartManuscript()
    {
        string[] args = OS.GetCmdlineUserArgs();
        int at = System.Array.IndexOf(args, "--look");
        if (at >= 0 && at + 1 < args.Length)
            _lookArg = args[at + 1] == "manuscript";
        _manuscript = new MeshInstance3D
        {
            Name = "Manuscript",
            // Its vertices are placed on the screen by the shader, so its box is made too big to ever be culled.
            Mesh = new QuadMesh { Size = Vector2.One, CustomAabb = new Aabb(-Vector3.One * 1e6f, Vector3.One * 2e6f) },
            MaterialOverride = ManuscriptLook.Material(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
        };
        // Leaf comes from the classic shaders' mark; Godot's own materials write real roughness, which would read as leaf.
        ((ShaderMaterial)_manuscript.MaterialOverride).SetShaderParameter("leaf_enabled", ClassicLighting.On);
        _camera.AddChild(_manuscript);
        _manuscript.Position = new Vector3(0, 0, -1);
        UpdateManuscript();
    }

    private void UpdateManuscript()
    {
        if (_manuscript is null)
            return;
        bool on = _lookArg ?? ClientSettings.ManuscriptLook;
        _manuscript.Visible = on;
        if (on == ManuscriptLook.Active && _lookSet)
            return;
        _lookSet = true;
        ManuscriptLook.Active = on;
        GD.Print($"[world] look: {(on ? "manuscript" : "classic")}");
        // Cut-outs already in the world change their edges (ManuscriptLook.SetCutOutEdges); new ones are made that way.
        var changed = new HashSet<BaseMaterial3D>();
        foreach (Node n in FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            if (n is not MeshInstance3D mi || mi == _manuscript)
                continue;
            if (ClassicLighting.Source(mi.MaterialOverride) is { } over)
            {
                if (Recut(over, changed) && mi.MaterialOverride is ShaderMaterial)
                    mi.MaterialOverride = ClassicLighting.Wrap(over);
                continue;
            }
            if (mi.Mesh is not { } mesh)
                continue;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                if (mi.GetActiveMaterial(s) is { } m && ClassicLighting.Source(m) is { } b && Recut(b, changed) && m is ShaderMaterial)
                    mi.SetSurfaceOverrideMaterial(s, ClassicLighting.Wrap(b));
        }
    }

    private bool _lookSet;

    // Changes a material's cut-out edges for the look; true when it changed (now or earlier in this pass), so a classic
    // surface takes the regenerated shader.
    private static bool Recut(BaseMaterial3D b, HashSet<BaseMaterial3D> changed)
    {
        if (changed.Contains(b))
            return true;
        if (!ManuscriptLook.SetCutOutEdges(b))
            return false;
        changed.Add(b);
        ClassicLighting.Rewrap(b);
        return true;
    }
}
