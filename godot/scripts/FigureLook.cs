// SPDX-License-Identifier: GPL-3.0-or-later
//
// A player's Catacombs appearance on its figure (roadmap M7b): the character's appearance bytes
// (DaocProtocol CharacterLook: face, hair style and colour, eye colour, skin tone) picked from the
// race's options file, models/fig3_<n>.json, which mpk figures writes from the install's fig3 tables.
// The face sliders drive the head's morph targets (models/fig3_<n>.json "sliders": each slider's low and
// high target). Not yet: the skin tone (read, not drawn: open question W30), mood, tattoos.

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public static class FigureLook
{
    // Each options file, parsed once (null: missing).
    private static readonly System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement?> Looks = new();

    /// <summary>
    /// Dresses a Catacombs figure in a character's appearance: the chosen hair style shows (the other
    /// styles hide; none for a bald style), wearing its colour's texture and tint; the head wears the
    /// chosen face texture with the eye colour texture multiplied over it (same UVs). Materials are per-instance copies. An index the race doesn't offer keeps the figure's own.
    /// <paramref name="optionsPath"/> is the race's models/fig3_<n>.json (mpk figures); <paramref name="dirs"/>
    /// are searched for textures. Returns a short description for the log ("" if the figure has no options).
    /// </summary>
    public static string Apply(Node3D figure, string optionsPath, CharacterLook look, params string[] dirs)
    {
        if (!Looks.TryGetValue(optionsPath, out System.Text.Json.JsonElement? opts))
        {
            opts = Godot.FileAccess.FileExists(optionsPath)
                ? System.Text.Json.JsonDocument.Parse(Godot.FileAccess.GetFileAsString(optionsPath)).RootElement : null;
            Looks[optionsPath] = opts;
        }
        if (opts is not { } o)
            return "";
        static System.Text.Json.JsonElement? Pick(System.Text.Json.JsonElement list, int i) =>
            i >= 0 && i < list.GetArrayLength() && list[i].ValueKind != System.Text.Json.JsonValueKind.Null ? list[i] : null;
        static Color Rgb(System.Text.Json.JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        ImageTexture? Tex(System.Text.Json.JsonElement? name) =>
            name?.GetString() is { } n ? ModelTexturing.Load(System.IO.Path.GetFileNameWithoutExtension(n), dirs) : null;

        System.Text.Json.JsonElement hairList = o.GetProperty("hair");
        int style = look.Hair < hairList.GetArrayLength() ? look.Hair : o.GetProperty("default_hair").GetInt32();
        System.Text.Json.JsonElement chosen = hairList[style];
        string? node = chosen.GetProperty("node").GetString();
        System.Text.Json.JsonElement? colour = Pick(chosen.GetProperty("colours"), look.HairColour);
        ImageTexture? face = Tex(Pick(o.GetProperty("faces"), look.Face));
        ImageTexture? eyes = Tex(Pick(o.GetProperty("eyes"), look.EyeColour));

        foreach (Node n in figure.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)n;
            string name = mi.Name.ToString();
            if (Part(name, "head"))
            {
                if (mi.GetActiveMaterial(0)?.Duplicate() is not BaseMaterial3D m)
                    continue;
                if (face is not null) m.AlbedoTexture = face;
                if (eyes is not null)
                {
                    m.DetailEnabled = true;
                    m.DetailAlbedo = eyes;
                    m.DetailUVLayer = BaseMaterial3D.DetailUV.UV1;
                    m.DetailBlendMode = BaseMaterial3D.BlendModeEnum.Mul;
                }
                mi.MaterialOverride = m;
                SetSliders(mi, o, look);
            }
            else if (name.StartsWith("hair", StringComparison.Ordinal))
            {
                string styleNode = name.Split('_')[0];
                mi.Visible = styleNode == node;
                if (mi.Visible && colour is { } c && mi.GetActiveMaterial(0)?.Duplicate() is BaseMaterial3D m)
                {
                    if (Tex(c.GetProperty("tex")) is { } t) m.AlbedoTexture = t;
                    m.AlbedoColor = Rgb(c.GetProperty("tint"));
                    mi.MaterialOverride = m;
                }
            }
        }
        return $" (face {look.Face}, hair {style}{(node is null ? " bald" : "")} colour {look.HairColour}, eyes {look.EyeColour}, skin {look.SkinTone} not drawn, " +
            $"sliders {look.Slider(0)}{look.Slider(1)}{look.Slider(2)}{look.Slider(3)})";

        static void SetSliders(MeshInstance3D head, System.Text.Json.JsonElement options, CharacterLook look)
        {
            if (!options.TryGetProperty("sliders", out System.Text.Json.JsonElement sliders))
                return;
            int shapes = head.GetBlendShapeCount();
            for (int k = 0; k < sliders.GetArrayLength() && k < 4; k++)
            {
                // 0..8, 4 neutral: below 4 blends toward the low target, above toward the high one.
                float v = Mathf.Clamp(look.Slider(k), 0, 8);
                int low = sliders[k].GetProperty("low").GetInt32(), high = sliders[k].GetProperty("high").GetInt32();
                if (low >= 0 && low < shapes) head.SetBlendShapeValue(low, Mathf.Clamp((4 - v) / 4, 0, 1));
                if (high >= 0 && high < shapes) head.SetBlendShapeValue(high, Mathf.Clamp((v - 4) / 4, 0, 1));
            }
        }

        static bool Part(string name, string label) => name == label || name.StartsWith(label + "_", StringComparison.Ordinal);
    }
}
