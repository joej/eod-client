// SPDX-License-Identifier: GPL-3.0-or-later
//
// Applies the owner's extracted DAoC textures to a loaded model. Our glTF exporter names each
// material after its texture (e.g. "AmbientCreaturesTexture"); the asset pipeline
// (tools/pipeline.sh) gathers the matching <name>.dds beside the .glb. Here we load that .dds at
// runtime and set it as the material's albedo. The textures live in the owner's folder, never in
// this repo (see CLAUDE.md, "Game client and assets").
//
// Godot's generic Image.load() does not recognise .dds, but Image.load_dds_from_buffer() does, so
// we read the file ourselves and decode from the buffer (verified against real client textures).

using Godot;

namespace EodClient;

public static class ModelTexturing
{
    /// <summary>
    /// Walks <paramref name="modelRoot"/> for mesh surfaces and, for each material, loads
    /// &lt;material-name&gt;.dds from <paramref name="textureDir"/> and assigns it as the albedo.
    /// Materials whose texture is absent are left as-is. Returns how many surfaces were textured.
    /// Missing directory or a non-DDS file is tolerated (the model simply stays untextured).
    /// </summary>
    public static int Apply(Node modelRoot, params string[] textureDirs)
    {
        // Keep only the dirs that exist, in the given priority order (e.g. overrides before models).
        var dirs = new System.Collections.Generic.List<string>();
        foreach (string d in textureDirs)
            if (!string.IsNullOrEmpty(d) && DirAccess.DirExistsAbsolute(d))
                dirs.Add(d);
        if (dirs.Count == 0)
            return 0;

        var cache = new System.Collections.Generic.Dictionary<string, ImageTexture?>();
        int textured = 0;
        foreach (MeshInstance3D mi in Meshes(modelRoot))
        {
            Mesh? mesh = mi.Mesh;
            if (mesh is null)
                continue;
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                // The active material is the glTF-imported one; its resource name is the texture stem.
                if (mi.GetActiveMaterial(s) is not BaseMaterial3D material)
                    continue;
                string stem = material.ResourceName;
                if (string.IsNullOrEmpty(stem))
                    continue;

                ImageTexture? tex = LoadTexture(dirs, stem, cache);
                if (tex is null)
                    continue;

                // Duplicate so we don't mutate a material shared across surfaces/instances.
                var copy = (BaseMaterial3D)material.Duplicate();
                copy.AlbedoTexture = tex;
                // Sharp at shallow angles (the ground under a chase camera) rather than blurred.
                copy.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
                // Cut-out foliage: with MSAA, alpha to coverage smooths the leaves' cut edges instead
                // of leaving them stair-stepped and shimmering.
                if (copy.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor)
                    copy.AlphaAntialiasingMode = BaseMaterial3D.AlphaAntiAliasing.AlphaToCoverage;
                ApplyLayers(copy, material, dirs, cache);
                mi.SetSurfaceOverrideMaterial(s, copy);
                textured++;
            }
        }
        return textured;
    }

    private const string LayersMeta = "eod_layers";

    /// <summary>
    /// Copies each material's glTF <c>extras</c> (the converter's extra layers: dark and detail map
    /// names, vertex colours) onto the Godot material as metadata, read straight from the file's JSON.
    /// Call after <c>GenerateScene</c>, before <see cref="Apply"/>.
    /// </summary>
    public static void TagLayers(GltfState state)
    {
        if (!state.Json.TryGetValue("materials", out Variant mats) || mats.VariantType != Variant.Type.Array)
            return;
        Godot.Collections.Array list = mats.AsGodotArray();
        Godot.Collections.Array<Material> materials = state.GetMaterials();
        for (int i = 0; i < list.Count && i < materials.Count; i++)
            if (list[i].AsGodotDictionary().TryGetValue("extras", out Variant extras)
                && extras.VariantType == Variant.Type.Dictionary)
                materials[i].SetMeta(LayersMeta, extras);
    }

    // DAoC's extra texture layers, as Godot's material detail layer (its own UV set, multiplied):
    // the dark map (mostly baked shading; the moss and rock of Domnann's standing stones), else the
    // detail map. Vertex colours, where the model says to use them, tint the base colour.
    private static void ApplyLayers(BaseMaterial3D copy, Material original, System.Collections.Generic.List<string> dirs,
        System.Collections.Generic.Dictionary<string, ImageTexture?> cache)
    {
        if (!original.HasMeta(LayersMeta))
            return;
        Godot.Collections.Dictionary layers = original.GetMeta(LayersMeta).AsGodotDictionary();
        string? layer = layers.TryGetValue("dark", out Variant d) ? d.AsString()
            : layers.TryGetValue("detail", out Variant t) ? t.AsString() : null;
        if (layer is not null && LoadTexture(dirs, layer, cache) is { } tex)
        {
            copy.DetailEnabled = true;
            copy.DetailAlbedo = tex;
            copy.DetailUVLayer = BaseMaterial3D.DetailUV.UV2;
            copy.DetailBlendMode = BaseMaterial3D.BlendModeEnum.Mul;
        }
        // A decal (a figure's face over its hair skin): mixed over the base by the decal's own alpha.
        else if (layers.TryGetValue("decal", out Variant dc) && LoadTexture(dirs, dc.AsString(), cache) is { } decal)
        {
            copy.DetailEnabled = true;
            copy.DetailAlbedo = decal;
            copy.DetailUVLayer = BaseMaterial3D.DetailUV.UV2;
            copy.DetailBlendMode = BaseMaterial3D.BlendModeEnum.Mix;
        }
        if (layers.ContainsKey("vertexColors"))
            copy.VertexColorUseAsAlbedo = true;
    }

    /// <summary>A layer's texture name from an imported material's glTF extras ("dark", "detail", "decal"), or null.</summary>
    public static string? Layer(Material? material, string layer) =>
        material is not null && material.HasMeta(LayersMeta)
        && material.GetMeta(LayersMeta).AsGodotDictionary().TryGetValue(layer, out Variant v) ? v.AsString() : null;

    /// <summary>Loads one texture by stem (e.g. "watery") from the first dir that has it, or null.</summary>
    public static ImageTexture? Load(string stem, params string[] dirs)
    {
        var existing = new System.Collections.Generic.List<string>();
        foreach (string d in dirs)
            if (!string.IsNullOrEmpty(d) && DirAccess.DirExistsAbsolute(d))
                existing.Add(d);
        return existing.Count == 0 ? null
            : LoadTexture(existing, stem, new System.Collections.Generic.Dictionary<string, ImageTexture?>());
    }

    // Loads <stem>.dds (then .tga/.bmp/.png) into an ImageTexture, searching the dirs in order (so an
    // overrides dir wins). Cached by stem. Returns null when no readable texture file is found.
    private static ImageTexture? LoadTexture(
        System.Collections.Generic.List<string> dirs, string stem,
        System.Collections.Generic.Dictionary<string, ImageTexture?> cache)
    {
        if (cache.TryGetValue(stem, out ImageTexture? cached))
            return cached;

        // The library stores textures under a lower-cased name (so the stem the glTF material carries
        // resolves on case-sensitive filesystems, where the NIF's casing and the on-disk file differ).
        string key = stem.ToLowerInvariant();
        ImageTexture? result = null;
        foreach (string dir in dirs)
        {
            foreach (string ext in new[] { ".dds", ".tga", ".bmp", ".png" })
            {
                string path = dir.PathJoin(key + ext);
                if (!Godot.FileAccess.FileExists(path))
                    continue;

                byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
                if (bytes.Length == 0)
                    continue;

                var img = new Image();
                Error err = ext switch
                {
                    ".dds" => img.LoadDdsFromBuffer(bytes),
                    ".tga" => img.LoadTgaFromBuffer(bytes),
                    ".bmp" => img.LoadBmpFromBuffer(bytes),
                    _ => img.LoadPngFromBuffer(bytes),
                };
                if (err == Error.Ok)
                {
                    // TGA and BMP (and the odd DDS) come without mipmaps, so they shimmered at a distance.
                    if (!img.HasMipmaps())
                    {
                        if (img.IsCompressed())
                            img.Decompress();
                        img.GenerateMipmaps();
                    }
                    result = ImageTexture.CreateFromImage(img);
                    break;
                }
                GD.PrintErr($"[texture] {stem}{ext} failed to decode ({err}).");
            }
            if (result is not null)
                break;
        }
        cache[stem] = result;
        return result;
    }

    private static System.Collections.Generic.IEnumerable<MeshInstance3D> Meshes(Node node)
    {
        if (node is MeshInstance3D mi)
            yield return mi;
        foreach (Node child in node.GetChildren())
            foreach (MeshInstance3D m in Meshes(child))
                yield return m;
    }
}
