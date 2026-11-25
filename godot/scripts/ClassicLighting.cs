// SPDX-License-Identifier: GPL-3.0-or-later
//
// Classic lighting (Settings → Lighting, on by default): the world lit as the retail client lights it, per vertex in
// display space with no cast shadows (godot/shaders/classic_light.gdshaderinc). Modern lighting is Godot's own.
//
// Godot's standard materials can't take a custom light function, so with Classic on every lit standard material in
// the world is drawn by a generated shader that does what the standard material did (albedo, vertex colour, detail
// layer, emission, alpha modes, culling) plus the classic light. The standard material stays the record: the generated
// one carries it (Source), so code that reads or changes a surface's material keeps working on the standard material
// and sets the result through Wrap. Not converted: unshaded, billboard, no-depth-test and non-mix-blend materials
// (markers, effects), and shader materials, which include the light themselves (ItemPaint, the Atlantis ground).

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace EodClient;

public static class ClassicLighting
{
    private const string IncludePath = "res://shaders/classic_light.gdshaderinc";
    private const string SourceMeta = "eod_classic_source";

    /// <summary>Classic lighting is on for the world being built (set by WorldView from the setting).</summary>
    public static bool On { get; set; }

    /// <summary>The lines a hand-written spatial shader starts its body with (after shader_type and render_mode).</summary>
    public static string Header => (On ? "#define CLASSIC_LIGHT\n" : "") + $"#include \"{IncludePath}\"\n";

    private static readonly ConditionalWeakTable<BaseMaterial3D, ShaderMaterial> Wrapped = new();
    private static readonly Dictionary<string, Shader> Shaders = [];

    /// <summary>The standard material behind a surface's material: itself, or the one a generated material draws.</summary>
    public static BaseMaterial3D? Source(Material? m) =>
        m as BaseMaterial3D ?? (m is ShaderMaterial s && s.HasMeta(SourceMeta) ? s.GetMeta(SourceMeta).As<BaseMaterial3D>() : null);

    /// <summary>What to draw a material with: with Classic on, a lit standard material's classic version; else itself.</summary>
    public static Material? Wrap(Material? m)
    {
        if (!On || m is not BaseMaterial3D b || !Convertible(b))
            return m;
        if (Wrapped.TryGetValue(b, out ShaderMaterial? done))
            return done;
        ShaderMaterial w = Build(b);
        Wrapped.AddOrUpdate(b, w);
        return w;
    }

    /// <summary><see cref="Wrap"/> again after the standard material was changed (the generated one copies its values).</summary>
    public static Material? Rewrap(BaseMaterial3D b)
    {
        Wrapped.Remove(b);
        return Wrap(b);
    }

    /// <summary>Draws every lit standard material of a mesh (its own or overridden) with the classic light.</summary>
    public static void Convert(MeshInstance3D mi)
    {
        if (!On || !GodotObject.IsInstanceValid(mi))
            return;
        if (mi.MaterialOverride is BaseMaterial3D over)
        {
            mi.MaterialOverride = Wrap(over);
            return;
        }
        if (mi.Mesh is not { } mesh)
            return;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            if (mi.GetActiveMaterial(s) is BaseMaterial3D b && Wrap(b) is ShaderMaterial w)
                mi.SetSurfaceOverrideMaterial(s, w);
    }

    private static bool Convertible(BaseMaterial3D b) =>
        b.ShadingMode != BaseMaterial3D.ShadingModeEnum.Unshaded
        && b.BillboardMode == BaseMaterial3D.BillboardModeEnum.Disabled
        && !b.NoDepthTest && !b.FixedSize
        && b.BlendMode == BaseMaterial3D.BlendModeEnum.Mix;

    private static ShaderMaterial Build(BaseMaterial3D b)
    {
        string cull = b.CullMode switch
        {
            BaseMaterial3D.CullModeEnum.Disabled => "cull_disabled",
            BaseMaterial3D.CullModeEnum.Front => "cull_front",
            _ => "cull_back",
        };
        string alpha = b.Transparency switch
        {
            BaseMaterial3D.TransparencyEnum.Alpha => "blend",
            BaseMaterial3D.TransparencyEnum.AlphaDepthPrePass => "prepass",
            BaseMaterial3D.TransparencyEnum.AlphaScissor or BaseMaterial3D.TransparencyEnum.AlphaHash => "scissor",
            _ => "opaque",
        };
        bool coverage = alpha == "scissor" && b.AlphaAntialiasingMode != BaseMaterial3D.AlphaAntiAliasing.Off;
        string filter = b.TextureFilter switch
        {
            BaseMaterial3D.TextureFilterEnum.Nearest => "filter_nearest",
            BaseMaterial3D.TextureFilterEnum.Linear => "filter_linear",
            BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps => "filter_nearest_mipmap",
            BaseMaterial3D.TextureFilterEnum.NearestWithMipmapsAnisotropic => "filter_nearest_mipmap_anisotropic",
            BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic => "filter_linear_mipmap_anisotropic",
            _ => "filter_linear_mipmap",
        };
        string repeat = b.TextureRepeat ? "repeat_enable" : "repeat_disable";
        string key = $"{cull} {alpha} {coverage} {filter} {repeat}";
        if (!Shaders.TryGetValue(key, out Shader? shader))
            Shaders[key] = shader = new Shader { Code = Code(cull, alpha, coverage, filter, repeat) };

        var m = new ShaderMaterial { Shader = shader, ResourceName = b.ResourceName };
        m.SetMeta(SourceMeta, b);
        m.SetShaderParameter("albedo_color", b.AlbedoColor);
        m.SetShaderParameter("has_albedo_tex", b.AlbedoTexture is not null);
        if (b.AlbedoTexture is { } tex)
            m.SetShaderParameter("albedo_tex", tex);
        m.SetShaderParameter("uv1_scale", b.Uv1Scale);
        m.SetShaderParameter("uv1_offset", b.Uv1Offset);
        m.SetShaderParameter("uv2_scale", b.UV2Triplanar ? Vector3.One : b.UV2Scale);
        m.SetShaderParameter("uv2_offset", b.UV2Triplanar ? Vector3.Zero : b.UV2Offset);
        m.SetShaderParameter("vertex_colour", b.VertexColorUseAsAlbedo);
        bool detail = b.DetailEnabled && b.DetailAlbedo is not null;
        m.SetShaderParameter("detail_mode", !detail ? 0 : b.DetailBlendMode switch
        {
            BaseMaterial3D.BlendModeEnum.Mul => 1,
            BaseMaterial3D.BlendModeEnum.Add => 3,
            BaseMaterial3D.BlendModeEnum.Sub => 4,
            _ => 2,
        });
        if (detail && b.DetailAlbedo is { } detailTex)
            m.SetShaderParameter("detail_tex", detailTex);
        m.SetShaderParameter("detail_uv2", b.DetailUVLayer == BaseMaterial3D.DetailUV.UV2);
        // A glow that is the texture times a colour (the dungeon fix-up, a keep's glow level) is retail's material
        // emissive: it goes into the lit colour, as fixed-function lighting adds it, instead of being added on top.
        // Every other material has retail's outdoor 1/16 (daoc-re RE/100 §8; whether the files or the client
        // set it is open).
        bool litGlow = b.EmissionEnabled && b.EmissionOperator == BaseMaterial3D.EmissionOperatorEnum.Multiply
            && b.EmissionTexture is not null && b.EmissionTexture == b.AlbedoTexture;
        m.SetShaderParameter("lit_emissive", litGlow
            ? new Vector3(b.Emission.R, b.Emission.G, b.Emission.B) * b.EmissionEnergyMultiplier : new Vector3(0.0625f, 0.0625f, 0.0625f));
        bool glow = b.EmissionEnabled && !litGlow;
        m.SetShaderParameter("emission", glow ? b.Emission : Colors.Black);
        m.SetShaderParameter("emission_energy", glow ? b.EmissionEnergyMultiplier : 0f);
        m.SetShaderParameter("emission_op", !glow || b.EmissionTexture is null ? 0
            : b.EmissionOperator == BaseMaterial3D.EmissionOperatorEnum.Multiply ? 1 : 2);
        if (glow && b.EmissionTexture is { } glowTex)
            m.SetShaderParameter("emission_tex", glowTex);
        m.SetShaderParameter("scissor", b.AlphaScissorThreshold);
        m.SetShaderParameter("coverage_edge", b.AlphaAntialiasingEdge);
        m.RenderPriority = b.RenderPriority;
        return m;
    }

    // What StandardMaterial3D draws for the features our materials use (Godot's generated shader, rewritten), lit the
    // classic way.
    private static string Code(string cull, string alpha, bool coverage, string filter, string repeat)
    {
        string modes = alpha switch
        {
            "blend" => "blend_mix, depth_draw_opaque",
            "prepass" => "blend_mix, depth_prepass_alpha",
            _ => "blend_mix, depth_draw_opaque",
        };
        string alphaOut = alpha switch
        {
            "opaque" => "",
            "scissor" => "ALPHA = a.a; ALPHA_SCISSOR_THRESHOLD = scissor;" + (coverage
                ? " ALPHA_ANTIALIASING_EDGE = coverage_edge; ALPHA_TEXTURE_COORDINATE = base_uv * vec2(textureSize(albedo_tex, 0));"
                : ""),
            _ => "ALPHA = a.a;",
        };
        return $$"""
            shader_type spatial;
            render_mode {{modes}}, {{cull}}, diffuse_lambert;
            #define CLASSIC_LIGHT
            #include "{{IncludePath}}"
            uniform vec4 albedo_color : source_color = vec4(1.0);
            uniform sampler2D albedo_tex : source_color, {{filter}}, {{repeat}};
            uniform bool has_albedo_tex = false;
            uniform vec3 uv1_scale = vec3(1.0);
            uniform vec3 uv1_offset = vec3(0.0);
            uniform vec3 uv2_scale = vec3(1.0);
            uniform vec3 uv2_offset = vec3(0.0);
            uniform bool vertex_colour = false;
            uniform int detail_mode = 0;
            uniform sampler2D detail_tex : source_color, {{filter}}, repeat_enable;
            uniform bool detail_uv2 = false;
            uniform vec4 emission : source_color = vec4(0.0, 0.0, 0.0, 1.0);
            uniform float emission_energy = 0.0;
            uniform int emission_op = 0;
            uniform sampler2D emission_tex : source_color, {{filter}}, {{repeat}};
            uniform float scissor = 0.5;
            uniform float coverage_edge = 0.3;
            uniform vec3 lit_emissive = vec3(0.0625);
            void vertex() {
                UV = UV * uv1_scale.xy + uv1_offset.xy;
                UV2 = UV2 * uv2_scale.xy + uv2_offset.xy;
                // Retail's vertex colours (raw, display space) stand in for the material's ambient and diffuse colour.
                CLASSIC_VERTEX(vertex_colour ? COLOR.rgb : vec3(1.0), lit_emissive)
            }
            void fragment() {
                vec2 base_uv = UV;
                vec4 a = albedo_color;
                if (has_albedo_tex)
                    a *= texture(albedo_tex, base_uv);
                // The colour is in the light (vertex()); its alpha still multiplies.
                if (vertex_colour)
                    a.a *= COLOR.a;
                if (detail_mode != 0) {
                    vec4 d = texture(detail_tex, detail_uv2 ? UV2 : base_uv);
                    vec3 target = detail_mode == 1 ? a.rgb * d.rgb : detail_mode == 3 ? a.rgb + d.rgb
                        : detail_mode == 4 ? a.rgb - d.rgb : d.rgb;
                    a.rgb = mix(a.rgb, target, d.a);
                }
                ALBEDO = a.rgb;
                vec3 glow = emission.rgb;
                if (emission_op == 1)
                    glow *= texture(emission_tex, base_uv).rgb;
                else if (emission_op == 2)
                    glow += texture(emission_tex, base_uv).rgb;
                EMISSION = glow * emission_energy;
                {{alphaOut}}
                CLASSIC_FRAGMENT
            }
            """;
    }
}
