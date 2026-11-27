// SPDX-License-Identifier: GPL-3.0-or-later
//
// A model's material animations (the converter's "<model>.anim.json", NifMaterialAnimJson): a flash fading out, a glow
// changing colour, a waterfall's texture flowing, a part blinking in and out. Each track drives one mesh (by its glTF node
// name): its material's alpha (albedo alpha), a colour (diffuse: albedo; self-illumination: emission), its texture's
// offset or tiling (uv1), or whether it is drawn. Keys are sampled linearly over the controller's cycle: looping, back
// and forth, or clamped (played once, then held).
// Scenery runs on one clock for every copy (they share their materials); an effect starts its own clock when it is shown
// (Restart, with its rate) and gets its own copies of the materials it animates.
// Ours: ambient and specular colour tracks and texture rotation aren't drawn; an opaque material's alpha has no effect.

using System.Text.Json;
using Godot;

namespace EodClient;

public static class MaterialAnims
{
    public const string Group = "material_anim";

    private sealed record Track(string Node, string Kind, int Target, float[] Times, float[][] Values, int Cycle, float Start, float Stop)
    {
        public float[] At(float t)
        {
            float span = Mathf.Max(Stop - Start, 0.001f);
            float local = Cycle switch
            {
                2 => Mathf.Clamp(t + Start, Start, Stop),                                         // clamp: once
                1 => Start + span - Mathf.Abs(Mathf.PosMod(t, 2 * span) - span),                 // reverse: back and forth
                _ => Start + Mathf.PosMod(t, span),                                               // loop
            };
            if (Times.Length == 1 || local <= Times[0])
                return Values[0];
            for (int i = 1; i < Times.Length; i++)
                if (local <= Times[i])
                {
                    float u = (local - Times[i - 1]) / Mathf.Max(Times[i] - Times[i - 1], 1e-5f);
                    var v = new float[Values[i].Length];
                    for (int k = 0; k < v.Length; k++)
                        v[k] = Mathf.Lerp(Values[i - 1][k], Values[i][k], u);
                    return v;
                }
            return Values[^1];
        }
    }

    // Each model's tracks; a node finds its own by its "material_anims" meta (copies of a template keep it).
    private static readonly List<Track[]> Models = [];

    // Each placed model's meshes, per track, found once.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Node3D, MeshInstance3D?[]> Meshes = new();

    /// <summary>Reads the model's material animations, when it has any, and puts it in the group the world view updates.</summary>
    public static void Attach(Node3D model, string glbPath)
    {
        string path = DaocAssets.Nif.NifMaterialAnimJson.PathFor(glbPath);
        if (!Godot.FileAccess.FileExists(path))
            return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(path));
            var tracks = new List<Track>();
            foreach (JsonElement t in doc.RootElement.GetProperty("tracks").EnumerateArray())
            {
                float[][] keys = t.GetProperty("keys").EnumerateArray().Select(k => k.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray();
                if (keys.Length == 0)
                    continue;
                tracks.Add(new Track(t.GetProperty("node").GetString() ?? "", t.GetProperty("kind").GetString() ?? "",
                    t.GetProperty("target").GetInt32(), keys.Select(k => k[0]).ToArray(), keys.Select(k => k[1..]).ToArray(),
                    (t.GetProperty("flags").GetInt32() >> 1) & 3, t.GetProperty("start").GetSingle(), t.GetProperty("stop").GetSingle()));
            }
            if (tracks.Count == 0)
                return;
            Models.Add([.. tracks]);
            model.SetMeta("material_anims", Models.Count - 1);
            model.AddToGroup(Group, persistent: true);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            GD.PushWarning($"material animations: {path}: {e.Message}");
        }
    }

    /// <summary>Starts a model's material animations from their beginning (an effect being shown), at <paramref name="rate"/>,
    /// on materials of its own.</summary>
    public static void Restart(Node3D model, float rate = 1)
    {
        if (!model.HasMeta("material_anims"))
            return;
        model.SetMeta("anim_born", Time.GetTicksMsec() / 1000.0);
        model.SetMeta("anim_rate", rate);
        Track[] tracks = Models[(int)model.GetMeta("material_anims")];
        foreach (string name in tracks.Select(t => t.Node).Distinct())
            if (model.FindChild(name, true, false) is MeshInstance3D mi && mi.Mesh is { } mesh)
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                    if (mi.GetActiveMaterial(s)?.Duplicate() is Material own)
                        mi.SetSurfaceOverrideMaterial(s, own);
    }

    /// <summary>Every animated model in the tree, at its time.</summary>
    public static void Update(SceneTree tree)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        foreach (Node n in tree.GetNodesInGroup(Group))
        {
            if (n is not Node3D model || !model.HasMeta("material_anims") || !model.IsVisibleInTree())
                continue;
            int i = (int)model.GetMeta("material_anims");
            if (i < 0 || i >= Models.Count)
                continue;
            float t = model.HasMeta("anim_born")
                ? (float)(now - model.GetMeta("anim_born").AsDouble()) * (float)model.GetMeta("anim_rate").AsDouble()
                : (float)now;
            Track[] tracks = Models[i];
            if (!Meshes.TryGetValue(model, out MeshInstance3D?[]? meshes))
            {
                meshes = tracks.Select(tr => model.FindChild(tr.Node, true, false) as MeshInstance3D).ToArray();
                Meshes.Add(model, meshes);
            }
            for (int k = 0; k < tracks.Length; k++)
                if (meshes[k] is { } mi && GodotObject.IsInstanceValid(mi))
                    Apply(mi, tracks[k], tracks[k].At(t));
        }
    }

    private static void Apply(MeshInstance3D mi, Track track, float[] v)
    {
        if (track.Kind == "vis")
        {
            mi.Visible = v[0] >= 0.5f;
            return;
        }
        if (mi.Mesh is not { } mesh)
            return;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            switch (mi.GetActiveMaterial(s))
            {
                case ShaderMaterial m: ApplyShader(m, track, v); break;
                case BaseMaterial3D m: ApplyStandard(m, track, v); break;
            }
    }

    private static void ApplyShader(ShaderMaterial m, Track track, float[] v)
    {
        switch (track.Kind)
        {
            case "alpha":
            {
                Color c = m.GetShaderParameter("albedo_color").AsColor();
                m.SetShaderParameter("albedo_color", c with { A = Mathf.Clamp(v[0], 0, 1) });
                break;
            }
            case "color" when track.Target == 1 && v.Length >= 3:
            {
                Color c = m.GetShaderParameter("albedo_color").AsColor();
                m.SetShaderParameter("albedo_color", new Color(v[0], v[1], v[2], c.A));
                break;
            }
            case "color" when track.Target == 3 && v.Length >= 3:
                m.SetShaderParameter("emission", new Color(v[0], v[1], v[2]));
                break;
            case "uv" or "texture":
            {
                (int axis, bool scale) = Uv(track);
                if (axis < 0)
                    break;
                string key = scale ? "uv1_scale" : "uv1_offset";
                Vector3 u = m.GetShaderParameter(key).AsVector3();
                u[axis] = v[0];
                m.SetShaderParameter(key, u);
                break;
            }
        }
    }

    private static void ApplyStandard(BaseMaterial3D m, Track track, float[] v)
    {
        switch (track.Kind)
        {
            case "alpha":
                m.AlbedoColor = m.AlbedoColor with { A = Mathf.Clamp(v[0], 0, 1) };
                break;
            case "color" when track.Target == 1 && v.Length >= 3:
                m.AlbedoColor = new Color(v[0], v[1], v[2], m.AlbedoColor.A);
                break;
            case "color" when track.Target == 3 && v.Length >= 3:
                m.EmissionEnabled = true;
                m.Emission = new Color(v[0], v[1], v[2]);
                break;
            case "uv" or "texture":
            {
                (int axis, bool scale) = Uv(track);
                if (axis < 0)
                    break;
                Vector3 u = scale ? m.Uv1Scale : m.Uv1Offset;
                u[axis] = v[0];
                if (scale) m.Uv1Scale = u; else m.Uv1Offset = u;
                break;
            }
        }
    }

    // uv: key groups 0 U offset, 1 V offset, 2 U tiling, 3 V tiling; texture: operations 0 U offset, 1 V offset, 2 rotation
    // (not drawn), 3 U tiling, 4 V tiling.
    private static (int Axis, bool Scale) Uv(Track track) => track.Kind == "uv"
        ? (track.Target % 2, track.Target >= 2)
        : track.Target switch { 0 => (0, false), 1 => (1, false), 3 => (0, true), 4 => (1, true), _ => (-1, false) };
}
