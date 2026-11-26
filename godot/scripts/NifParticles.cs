// SPDX-License-Identifier: GPL-3.0-or-later
//
// A model's particle systems (fire, smoke, mist, sparks; the converter's "<model>.particles.json", NifParticleJson) as
// Godot GPU particles, added to the loaded model so every placed copy of it burns. One GPUParticles3D per system, placed
// at its emitter, simulated in the emitter's own frame (static models don't move, so the placement's turn and scale carry
// the whole system: direction, speed, start box and particle size alike).
//
// As the client runs them (stock NetImmerse particles): born at the emitter's birth rate inside its start box, sent along
// its up axis tilted by the declination (cone: the declination variation) and turned by the planar angle, at the speed
// (± its random part), for the lifetime (± its random part); never more alive than the system's particle count. Modifiers:
// gravity (along a direction, or toward a point), colour over life, grow from nothing / shrink to nothing, spin. Each
// particle is a camera-facing card of twice the radius, unlit, drawn with the shape's alpha blend: the NiAlphaProperty's
// source and destination factors pick one of the shaders below (normal, additive, screen, multiply, inverse destination).
// The mapping of the controller's fields and of the odd blends follows the Offline DAoC / DAoC Sandbox client (GPL-3.0,
// nif_particles.gd), as do the pool cap and the degrees in NiParticleRotation.
//
// Ours (not traced): unlit cards; the bomb modifier and mesh particles are not drawn; play-once (clamped) systems loop
// like the rest; systems end at 2048 units from the camera (the client's effect cull distance, RE/82).
//
// Settings → Graphics → Modern particles (off, Classic, by default; retail has none of these):
// - soft particles: each card fades out where it meets the ground or a wall instead of cutting a hard line through it;
// - firelight: a warm flickering light among a model's glowing (additive) systems, lighting the logs, ground and tents
//   around a fire (FireLight);
// - glow: the glowing cards drawn brighter than white, and the environment's glow blooming only what is brighter than
//   white, so flames bloom and nothing else does.

using System.Text.Json;
using Godot;

namespace EodClient;

public static class NifParticles
{
    private const float CullDistance = 2048 * 0.025f;   // metres
    private const float SoftDistance = 0.4f;             // metres over which a Modern card fades into what it meets

    /// <summary>
    /// Adds the particle systems of the model loaded from <paramref name="glbPath"/>, if it has any, as children of
    /// <paramref name="model"/> (the glTF scene root). Returns how many were added.
    /// </summary>
    public static int Attach(Node3D model, string glbPath, string[] textureDirs)
    {
        string path = DaocAssets.Nif.NifParticleJson.PathFor(glbPath);
        if (!Godot.FileAccess.FileExists(path))
            return 0;
        int added = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(path));
            var fire = new System.Collections.Generic.List<(Vector3 At, Color Colour, float Reach)>();
            foreach (JsonElement s in doc.RootElement.GetProperty("systems").EnumerateArray())
                if (Build(s, textureDirs, fire) is GpuParticles3D p)
                {
                    model.AddChild(p);
                    added++;
                }
            if (fire.Count > 0)
                model.AddChild(FireLight.For(fire));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            GD.PushWarning($"particles: {path}: {e.Message}");
        }
        return added;
    }

    private static float F(JsonElement e, string key, float fallback = 0) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    private static Vector3 V(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.GetArrayLength() >= 3
            ? new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle()) : Vector3.Zero;

    private static GpuParticles3D? Build(JsonElement s, string[] textureDirs,
        System.Collections.Generic.List<(Vector3 At, Color Colour, float Reach)> fire)
    {
        string? textureName = s.TryGetProperty("texture", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        Texture2D? texture = string.IsNullOrEmpty(textureName) ? null
            : ModelTexturing.Load(System.IO.Path.GetFileNameWithoutExtension(textureName), textureDirs);
        if (texture is null)
            return null;   // its texture isn't in the library: nothing rather than white squares

        JsonElement em = s.GetProperty("emitter");
        float E(int i) => em[i].GetSingle();
        var emitter = new Transform3D(
            new Basis(new Vector3(E(0), E(1), E(2)), new Vector3(E(3), E(4), E(5)), new Vector3(E(6), E(7), E(8))),
            new Vector3(E(9), E(10), E(11)));

        float life = Mathf.Max(F(s, "lifetime", 1), 0.05f), lifeRandom = F(s, "lifetime_random");
        float rate = F(s, "rate"), speed = F(s, "speed"), speedRandom = F(s, "speed_random");
        int max = (int)F(s, "max");
        int byRate = Mathf.CeilToInt(rate * (life + lifeRandom));
        var p = new GpuParticles3D
        {
            Name = "particles_" + System.IO.Path.GetFileNameWithoutExtension(textureName),
            Transform = emitter,
            LocalCoords = true,
            Lifetime = life + lifeRandom,
            // The system's particle count is its pool: never more alive than that, however high the rate.
            Amount = Mathf.Clamp(max > 0 ? Mathf.Min(max, byRate) : byRate, 1, 512),
            Preprocess = life + lifeRandom,   // already burning when it comes into view
            VisibilityRangeEnd = CullDistance,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        var pm = new ParticleProcessMaterial
        {
            // Along the emitter's up axis tilted by the declination, turned by the planar angle; the variation is the cone.
            Direction = Vector3.Up.Rotated(Vector3.Right, F(s, "declination")).Rotated(Vector3.Up, F(s, "planar_angle")),
            Spread = Mathf.RadToDeg(F(s, "declination_variation")),
            InitialVelocityMin = Mathf.Max(speed - speedRandom, 0),
            InitialVelocityMax = speed + speedRandom,
            LifetimeRandomness = Mathf.Clamp(lifeRandom / (life + lifeRandom), 0, 1),
            Gravity = Vector3.Zero,
        };
        Vector3 box = V(s, "box");
        if (box.X + box.Y + box.Z > 0)
        {
            pm.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
            pm.EmissionBoxExtents = box * 0.5f;
        }

        Basis toLocal = emitter.Basis.Inverse();
        Color ramp = Colors.Black;   // the colour over life, averaged (weighted by alpha): a firelight's colour
        foreach (JsonElement m in s.GetProperty("modifiers").EnumerateArray())
            switch (m.GetProperty("type").GetString())
            {
                case "gravity":
                {
                    // In the model's frame; the particles run in the emitter's. A spherical field pulls toward its point
                    // as seen from the emitter (ours: one direction for the whole system).
                    Vector3 dir = m.GetProperty("spherical").GetBoolean()
                        ? (V(m, "position") - emitter.Origin).Normalized() : V(m, "direction");
                    pm.Gravity += toLocal * dir * F(m, "force");
                    break;
                }
                case "colors":
                {
                    var offsets = new System.Collections.Generic.List<float>();
                    var colours = new System.Collections.Generic.List<Color>();
                    foreach (JsonElement k in m.GetProperty("keys").EnumerateArray())
                    {
                        offsets.Add(Mathf.Clamp(k[0].GetSingle(), 0, 1));
                        colours.Add(new Color(k[1].GetSingle(), k[2].GetSingle(), k[3].GetSingle(), k[4].GetSingle()));
                    }
                    if (offsets.Count > 0)
                        pm.ColorRamp = new GradientTexture1D { Gradient = new Gradient { Offsets = [.. offsets], Colors = [.. colours] } };
                    foreach (Color k in colours)
                        ramp += new Color(k.R, k.G, k.B) * k.A / colours.Count;
                    break;
                }
                case "growfade":
                {
                    float grow = Mathf.Clamp(F(m, "grow") / life, 0, 1), fade = Mathf.Clamp(F(m, "fade") / life, 0, 1);
                    var c = new Curve();
                    c.AddPoint(new Vector2(0, grow > 0 ? 0 : 1));
                    if (grow > 0 && grow < 1)
                        c.AddPoint(new Vector2(grow, 1));
                    if (fade > 0 && 1 - fade > grow)
                        c.AddPoint(new Vector2(1 - fade, 1));
                    c.AddPoint(new Vector2(1, fade > 0 ? 0 : 1));
                    pm.ScaleCurve = new CurveTexture { Curve = c };
                    break;
                }
                case "spin":
                {
                    // Radians per second; some DAoC models write degrees (333.3: about a turn a second, as 6.28 is).
                    float spin = F(m, "speed");
                    if (Mathf.Abs(spin) > 4 * Mathf.Tau)
                        spin = Mathf.DegToRad(spin);
                    pm.AngularVelocityMin = pm.AngularVelocityMax = Mathf.RadToDeg(spin);
                    break;
                }
            }
        p.ProcessMaterial = pm;

        // The space the system can fill: what it may travel in its life, so it isn't culled while visible.
        float size = F(s, "size", 1);
        float reach = Mathf.Max((speed + speedRandom) * (life + lifeRandom) + box.Length() + size * 2
            + pm.Gravity.Length() * (life + lifeRandom) * (life + lifeRandom) * 0.5f, 1);
        p.VisibilityAabb = new Aabb(-Vector3.One * reach, Vector3.One * reach * 2);

        var quad = new QuadMesh { Size = Vector2.One * size * 2 };   // the controller size is the particle radius
        BlendKind kind = Blend((int)F(s, "alpha_flags"));
        quad.Material = Material(kind, texture);
        p.DrawPass1 = quad;

        // A glowing system (light added) of real size gives the model a firelight: its texture's colour times the ramp's.
        if (kind is BlendKind.Add or BlendKind.AddAll && rate > 0 && size * 2 >= 0.05f)
        {
            Color tex = AverageColour(texture);
            Color c = ramp == Colors.Black ? tex : new Color(tex.R * ramp.R, tex.G * ramp.G, tex.B * ramp.B);
            float bright = Mathf.Max(c.R, Mathf.Max(c.G, c.B));
            if (bright > 0.05f)
                fire.Add((emitter.Origin + emitter.Basis.Y.Normalized() * size, c / bright,
                    box.Length() + size * 2 + speed * life));
        }
        return p;
    }

    // ----- drawing -----

    public enum BlendKind { Mix, Add, AddAll, Screen, Multiply, InvDest }

    /// <summary>The draw for NiAlphaProperty flags (bit 0 blend; bits 1-4 source, 5-8 destination factor: 0 ONE, 1 ZERO,
    /// 2 SRCCOLOR, 3 INVSRCCOLOR, 4 DESTCOLOR, 5 INVDESTCOLOR, 6 SRCALPHA, 7 INVSRCALPHA, 8 DESTALPHA, 9 INVDESTALPHA).</summary>
    public static BlendKind Blend(int flags)
    {
        if ((flags & 1) == 0)
            return BlendKind.Mix;
        int src = (flags >> 1) & 15, dst = (flags >> 5) & 15;
        if (src == 5)
            return dst == 7 ? BlendKind.InvDest : BlendKind.AddAll;   // the colour in full; with INVSRCALPHA it dims behind
        if (src == 4)
            return BlendKind.Multiply;                               // the scene behind times the particle: a tint
        if (dst is 0 or 8)
            return src == 0 ? BlendKind.AddAll : BlendKind.Add;      // light added (ONE, or DESTALPHA: 1 in the frame)
        return dst == 3 ? BlendKind.Screen : BlendKind.Mix;
    }

    // Modern: glowing cards this much brighter than their texture, and bloom from what is brighter than white.
    private const float GlowBoost = 2.5f;
    private static Godot.Environment? _environment;

    /// <summary>The world's environment, whose glow Modern particles switch on.</summary>
    public static void UseEnvironment(Godot.Environment environment)
    {
        _environment = environment;
        ApplyGlow();
    }

    private static void ApplyGlow()
    {
        if (_environment is not { } e || !GodotObject.IsInstanceValid(e))
            return;
        e.GlowEnabled = Modern;
        e.GlowHdrThreshold = 1.0f;     // only what is brighter than white: the boosted flames
        e.GlowIntensity = 0.9f;
        e.GlowBloom = 0f;
        e.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
    }

    /// <summary>Whether Modern particles are on (the setting, or --modern-particles).</summary>
    public static bool ModernOn => Modern;

    // A texture's average colour (its visible part), from a small copy.
    private static readonly System.Collections.Generic.Dictionary<Texture2D, Color> Averages = new();
    private static Color AverageColour(Texture2D texture)
    {
        if (Averages.TryGetValue(texture, out Color c))
            return c;
        Image? img = texture.GetImage();
        if (img is null)
            return Averages[texture] = Colors.White;
        img = (Image)img.Duplicate();
        if (img.IsCompressed())
            img.Decompress();
        img.Resize(16, 16, Image.Interpolation.Bilinear);
        Color sum = Colors.Black;
        float weight = 0;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Color p = img.GetPixel(x, y);
                float w = p.A * Mathf.Max(p.R, Mathf.Max(p.G, p.B));
                sum += new Color(p.R, p.G, p.B) * w;
                weight += w;
            }
        return Averages[texture] = weight > 0 ? new Color(sum.R / weight, sum.G / weight, sum.B / weight) : Colors.White;
    }

    private static readonly System.Collections.Generic.Dictionary<(BlendKind, bool), Shader> Shaders = new();
    private static readonly System.Collections.Generic.List<(ShaderMaterial Material, BlendKind Kind)> Materials = new();

    /// <summary>--modern-particles (shots): Modern whatever the saved setting says.</summary>
    public static bool ForceModern;

    private static bool Modern => ForceModern || ClientSettings.ModernParticles;

    private static ShaderMaterial Material(BlendKind kind, Texture2D texture)
    {
        var m = new ShaderMaterial { Shader = ShaderFor(kind, Modern) };
        m.SetShaderParameter("tex", texture);
        m.SetShaderParameter("soft_distance", SoftDistance);
        Materials.RemoveAll(x => !GodotObject.IsInstanceValid(x.Material));
        Materials.Add((m, kind));
        return m;
    }

    /// <summary>Switches every particle drawn so far between Classic and Modern (soft particles).</summary>
    public static void ApplySetting()
    {
        bool modern = Modern;
        foreach ((ShaderMaterial m, BlendKind kind) in Materials)
            if (GodotObject.IsInstanceValid(m))
                m.Shader = ShaderFor(kind, modern);
        ApplyGlow();   // firelights follow the setting themselves (FireLight)
    }

    private static Shader ShaderFor(BlendKind kind, bool soft)
    {
        if (Shaders.TryGetValue((kind, soft), out Shader? s))
            return s;
        string blend = kind switch
        {
            BlendKind.Add or BlendKind.AddAll => "blend_add",
            BlendKind.Multiply => "blend_mul",
            BlendKind.InvDest => "blend_premul_alpha",
            _ => "blend_mix",
        };
        string body = kind switch
        {
            // Screen (INVSRCCOLOR): black adds nothing; the brightest channel is the coverage.
            BlendKind.Screen => "vec3 c = t.rgb * COLOR.rgb; float m = max(max(c.r, c.g), max(c.b, 0.001)); ALBEDO = c / m; ALPHA = m * t.a * COLOR.a;",
            // Multiply: what is behind times (the colour + the part its alpha lets through).
            BlendKind.Multiply => "float a = t.a * COLOR.a; ALBEDO = clamp(t.rgb * COLOR.rgb + vec3(1.0 - a), 0.0, 1.0); ALPHA = 1.0;",
            // ONE / ONE and INVDESTCOLOR / ONE: the colour added in full, its alpha takes no part.
            BlendKind.AddAll => "ALBEDO = t.rgb * COLOR.rgb; ALPHA = 1.0;",
            _ => "ALBEDO = t.rgb * COLOR.rgb; ALPHA = t.a * COLOR.a;",
        };
        // Soft particles (Modern): fade the card where it nears what is behind it. A multiply card fades toward white
        // (no tint), the others toward nothing.
        string softUniform = soft ? "uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;\n" : "";
        // Modern: glowing cards brighter than white, for the environment's glow to bloom.
        string boost = soft && kind is BlendKind.Add or BlendKind.AddAll
            ? $"\n\tALBEDO *= {GlowBoost.ToString(System.Globalization.CultureInfo.InvariantCulture)};" : "";
        string softCode = !soft ? "" : kind == BlendKind.Multiply
            ? "\n\tALBEDO = mix(vec3(1.0), ALBEDO, Soft(SCREEN_UV, VERTEX, INV_PROJECTION_MATRIX));"
            : kind == BlendKind.AddAll
                ? "\n\tALBEDO *= Soft(SCREEN_UV, VERTEX, INV_PROJECTION_MATRIX);"
                : "\n\tALPHA *= Soft(SCREEN_UV, VERTEX, INV_PROJECTION_MATRIX);";
        string softFunc = !soft ? "" : """
            float Soft(vec2 uv, vec3 vertex, mat4 inv_projection) {
            	float depth = textureLod(depth_tex, uv, 0.0).r;
            	vec4 behind = inv_projection * vec4(uv * 2.0 - 1.0, depth, 1.0);
            	behind.xyz /= behind.w;
            	return clamp((vertex.z - behind.z) / soft_distance, 0.0, 1.0);
            }

            """;
        s = new Shader
        {
            Code = $$"""
                shader_type spatial;
                render_mode {{blend}}, depth_draw_never, cull_disabled, unshaded;
                uniform sampler2D tex : source_color, filter_linear_mipmap, repeat_disable;
                uniform float soft_distance = 0.4;
                {{softUniform}}
                void vertex() {
                	// A camera-facing card turned by the particle's spin and sized by its scale (Godot's particle billboard).
                	mat4 w = mat4(normalize(INV_VIEW_MATRIX[0]), normalize(INV_VIEW_MATRIX[1]), normalize(INV_VIEW_MATRIX[2]), MODEL_MATRIX[3]);
                	float a = INSTANCE_CUSTOM.x;
                	w = w * mat4(vec4(cos(a), -sin(a), 0.0, 0.0), vec4(sin(a), cos(a), 0.0, 0.0), vec4(0.0, 0.0, 1.0, 0.0), vec4(0.0, 0.0, 0.0, 1.0));
                	MODELVIEW_MATRIX = VIEW_MATRIX * w;
                	MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
                		vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0), vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0), vec4(0.0, 0.0, 0.0, 1.0));
                	MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
                }

                {{softFunc}}void fragment() {
                	vec4 t = texture(tex, UV);
                	{{body}}{{softCode}}{{boost}}
                }
                """,
        };
        Shaders[(kind, soft)] = s;
        return s;
    }
}
