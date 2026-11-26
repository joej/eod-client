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
// - firelight: a warm flickering light among a world model's glowing (additive) systems, lighting the logs, ground and
//   tents around a fire (FireLight; zone objects and keeps only, AddFireLight);
// - glow: the glowing cards drawn brighter than white, and the environment's glow blooming only what is brighter than
//   white, so flames bloom and nothing else does.

using System.Text.Json;
using Godot;

namespace EodClient;

public static class NifParticles
{
    private const float CullDistance = 2048 * 0.025f;   // metres
    private const float SoftDistance = 0.4f;             // metres over which a Modern card fades into what it meets

    private const string FireMeta = "firelight";

    /// <summary>
    /// Gives a world model (a zone's or a keep's object) its firelight, when its particles glow (Modern particles). Not
    /// for figures, their gear or spell effects: a glowing aura or a magic weapon is no campfire.
    /// </summary>
    public static void AddFireLight(Node3D model)
    {
        if (model.HasMeta(FireMeta) && model.GetNodeOrNull("FireLight") is null)
            model.AddChild(FireLight.From(model.GetMeta(FireMeta).AsGodotDictionary()));
    }

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
                model.SetMeta(FireMeta, FireLight.Describe(fire));   // AddFireLight puts the light on a world fire
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
        // Mesh particles (NiParticleMeshModifier) draw their mesh's card: its size, and its texture and blend when the
        // shape has none of its own (Albion's fire bolts' flame cards drew as white squares without them).
        JsonElement? mesh = null;
        foreach (JsonElement m in s.GetProperty("modifiers").EnumerateArray())
            if (m.GetProperty("type").GetString() == "meshes" && F(m, "width") > 0)
                mesh = m;
        string? Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? textureName = Str(s, "texture");
        int alphaFlags = (int)F(s, "alpha_flags");
        if (string.IsNullOrEmpty(textureName) && mesh is { } mt && !string.IsNullOrEmpty(Str(mt, "texture")))
        {
            textureName = Str(mt, "texture");
            if (alphaFlags == 0)
                alphaFlags = (int)F(mt, "alpha_flags");
        }
        Texture2D? texture = string.IsNullOrEmpty(textureName) ? null
            : ModelTexturing.Load(System.IO.Path.GetFileNameWithoutExtension(textureName), textureDirs);
        if (texture is null)
            return null;   // its texture isn't in the library: nothing rather than white squares

        JsonElement em = s.GetProperty("emitter");
        float E(int i) => em[i].GetSingle();
        var emitter = new Transform3D(
            new Basis(new Vector3(E(0), E(1), E(2)), new Vector3(E(3), E(4), E(5)), new Vector3(E(6), E(7), E(8))),
            new Vector3(E(9), E(10), E(11)));

        // Some files store a negative lifetime spread (redscint_hit: -1.33): no spread.
        float life = Mathf.Max(F(s, "lifetime", 1), 0.05f), lifeRandom = Mathf.Max(F(s, "lifetime_random"), 0);
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
                case "bomb":
                {
                    // A push away from the emitter for its duration (ours, approximate: as an outward acceleration over
                    // the first part of each particle's life, decay ignored; the bomb's own point and timing aren't used).
                    float duration = Mathf.Max(F(m, "duration"), 0.05f);
                    pm.RadialAccelMin = pm.RadialAccelMax = F(m, "delta_v") / duration;
                    float until = Mathf.Clamp(duration / (life + lifeRandom), 0.02f, 1);
                    var c = new Curve();
                    c.AddPoint(new Vector2(0, 1));
                    if (until < 1)
                    {
                        c.AddPoint(new Vector2(until, 1));
                        c.AddPoint(new Vector2(Mathf.Min(until + 0.01f, 1), 0));
                    }
                    pm.RadialAccelCurve = new CurveTexture { Curve = c };
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

        // The controller size is the particle radius; a mesh particle's card is its mesh scaled by that radius (units).
        var quad = new QuadMesh
        {
            Size = mesh is { } mq ? new Vector2(F(mq, "width"), F(mq, "height")) * (size / 0.025f) : Vector2.One * size * 2,
        };
        BlendKind kind = Blend(alphaFlags);
        quad.Material = Material(kind, texture);
        p.DrawPass1 = quad;
        Timeline(p, s, life + lifeRandom);

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

    // ----- timing -----

    // The controller's emit window and its cycle (its start to stop time: loop, reverse, or clamp = play once), and the
    // visibility keys of a system an effect shows partway in. A zone model loops forever, already burning when it comes
    // into view; one whose window or keys change over its cycle is driven each frame (UpdateTimelines). An effect plays its
    // systems once from the start (PlayOnce).
    private sealed record Timing(float EmitStart, float EmitStop, float Start, float Stop, bool Once,
        (float Time, bool Visible)[] Vis, float VisStart, float VisStop, bool VisOnce)
    {
        public float Cycle => Mathf.Max(Stop - Start, 0.001f);

        public bool EmitsAt(float t) => EmitStop <= EmitStart || (t >= EmitStart && t < EmitStop);

        public bool ShownAt(float t)
        {
            if (Vis.Length == 0)
                return true;
            float span = Mathf.Max(VisStop - VisStart, 0.001f);
            float v = VisOnce ? Mathf.Min(t, VisStop) : VisStart + Mathf.PosMod(t - VisStart, span);
            bool shown = Vis[0].Visible;
            foreach ((float time, bool visible) in Vis)
                if (time <= v + 1e-4f)
                    shown = visible;
            return shown;
        }

        // Whether the system changes over its cycle at all (else it just burns).
        public bool Varies => Vis.Length > 0 && Vis.Any(k => !k.Visible)
            || EmitStop > EmitStart && (EmitStart > Start + 0.05f || EmitStop < Stop - 0.05f);
    }

    public const string TimelineGroup = "particle_timeline";

    private static bool Clamped(int flags) => ((flags >> 1) & 3) == 2;

    private static void Timeline(GpuParticles3D p, JsonElement s, float life)
    {
        (float, bool)[] vis = [];
        float vs = 0, ve = 0;
        bool visOnce = false;
        if (s.TryGetProperty("visibility", out JsonElement v))
        {
            vis = v.GetProperty("keys").EnumerateArray().Select(k => (k[0].GetSingle(), k[1].GetBoolean())).ToArray();
            (vs, ve, visOnce) = (F(v, "start"), F(v, "stop"), Clamped((int)F(v, "flags")));
        }
        var timing = new Timing(F(s, "emit_start"), F(s, "emit_stop"), F(s, "start"), F(s, "stop"),
            Clamped((int)F(s, "flags")), vis, vs, ve, visOnce);
        TimingList.Add(timing);
        p.SetMeta("timing", TimingList.Count - 1);
        if (timing.Varies || timing.Once)
            p.AddToGroup(TimelineGroup, persistent: true);
        else
            p.Preprocess = life;   // already burning when it comes into view
    }

    // Each system's timing; a node finds its own by its "timing" meta, which placed copies of a template keep.
    private static readonly System.Collections.Generic.List<Timing> TimingList = new();

    private static Timing? TimingOf(GpuParticles3D p) =>
        p.HasMeta("timing") && (int)p.GetMeta("timing") is int i && i >= 0 && i < TimingList.Count ? TimingList[i] : null;

    /// <summary>Starts every particle system under <paramref name="root"/> from its beginning (an effect, which the caller
    /// removes after its Duration): each by its own cycle (a looping system keeps emitting meanwhile, a clamped one plays
    /// its window once), shown by its visibility keys, from now, at <paramref name="rate"/> times its speed.</summary>
    public static void PlayOnce(Node root, float rate = 1)
    {
        foreach (Node n in root.FindChildren("*", "GPUParticles3D", true, false))
            if (n is GpuParticles3D p)
            {
                p.SetMeta("born", Time.GetTicksMsec() / 1000.0);
                p.SetMeta("rate", rate);
                p.Preprocess = 0;
                p.Emitting = false;
                if (!p.IsInGroup(TimelineGroup))
                    p.AddToGroup(TimelineGroup);
            }
    }

    /// <summary>Every timed system in the tree: emitting in its window, shown by its keys.</summary>
    public static void UpdateTimelines(SceneTree tree)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        foreach (Node n in tree.GetNodesInGroup(TimelineGroup))
        {
            if (n is not GpuParticles3D p || TimingOf(p) is not Timing tm)
                continue;
            if (!p.HasMeta("born"))
                p.SetMeta("born", now);
            float age = (float)(now - p.GetMeta("born").AsDouble()) * (p.HasMeta("rate") ? (float)p.GetMeta("rate") : 1f);
            bool once = tm.Once;
            float t = once ? tm.Start + age : tm.Start + Mathf.PosMod(age, tm.Cycle);
            bool emit = tm.EmitsAt(t) && (!once || t <= tm.Stop + 0.001f || tm.Stop <= tm.Start);
            if (p.Emitting != emit)
                p.Emitting = emit;
            bool shown = tm.ShownAt(t);
            if (p.Visible != shown)
                p.Visible = shown;
        }
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
