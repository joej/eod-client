// SPDX-License-Identifier: GPL-3.0-or-later
//
// Dyes and guild emblems on worn items (roadmap M7b), from models/colours.json (mpk figures: the install's
// tints.csv, patterns.csv and insignia.csv).
// - A dye multiplies the item's texture by its Catacombs colour (tints.csv).
// - An emblem: the client draws the item's surface x a 2 x 4 pixel pattern of the emblem's two colours x 2 x
//   the logo's red (a black logo on white), in its own gamma space (as OfflineDAoC, GPL-3.0, reports the
//   client's item and cloak shaders). Each layer has its own UV set. A Catacombs cloak: cloth on UV, pattern
//   on CUSTOM0.xy, logo on CUSTOM0.zw (mpk figures moves them there). An emblem item such as a shield: its base
//   texture slot is the pattern (cloakpattern01, on UV), its dark slot the logo (UV2) and its detail slot the
//   surface (CUSTOM0.xy); without an emblem only the surface shows.

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public static class ItemPaint
{
    private static readonly System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement?> Tables = new();
    private static readonly System.Collections.Generic.Dictionary<int, ImageTexture> Patterns = new();
    private static Shader? _shader;

    private static System.Text.Json.JsonElement? Table(string path)
    {
        if (!Tables.TryGetValue(path, out var t))
        {
            t = System.IO.File.Exists(path) ? System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path)).RootElement : null;
            Tables[path] = t;
        }
        return t;
    }

    private static Color Rgb(System.Text.Json.JsonElement e) => Color.Color8((byte)e[0].GetInt32(), (byte)e[1].GetInt32(), (byte)e[2].GetInt32());

    /// <summary>A dye's colour (tints.csv's Catacombs one), or null for none (0, or not in the table).</summary>
    public static Color? Dye(string coloursPath, int dye) =>
        dye > 0 && Table(coloursPath) is { } t && t.GetProperty("dyes").TryGetProperty(dye.ToString(System.Globalization.CultureInfo.InvariantCulture), out var c)
            ? Rgb(c) : null;

    /// <summary>Multiplies every surface of a held model by a dye (its own materials duplicated first).</summary>
    public static void DyeModel(Node3D model, Color dye)
    {
        foreach (Node n in model.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)n;
            for (int s = 0; s < (mi.Mesh?.GetSurfaceCount() ?? 0); s++)
                if (mi.GetActiveMaterial(s)?.Duplicate() is BaseMaterial3D m)
                {
                    m.AlbedoColor *= dye;
                    mi.SetSurfaceOverrideMaterial(s, m);
                }
        }
    }

    /// <summary>
    /// The material for a cloak or an emblem item: <paramref name="surface"/> (the cloth or the item's own
    /// texture) times the emblem's pattern and logo, or the surface alone with no emblem (an emblem item's
    /// surface still needs its own UV set). <paramref name="itemLayout"/>: the shield layout, else the cloak's.
    /// </summary>
    public static ShaderMaterial Material(string coloursPath, Texture2D? surface, Emblem? emblem, bool itemLayout,
        Color tint, params string[] dirs)
    {
        _shader ??= new Shader
        {
            Code = """
                shader_type spatial;
                render_mode cull_back;
                uniform sampler2D surface_tex : filter_linear_mipmap_anisotropic, repeat_enable;
                uniform sampler2D pattern_tex : filter_linear, repeat_enable;
                uniform sampler2D logo_tex : filter_linear_mipmap, repeat_disable;
                uniform bool has_surface = false;
                uniform bool has_emblem = false;
                uniform bool has_logo = false;
                uniform bool item_layout = false;
                uniform vec3 tint = vec3(1.0);
                varying vec4 extra_uv;
                void vertex() {
                    extra_uv = CUSTOM0;
                }
                void fragment() {
                    vec2 surface_uv = item_layout ? extra_uv.xy : UV;
                    vec2 pattern_uv = item_layout ? UV : extra_uv.xy;
                    vec2 logo_uv = item_layout ? UV2 : extra_uv.zw;
                    // In the client's gamma space, as it draws them; linear at the end.
                    vec3 c = (has_surface ? texture(surface_tex, surface_uv).rgb : vec3(0.6)) * tint;
                    if (has_emblem) {
                        c *= texture(pattern_tex, pattern_uv).rgb * 2.0;
                        if (has_logo)
                            c *= texture(logo_tex, logo_uv).r;
                    }
                    ALBEDO = pow(clamp(c, 0.0, 1.0), vec3(2.2));
                    ROUGHNESS = 1.0;
                }
                """,
        };
        var m = new ShaderMaterial { Shader = _shader };
        if (surface is not null)
            m.SetShaderParameter("surface_tex", surface);
        m.SetShaderParameter("has_surface", surface is not null);
        m.SetShaderParameter("item_layout", itemLayout);
        m.SetShaderParameter("tint", new Vector3(tint.R, tint.G, tint.B));
        if (emblem is { } e && Table(coloursPath) is { } t)
        {
            m.SetShaderParameter("has_emblem", true);
            m.SetShaderParameter("pattern_tex", Pattern(t, e));
            Texture2D? logo = t.GetProperty("logos").TryGetProperty(e.Logo.ToString(System.Globalization.CultureInfo.InvariantCulture), out var file)
                ? ModelTexturing.Load(file.GetString() ?? "", dirs) : null;
            if (logo is not null)
                m.SetShaderParameter("logo_tex", logo);
            m.SetShaderParameter("has_logo", logo is not null);
        }
        return m;
    }

    // The emblem's 2 x 4 background: the primary colour, the secondary where the pattern's mask says 1.
    private static ImageTexture Pattern(System.Text.Json.JsonElement t, Emblem e)
    {
        int key = e.Pattern << 7 | e.Primary << 3 | e.Secondary;
        if (Patterns.TryGetValue(key, out ImageTexture? cached))
            return cached;
        var masks = t.GetProperty("masks");
        var mask = e.Pattern < masks.GetArrayLength() ? masks[e.Pattern] : masks[masks.GetArrayLength() - 1];
        Color primary = Rgb(t.GetProperty("primary")[e.Primary & 15]), secondary = Rgb(t.GetProperty("secondary")[e.Secondary & 7]);
        var img = Image.CreateEmpty(2, 4, false, Image.Format.Rgb8);
        for (int i = 0; i < 8; i++)
            img.SetPixel(i % 2, i / 2, mask[i].GetInt32() == 1 ? secondary : primary);
        return Patterns[key] = ImageTexture.CreateFromImage(img);
    }
}
