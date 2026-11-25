// SPDX-License-Identifier: GPL-3.0-or-later
//
// The Manuscript look's pigments (shaders/manuscript.gdshader): the colours an illuminator's workshop had, each in a
// light tone (mixed with lead white), pure, and a dark tone (shaded with carbon black). The sRGB values are
// approximations of the pigments as they look on vellum, picked by eye; tune them here.

using System;
using Godot;

namespace EodClient;

public static class ManuscriptLook
{
    private static readonly (string Name, Color Colour)[] Pigments =
    [
        ("ultramarine (lapis)", new Color(0.16f, 0.24f, 0.60f)),
        ("azurite", new Color(0.29f, 0.46f, 0.68f)),
        ("vermilion", new Color(0.80f, 0.22f, 0.13f)),
        ("red lead", new Color(0.87f, 0.43f, 0.21f)),
        ("verdigris", new Color(0.24f, 0.54f, 0.44f)),
        ("green earth", new Color(0.42f, 0.52f, 0.30f)),
        ("sap green", new Color(0.30f, 0.42f, 0.16f)),
        ("yellow ochre", new Color(0.78f, 0.61f, 0.27f)),
        ("orpiment", new Color(0.90f, 0.76f, 0.32f)),
        ("red ochre", new Color(0.55f, 0.29f, 0.18f)),
        ("umber", new Color(0.36f, 0.27f, 0.18f)),
        ("folium", new Color(0.46f, 0.26f, 0.42f)),
    ];

    // Which surfaces are leaf, by their texture's name (the material's): gold for gold, gilt, brass and bronze; silver
    // for iron, steel and plate. Armour textures that mix metal with leather or cloth are left out (no name tells).
    private static readonly System.Text.RegularExpressions.Regex GoldNames =
        new("gold|gilt|gilded|brass|bronze", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SilverNames =
        new("silver|iron|steel|metal|plt\\d|chn\\d|platemail|chainlink|_chain|^chain|sword",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The leaf a surface gets in the Manuscript look, from its texture's name: 0 none, 1 gold, 2 silver.</summary>
    public static int Leaf(string? texture) =>
        string.IsNullOrEmpty(texture) || texture.Contains("stone", System.StringComparison.OrdinalIgnoreCase) ? 0   // stone with metal in it
        : GoldNames.IsMatch(texture) ? 1 : SilverNames.IsMatch(texture) ? 2 : 0;

    private const string CoverageMeta = "eod_alpha_to_coverage";

    /// <summary>The Manuscript look is showing (set by WorldView).</summary>
    public static bool Active { get; set; }

    /// <summary>
    /// A cut-out material's edges: alpha to coverage normally, a plain cut with the Manuscript look. Godot draws a cut-out
    /// with alpha to coverage in the transparent pass, after the screen copy the look repaints, so trees and walls with
    /// cut-outs would stay unpainted. Remembers that the material had it, to give it back; <c>fresh</c>: a cut-out just
    /// made, which gets the edges by default.
    /// </summary>
    public static bool SetCutOutEdges(BaseMaterial3D b, bool fresh = false)
    {
        if (b.Transparency != BaseMaterial3D.TransparencyEnum.AlphaScissor)
            return false;
        if (!b.HasMeta(CoverageMeta))
        {
            if (!fresh && b.AlphaAntialiasingMode == BaseMaterial3D.AlphaAntiAliasing.Off)
                return false;   // a plain cut-out of its own
            b.SetMeta(CoverageMeta, true);
        }
        BaseMaterial3D.AlphaAntiAliasing want = Active ? BaseMaterial3D.AlphaAntiAliasing.Off : BaseMaterial3D.AlphaAntiAliasing.AlphaToCoverage;
        if (b.AlphaAntialiasingMode == want)
            return false;
        b.AlphaAntialiasingMode = want;
        return true;
    }

    private static readonly Color LeadWhite = new(0.93f, 0.90f, 0.82f);
    private static readonly Color CarbonBlack = new(0.11f, 0.10f, 0.09f);

    /// <summary>
    /// Every tone the shader may paint with, in display (sRGB) colours: white, black, three grey washes (grisaille: stone
    /// stays grey instead of turning ochre), then each pigment light, pure and dark.
    /// </summary>
    public static Color[] Palette()
    {
        var all = new System.Collections.Generic.List<Color>
        {
            LeadWhite, CarbonBlack,
            LeadWhite.Lerp(CarbonBlack, 0.3f), LeadWhite.Lerp(CarbonBlack, 0.5f), LeadWhite.Lerp(CarbonBlack, 0.7f),
        };
        foreach ((_, Color c) in Pigments)
        {
            all.Add(c.Lerp(LeadWhite, 0.45f));
            all.Add(c);
            all.Add(c.Lerp(CarbonBlack, 0.35f));
        }
        return [.. all];
    }

    /// <summary>A shader material with the palette set (Oklab for matching, linear RGB for painting).</summary>
    public static ShaderMaterial Material()
    {
        Color[] palette = Palette();
        var lab = new Vector3[palette.Length];
        var rgb = new Vector3[palette.Length];
        for (int i = 0; i < palette.Length; i++)
        {
            Color linear = palette[i].SrgbToLinear();
            rgb[i] = new Vector3(linear.R, linear.G, linear.B);
            lab[i] = Oklab(rgb[i]);
        }
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/manuscript.gdshader"), RenderPriority = (int)Godot.Material.RenderPriorityMin };
        m.SetShaderParameter("pigment_lab", lab);
        m.SetShaderParameter("pigment_rgb", rgb);
        m.SetShaderParameter("pigment_count", palette.Length);
        return m;
    }

    /// <summary>Linear sRGB → Oklab (Björn Ottosson's matrices, as in the shader).</summary>
    public static Vector3 Oklab(Vector3 c)
    {
        float l = MathF.Cbrt(0.4122214708f * c.X + 0.5363325363f * c.Y + 0.0514459929f * c.Z);
        float m = MathF.Cbrt(0.2119034982f * c.X + 0.6806995451f * c.Y + 0.1073969566f * c.Z);
        float s = MathF.Cbrt(0.0883024619f * c.X + 0.2817188376f * c.Y + 0.6299787005f * c.Z);
        return new Vector3(
            0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
            1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
            0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }
}
