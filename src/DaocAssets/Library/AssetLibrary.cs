// SPDX-License-Identifier: GPL-3.0-or-later
//
// The convert-once asset library. Backed by an AssetIndex (so sources are found without rescanning
// the install), it converts each referenced model to <lib>/models/<name>.glb and gathers its
// textures to <lib>/textures/<name>.dds, once, caching on disk. A model/texture present in
// <lib>/overrides/ wins over the converted one at resolve time.
//
// Two layers, two policies:
//  - models/ and textures/ are client-DERIVED content: owner-local (private/), never redistributed.
//    The library exists so the client loads from it instead of us shipping the originals.
//  - overrides/ are assets WE OR CONTRIBUTORS author (original work), cleanly distributable; they
//    are how the asset set becomes open over time. (See CLAUDE.md, "Game client and assets".)

using DaocAssets.Nif;

namespace DaocAssets.Library;

/// <summary>Where a resolved asset came from.</summary>
public enum AssetOrigin { Override, Converted, Missing }

/// <summary>A resolved model: its .glb path (or null if missing) and whether it was an override.</summary>
public readonly record struct ModelResult(string? GlbPath, AssetOrigin Origin);

/// <summary>
/// A convert-once library over a client install. <c>models/</c> and <c>textures/</c> hold the
/// converted assets; <c>overrides/</c> holds authored replacements that win. Models convert lazily
/// on first request and are cached on disk, so the second request (and later runs) is a file check.
/// </summary>
public sealed class AssetLibrary
{
    private readonly AssetIndex _index;
    private readonly string _root;
    private readonly string _models;
    private readonly string _textures;
    private readonly string _overrides;

    public AssetLibrary(AssetIndex index, string libraryRoot)
    {
        _index = index;
        _root = libraryRoot;
        _models = Path.Combine(_root, "models");
        _textures = Path.Combine(_root, "textures");
        _overrides = Path.Combine(_root, "overrides");
    }

    /// <summary>
    /// Returns the .glb for model <paramref name="nifName"/>, converting it from the install (via the
    /// index) on first request and caching it. An <c>overrides/&lt;stem&gt;.glb</c> wins and is used
    /// as-is. Returns <see cref="AssetOrigin.Missing"/> if the model isn't in the index and has no
    /// override. Textures the model references are gathered into <c>textures/</c> as a side effect.
    /// </summary>
    public ModelResult GetModel(string nifName)
    {
        string stem = Path.GetFileNameWithoutExtension(BareName(nifName));
        string glbName = stem + ".glb";

        string overridePath = Path.Combine(_overrides, glbName);
        if (File.Exists(overridePath))
            return new ModelResult(overridePath, AssetOrigin.Override);

        string glbPath = Path.Combine(_models, glbName);
        if (File.Exists(glbPath))
            return new ModelResult(glbPath, AssetOrigin.Converted);

        byte[]? nifBytes = _index.Read(nifName);
        if (nifBytes is null)
            return new ModelResult(null, AssetOrigin.Missing);

        try
        {
            NifModel model = NifFile.Read(nifBytes);
            Directory.CreateDirectory(_models);
            GltfExporter.SaveGlb(model, glbPath);
            foreach (NifMesh mesh in model.Meshes)
                if (!string.IsNullOrEmpty(mesh.TextureFile))
                    GatherTexture(mesh.TextureFile!);
            return new ModelResult(glbPath, AssetOrigin.Converted);
        }
        catch (NifFormatException)
        {
            return new ModelResult(null, AssetOrigin.Missing);
        }
    }

    /// <summary>
    /// Ensures texture <paramref name="textureName"/> is in <c>textures/</c> (converted) unless an
    /// override exists; returns the resolved path, or null if the index doesn't have it. The lookup
    /// resolves across texture extensions (a NIF's stale <c>.tga</c> reference finds the shipped
    /// <c>.dds</c>) and the file is written under its real on-disk name so the stem-based Godot loader
    /// finds it. Copies the client's texture bytes verbatim (no transcode) so the client can load it.
    /// </summary>
    public string? GatherTexture(string textureName)
    {
        string stem = Path.GetFileNameWithoutExtension(BareName(textureName));

        // An override wins. Match by stem across texture extensions (the NIF reference may differ).
        if (Directory.Exists(_overrides))
            foreach (string ext in TextureExts)
            {
                string op = Path.Combine(_overrides, stem + ext);
                if (File.Exists(op))
                    return op;
            }

        // Already gathered? (match by stem — we store under the real on-disk name).
        if (Directory.Exists(_textures))
            foreach (string ext in TextureExts)
            {
                string ep = Path.Combine(_textures, stem + ext);
                if (File.Exists(ep))
                    return ep;
            }

        (byte[] Bytes, string FileName)? found = _index.ReadTexture(textureName);
        if (found is null)
            return null;
        Directory.CreateDirectory(_textures);
        // Store under a lower-cased name so the stem-based loader finds it on case-sensitive
        // filesystems too (the NIF reference, the on-disk file and the glTF material name disagree on
        // case; the loader lower-cases the stem it looks up, so we lower-case what we write).
        string dest = Path.Combine(_textures, found.Value.FileName.ToLowerInvariant());
        File.WriteAllBytes(dest, found.Value.Bytes);
        return dest;
    }

    private static readonly string[] TextureExts = [".dds", ".tga", ".bmp"];

    /// <summary>The directory a loader should read assets from: the library root. Loaders resolve
    /// <c>overrides/</c> then <c>models/</c> / <c>textures/</c> themselves (see the Godot side).</summary>
    public string Root => _root;

    private static string BareName(string name)
    {
        int slash = name.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? name : name[(slash + 1)..];
    }
}
