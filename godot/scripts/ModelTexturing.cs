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
    public static int Apply(Node modelRoot, string textureDir)
    {
        if (string.IsNullOrEmpty(textureDir) || !DirAccess.DirExistsAbsolute(textureDir))
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

                ImageTexture? tex = LoadTexture(textureDir, stem, cache);
                if (tex is null)
                    continue;

                // Duplicate so we don't mutate a material shared across surfaces/instances.
                var copy = (BaseMaterial3D)material.Duplicate();
                copy.AlbedoTexture = tex;
                mi.SetSurfaceOverrideMaterial(s, copy);
                textured++;
            }
        }
        return textured;
    }

    // Loads <stem>.dds (then .tga/.bmp as fallbacks via Godot's own loaders) into an ImageTexture,
    // caching by stem. Returns null when no readable texture file is found.
    private static ImageTexture? LoadTexture(
        string dir, string stem, System.Collections.Generic.Dictionary<string, ImageTexture?> cache)
    {
        if (cache.TryGetValue(stem, out ImageTexture? cached))
            return cached;

        ImageTexture? result = null;
        foreach (string ext in new[] { ".dds", ".tga", ".bmp", ".png" })
        {
            string path = dir.PathJoin(stem + ext);
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
                result = ImageTexture.CreateFromImage(img);
                break;
            }
            GD.PrintErr($"[texture] {stem}{ext} failed to decode ({err}).");
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
