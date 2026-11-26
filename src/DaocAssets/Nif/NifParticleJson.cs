// SPDX-License-Identifier: GPL-3.0-or-later
//
// A model's particle systems as the client reads them: "<model>.particles.json" beside its .glb. Everything is already in
// the glTF / Godot frame the model's meshes are exported in (Y up, metres; GltfExporter.NifToGltfMatrix): the emitter as
// a transform whose local +Y is the NIF emitter's up axis (+Z), distances and speeds in metres, angles in radians. The
// blend stays as the NiAlphaProperty flags, so the client chooses how to draw each one.

using System.Numerics;
using System.Text.Json;

namespace DaocAssets.Nif;

public static class NifParticleJson
{
    /// <summary>The file name beside a model's .glb.</summary>
    public static string PathFor(string glbPath) => Path.ChangeExtension(glbPath, null) + ".particles.json";

    /// <summary>The systems as JSON text, in the glTF frame.</summary>
    public static string ToJson(IReadOnlyList<NifParticleSystem> systems)
    {
        Matrix4x4 a = GltfExporter.NifToGltfMatrix;
        Matrix4x4.Invert(a, out Matrix4x4 toNif);
        const float m = 0.025f;                          // NIF units to metres (as the meshes)
        float[] V(Vector3 v) => [v.X, v.Y, v.Z];
        Vector3 Dir(Vector3 v) => Vector3.TransformNormal(v, a) is var d && d.LengthSquared() > 0 ? Vector3.Normalize(d) : Vector3.Zero;

        var list = new List<Dictionary<string, object?>>();
        foreach (NifParticleSystem s in systems)
        {
            // The emitter in the glTF frame: from glTF to NIF, the emitter, back; its basis keeps the node's own
            // rotation and scale, its origin is in metres.
            Matrix4x4 e = toNif * s.Emitter * a;
            var o = new Dictionary<string, object?>
            {
                ["name"] = s.Name,
                ["emitter"] = new[] { e.M11, e.M12, e.M13, e.M21, e.M22, e.M23, e.M31, e.M32, e.M33, e.M41, e.M42, e.M43 },
                ["texture"] = s.Texture,
                ["alpha_flags"] = s.AlphaFlags,
                ["size"] = s.Size * m,
                ["speed"] = s.Speed * m,
                ["speed_random"] = s.SpeedRandom * m,
                ["declination"] = s.Declination,
                ["declination_variation"] = s.DeclinationVariation,
                ["planar_angle"] = s.PlanarAngle,
                ["planar_angle_variation"] = s.PlanarAngleVariation,
                ["rate"] = s.EmitRate,
                ["lifetime"] = s.Lifetime,
                ["lifetime_random"] = s.LifetimeRandom,
                // The start box along the emitter's own axes (NIF x, y, z are the emitter frame's x, z, -y here).
                ["box"] = V(Vector3.Abs(Vector3.TransformNormal(s.StartRandom, a))),
                ["max"] = s.MaxParticles,
                ["emit_start"] = s.EmitStart,
                ["emit_stop"] = s.EmitStop,
                ["start"] = s.StartTime,
                ["stop"] = s.StopTime,
                ["flags"] = s.ControllerFlags,
                ["emit_flags"] = s.EmitFlags,
            };
            var mods = new List<Dictionary<string, object?>>();
            foreach (NifParticleModifier mod in s.Modifiers)
                mods.Add(mod switch
                {
                    NifParticleGravity g => new()
                    {
                        ["type"] = "gravity", ["spherical"] = g.Spherical, ["force"] = g.Force * m, ["decay"] = g.Decay,
                        ["position"] = V(Vector3.Transform(g.Position, a)), ["direction"] = V(Dir(g.Direction)),
                    },
                    NifParticleColors c => new()
                    {
                        ["type"] = "colors",
                        ["keys"] = c.Keys.Select(k => new[] { k.Time, k.Color.X, k.Color.Y, k.Color.Z, k.Color.W }).ToArray(),
                    },
                    NifParticleGrowFade gf => new() { ["type"] = "growfade", ["grow"] = gf.Grow, ["fade"] = gf.Fade },
                    NifParticleSpin sp => new()
                    {
                        ["type"] = "spin", ["speed"] = sp.Speed, ["random_axis"] = sp.RandomAxis, ["axis"] = V(Dir(sp.Axis)),
                    },
                    NifParticleBomb b => new()
                    {
                        ["type"] = "bomb", ["decay"] = b.Decay, ["duration"] = b.Duration, ["delta_v"] = b.DeltaV * m,
                        ["start"] = b.Start, ["decay_type"] = b.DecayType, ["symmetry"] = b.Symmetry,
                        ["position"] = V(Vector3.Transform(b.Position, a)), ["direction"] = V(Dir(b.Direction)),
                    },
                    NifParticleMeshes pm => new() { ["type"] = "meshes", ["count"] = pm.Count },
                    _ => new() { ["type"] = mod.GetType().Name },
                });
            o["modifiers"] = mods;
            list.Add(o);
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["systems"] = list });
    }
}
