// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: "Atlantis Terrain", retail's default ground (W8; DaocAssets.Zone.AtlantisTerrain). Each of a zone's 8 x 8
// patches (one terrain tile mesh each, texCC-RR) is painted with a stack of tiled detail textures blended by masks:
// colour = layer 0, then lerp(colour, layer i, its mask weight) for each further layer. The zone's layer textures go
// into one texture array (decompressed, 512 x 512, mipmapped: they come as 512 and 1024 DXT1) and its masks into
// another; each patch gets a shader material with its layers' indices, repeats and mask slots. Settings → Classic
// terrain keeps the baked tiles instead, as retail's option does; a patch without layers keeps its tile. The blend is
// multiplied by the zone's ground shading (shadow.pgm, from shadow.pcx with the tree-cluster stamps: ZoneShadow), one
// texel per heightmap cell, as retail's terrain vertex colours.

using System.Collections.Generic;
using System.Linq;
using DaocAssets.Zone;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int AtlantisSize = 512, AtlantisMaxLayers = 12;

    // Decoded layer images by library path, shared by every zone that uses them.
    private static readonly Dictionary<string, Image?> AtlantisImages = [];

    private static readonly Shader AtlantisShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode cull_disabled;
            uniform sampler2DArray layers : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
            uniform sampler2DArray masks : filter_linear, repeat_disable;
            uniform int count;
            uniform int tex_index[12];
            uniform int mask_index[12];
            uniform int channel[12];
            uniform vec2 tiles[12];
            uniform int clamped[12];
            uniform vec2 flip;
            uniform sampler2D shade : hint_default_white, filter_linear, repeat_disable;
            uniform vec2 patch_at;
            void fragment() {
                // The tile's UVs, mirrored back where the zone mirrors its classic tiles (flip_x / flip_y).
                vec2 local = mix(UV, vec2(1.0) - UV, flip);
                vec3 colour = vec3(0.0);
                bool based = false;
                for (int i = 0; i < count; i++) {
                    if (tex_index[i] < 0) continue;
                    vec2 uv = clamped[i] == 1 ? clamp(local, vec2(0.001), vec2(0.999)) * tiles[i] : local * tiles[i];
                    vec3 c = texture(layers, vec3(uv, float(tex_index[i]))).rgb;
                    if (!based) { colour = c; based = true; continue; }
                    // The mask in the tile's own orientation: flipped vertically, the snow in Vale of Mularn broke off
                    // hard at every patch border (2026-11-09); as here, the patches meet.
                    float w = texture(masks, vec3(local, float(mask_index[i])))[channel[i]];
                    colour = mix(colour, c, w);
                }
                // The ground shading: texel i sits on heightmap cell i (32 cells per patch), as a vertex colour would.
                vec2 cell = (patch_at + local) * 32.0;
                colour *= texture(shade, (cell + 0.5) / vec2(textureSize(shade, 0))).r;
                ALBEDO = colour;
                ROUGHNESS = 1.0;
            }
            """,
    };

    // Puts the Atlantis material on each tile of a zone's terrain that has layers; true if it did.
    private static IEnumerable<bool> AtlantisSteps(ZoneParts parts, Node3D terrain, string libraryDir)
    {
        string csv = parts.Dir.Length == 0 ? "" : parts.Dir.PathJoin("atlantis.csv");
        if (ClientSettings.ClassicTerrain || csv.Length == 0 || !Godot.FileAccess.FileExists(csv))
            yield break;
        string text = Godot.FileAccess.GetFileAsString(csv);
        List<AtlantisLayer> layers = AtlantisTerrain.ReadCsv(text);
        // The converter's last line: "flip,x,y", the zone's mirroring (SECTOR.DAT flip_x / flip_y).
        string[] flipRow = text.Split('\n').FirstOrDefault(l => l.StartsWith("flip,"))?.Trim().Split(',') ?? [];
        bool flipX = flipRow.Length > 2 && flipRow[1] == "1", flipY = flipRow.Length > 2 && flipRow[2] == "1";
        if (layers.Count == 0)
            yield break;

        // The zone's distinct layer textures, in one array.
        var textures = layers.Select(l => l.Texture).Where(t => t.Length > 0).Distinct().ToList();
        var images = new Godot.Collections.Array<Image>();
        var texIndex = new Dictionary<string, int>();
        foreach (string t in textures)
        {
            if (LayerImage(libraryDir.PathJoin("textures").PathJoin("terraintex").PathJoin(t)) is Image img)
            {
                texIndex[t] = images.Count;
                images.Add(img);
            }
            yield return true;
        }
        // Its masks, in another (128 x 128, decompressed).
        string maskDir = libraryDir.PathJoin("textures").PathJoin("terrain").PathJoin($"zone{parts.Id:000}").PathJoin("atlantis");
        var maskImages = new Godot.Collections.Array<Image>();
        var maskIndex = new Dictionary<string, int>();
        foreach (string mask in layers.Select(l => l.Weight.Mask).Distinct())
        {
            string path = maskDir.PathJoin(mask);
            if (!Godot.FileAccess.FileExists(path))
                continue;
            var img = new Image();
            if (img.LoadDdsFromBuffer(Godot.FileAccess.GetFileAsBytes(path)) != Error.Ok)
                continue;
            if (img.IsCompressed())
                img.Decompress();
            img.ClearMipmaps();
            img.Convert(Image.Format.Rgba8);
            if (img.GetWidth() != 128 || img.GetHeight() != 128)
                img.Resize(128, 128);
            maskIndex[mask] = maskImages.Count;
            maskImages.Add(img);
        }
        if (images.Count == 0 || maskImages.Count == 0)
            yield break;
        // The ground shading (absent from zones converted before it: unshaded).
        ImageTexture? shade = null;
        string shadowPath = parts.Dir.PathJoin("shadow.pgm");
        if (Godot.FileAccess.FileExists(shadowPath)
            && ZoneShadow.ReadPgm(Godot.FileAccess.GetFileAsBytes(shadowPath)) is var (sw, sh, pixels))
            shade = ImageTexture.CreateFromImage(Image.CreateFromData(sw, sh, false, Image.Format.L8, pixels));
        var layerArray = new Texture2DArray();
        layerArray.CreateFromImages(images);
        var maskArray = new Texture2DArray();
        maskArray.CreateFromImages(maskImages);
        yield return true;

        var byPatch = layers.GroupBy(l => (l.PatchX, l.PatchY)).ToDictionary(g => g.Key, g => g.OrderBy(l => l.Index).ToList());
        int painted = 0;
        foreach (MeshInstance3D mi in FindMeshes(terrain))
        {
            // Tile meshes are named texCC-RR: column (patch x), row (patch y).
            string name = mi.Name.ToString();
            int dash = name.IndexOf('-');
            if (!name.StartsWith("tex") || dash < 4 || !int.TryParse(name[3..dash], out int px) || !int.TryParse(name[(dash + 1)..], out int py)
                || !byPatch.TryGetValue((px, py), out var stack) || mi.Mesh is null)
                continue;
            int n = System.Math.Min(stack.Count, AtlantisMaxLayers);
            int[] tex = new int[AtlantisMaxLayers], maskAt = new int[AtlantisMaxLayers], chan = new int[AtlantisMaxLayers], clamp = new int[AtlantisMaxLayers];
            var tiles = new Vector2[AtlantisMaxLayers];
            for (int i = 0; i < AtlantisMaxLayers; i++)
            {
                tex[i] = -1;
                if (i >= n)
                    continue;
                AtlantisLayer l = stack[i];
                if (l.Visible && texIndex.TryGetValue(l.Texture, out int ti))
                    tex[i] = ti;
                maskAt[i] = maskIndex.GetValueOrDefault(l.Weight.Mask);
                chan[i] = l.Weight.Channel;
                tiles[i] = new Vector2(l.Tiles / l.UScale, l.Tiles / l.VScale);
                clamp[i] = l.Tileable ? 0 : 1;
            }
            var mat = new ShaderMaterial { Shader = AtlantisShader };
            mat.SetShaderParameter("layers", layerArray);
            mat.SetShaderParameter("masks", maskArray);
            mat.SetShaderParameter("count", n);
            mat.SetShaderParameter("tex_index", tex);
            mat.SetShaderParameter("mask_index", maskAt);
            mat.SetShaderParameter("channel", chan);
            mat.SetShaderParameter("tiles", tiles);
            mat.SetShaderParameter("clamped", clamp);
            mat.SetShaderParameter("flip", new Vector2(flipX ? 1 : 0, flipY ? 1 : 0));
            mat.SetShaderParameter("patch_at", new Vector2(px, py));
            if (shade is not null)
                mat.SetShaderParameter("shade", shade);
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
                mi.SetSurfaceOverrideMaterial(s, mat);
            painted++;
        }
        GD.Print($"[world] zone {parts.Id}: Atlantis terrain on {painted} patch(es), {images.Count} layer texture(s), {maskImages.Count} mask(s), {(shade is null ? "no ground shading" : "ground shading")}");
        yield return true;
    }

    // A layer texture as the array wants it: decompressed, 512 x 512, mipmapped (decoded once per run).
    private static Image? LayerImage(string path)
    {
        if (AtlantisImages.TryGetValue(path, out Image? cached))
            return cached;
        Image? img = null;
        if (Godot.FileAccess.FileExists(path))
        {
            img = new Image();
            if (img.LoadDdsFromBuffer(DaocAssets.DdsMipmaps.TopLevelIfPartial(Godot.FileAccess.GetFileAsBytes(path))) != Error.Ok)
                img = null;
            else
            {
                if (img.IsCompressed())
                    img.Decompress();
                img.ClearMipmaps();
                img.Convert(Image.Format.Rgba8);
                if (img.GetWidth() != AtlantisSize || img.GetHeight() != AtlantisSize)
                    img.Resize(AtlantisSize, AtlantisSize, Image.Interpolation.Bilinear);
                img.GenerateMipmaps();
            }
        }
        AtlantisImages[path] = img;
        return img;
    }
}
