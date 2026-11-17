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

/// <summary>Where a model came from: an override, a conversion, not in the install (Missing), in the
/// install but in a form the reader doesn't take (Unsupported: an old NIF version, or no geometry we
/// read), or in the install but the conversion threw (Failed: a converter bug to fix).</summary>
public enum AssetOrigin { Override, Converted, Missing, Unsupported, Failed }

/// <summary>A resolved model: its .glb path (or null if missing) and whether it was an override.</summary>
public readonly record struct ModelResult(string? GlbPath, AssetOrigin Origin, string? Error = null);

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
    // A scoped view (Scoped): files come from these install folders first, and a file that differs from
    // the default one of its name is stored under models/<scope>/ or textures/<scope>/.
    private string? _scope;
    private IReadOnlyList<string>? _prefer;
    // Models convert with the client's model texture lookup: the model's own folder, then zones/Nifs (W63). Off for
    // figures, items and effects, whose skins go through a loader of their own (not traced): they keep the
    // install-wide lookup preferring their folders.
    private bool _modelFolders = true;
    // A dungeon's texture renames (texproxy.csv) and the scope a model they change is stored in; _proxied: this view
    // converts such a model (its texture names renamed).
    private TextureProxy? _proxy;
    private string? _proxyScope;
    private bool _proxied;

    /// <summary>
    /// The view a dungeon's fixtures convert in: their texture names go through the dungeon's <c>texproxy.csv</c> first,
    /// as the client loads them. A model one of whose texture names it renames is stored as that dungeon's own
    /// (<c>models/&lt;scope&gt;/</c>, its materials naming the new textures); any other converts as anywhere else.
    /// </summary>
    public AssetLibrary WithTextureProxy(string scope, TextureProxy proxy) => proxy.Count == 0 ? this
        : new(_index, _root) { _scope = _scope, _prefer = _prefer, _modelFolders = _modelFolders, _proxy = proxy, _proxyScope = scope };

    /// <summary>
    /// A view of the library for one kind of asset ("figures", "items", "effects"): names resolve to the
    /// install's <paramref name="folders"/> first. Where that is a different file from the one zone
    /// conversion uses, it is stored under its own folder, so neither overwrites the other: the client
    /// looks in textures/&lt;scope&gt;/ (models/&lt;scope&gt;/) first for these models.
    /// </summary>
    public AssetLibrary Scoped(string scope, params string[] folders) =>
        new(_index, _root) { _scope = scope, _prefer = folders, _modelFolders = false };

    /// <summary>As <see cref="Scoped"/>, for zone models (a capital's pieces, the tutorial's): their textures are looked
    /// up as the client does for a model, in the model's own folder, then zones/Nifs.</summary>
    public AssetLibrary ZoneScoped(string scope, params string[] folders) =>
        new(_index, _root) { _scope = scope, _prefer = folders };

    // The view a model converts in. The client looks a model's textures up in the model's own folder, then the global
    // zones/Nifs (daoc-re, 2026-11-16; W63). A model stored elsewhere than zones/Nifs (a capital's pieces in
    // zones/zoneNNN/nifs, the tutorial's, housing's, Newtowns') converts in a scope named after its folder ("zone026",
    // "tutorial", "phousing"), so a texture of its own folder that differs from the zones/Nifs one is stored apart
    // (textures/<scope>/) instead of overwriting the one zones/Nifs models use. Items, figures and effects keep the
    // lookup of their own views (Scoped), which share their stored files.
    private AssetLibrary ForModel(string nifName)
    {
        if (_scope is not null || !_modelFolders || _index.Locate(nifName, null) is not { } loc)
            return this;
        string folder = _index.RelativeFolder(loc);
        if (folder.Equals(AssetIndex.GlobalTextureFolder, StringComparison.OrdinalIgnoreCase))
            return this;
        string[] parts = folder.ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] is "items" or "figures" or "effects")
            return new AssetLibrary(_index, _root) { _modelFolders = false };
        string scope = string.Join('-', parts.Where(p => p is not ("zones" or "nifs")));
        return ZoneScoped(scope.Length > 0 ? scope : parts[0], folder, AssetIndex.GlobalTextureFolder);
    }

    // Where a converted model or texture of this name goes: the shared folder, or the scope's own when the
    // scope's file differs from the default one (a byte-identical copy elsewhere still shares).
    private string Folder(string shared, string name, bool texture, string? modelFolder = null)
    {
        if (_scope is null || _prefer is null)
            return shared;
        if (texture)
        {
            // The scope's copy against the one a model in zones/Nifs (or an unscoped one) gets.
            var mineAt = FindTexture(name, modelFolder);
            var theirsAt = FindTexture(name, modelFolder is null ? null : AssetIndex.GlobalTextureFolder, shared: true);
            if (mineAt is null || theirsAt is null)
                return mineAt is null ? shared : Path.Combine(shared, _scope);
            if (mineAt.Value.Location == theirsAt.Value.Location)
                return shared;
            byte[]? m = _index.Read(mineAt)?.Bytes, t = _index.Read(theirsAt)?.Bytes;
            return m is not null && t is not null && m.AsSpan().SequenceEqual(t) ? shared : Path.Combine(shared, _scope);
        }
        if (_index.IsDefault(name, _prefer))
            return shared;
        byte[]? mine = _index.Read(name, _prefer);
        byte[]? theirs = _index.Read(name);
        return mine is not null && theirs is not null && mine.AsSpan().SequenceEqual(theirs) ? shared : Path.Combine(shared, _scope);
    }

    // A texture for a model stored in modelFolder (the client's lookup), or, without one, the install-wide lookup
    // preferring this view's folders (shared: no preference).
    private (AssetLocation Location, string FileName)? FindTexture(string name, string? modelFolder, bool shared = false) =>
        modelFolder is not null ? _index.LocateModelTexture(name, modelFolder) : _index.LocateTexture(name, shared ? null : _prefer);

    /// <summary>How a placement manifest names a converted model: its path under <c>models/</c> with '/'
    /// ("elm1.glb", or "tutorial/ochpier.glb" for a scope's own copy); an override by its file name.</summary>
    public string ManifestName(string glbPath)
    {
        string rel = Path.GetRelativePath(_models, glbPath);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)
            ? Path.GetFileName(glbPath) : rel.Replace('\\', '/');
    }

    /// <summary>
    /// The path as the file is actually named. On a case-insensitive filesystem a model converted for one
    /// zone as "beech_gnarl_dead.glb" is found again for another zone's "Beech_gnarl_dead.nif"; the manifest
    /// must carry the stored spelling, or the client can't open it on a case-sensitive one (Godot warns).
    /// </summary>
    private static string OnDiskName(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is null)
            return path;
        string name = Path.GetFileName(path);
        foreach (string f in Directory.EnumerateFiles(dir, name, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
            if (Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                return f;
        return path;
    }

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
    /// <remarks>With <paramref name="reconvert"/> the cached glb and textures are ignored and converted
    /// again (the Makefile asks for this when the converter changed).</remarks>
    public ModelResult GetModel(string nifName, bool reconvert = false)
    {
        if (_proxy is not null && !_proxied)
            return GetProxiedModel(nifName, reconvert);
        if (ForModel(nifName) is { } own && !ReferenceEquals(own, this))
            return own.GetModel(nifName, reconvert);
        string stem = Path.GetFileNameWithoutExtension(BareName(nifName));
        string glbName = stem + ".glb";

        string overridePath = Path.Combine(_overrides, glbName);
        if (File.Exists(overridePath))
            return new ModelResult(overridePath, AssetOrigin.Override);

        string models = Folder(_models, nifName, texture: false);
        string scoped = _scope is null ? models : Path.Combine(_models, _scope);
        if (!reconvert)
            foreach (string dir in (_proxied ? [scoped] : new[] { scoped, models }).Distinct())
                if (File.Exists(Path.Combine(dir, glbName)))
                    return new ModelResult(OnDiskName(Path.Combine(dir, glbName)), AssetOrigin.Converted);

        byte[]? nifBytes = _index.Read(nifName, _prefer);
        if (nifBytes is null)
            return new ModelResult(null, AssetOrigin.Missing);

        try
        {
            // With its patch files merged in (<name>_p00.nif, … beside it), as the client loads it.
            NifModel model = NifFile.Read(nifBytes, _index.ReadPatches(nifName, _prefer));
            if (_proxied)
                model = Renamed(model, _proxy!);
            // Textures first, the glb last: a glb on disk means its model converted completely.
            // Base textures and the extra layers (dark and detail maps) the client binds by name.
            // A model one of whose textures is the scope's own copy goes in the scope's folder too: the client takes a
            // model's textures from its scope's folder first only for a model stored there (W63: a city's pieces).
            bool scopedTexture = false;
            string? modelFolder = _modelFolders && _index.Locate(nifName, _prefer) is { } at ? _index.RelativeFolder(at) : null;
            string scopeTextures = _scope is null ? "" : Path.GetFullPath(Path.Combine(_textures, _scope));
            foreach (NifMesh mesh in model.Meshes)
                foreach (string? texture in TextureNames(mesh))
                    if (!string.IsNullOrEmpty(texture) && GatherTexture(texture!, reconvert, modelFolder) is string got && _scope is not null
                        && string.Equals(Path.GetDirectoryName(Path.GetFullPath(got)), scopeTextures, StringComparison.OrdinalIgnoreCase))
                        scopedTexture = true;
            string glbPath = Path.Combine(scopedTexture || _proxied ? scoped : models, glbName);
            Directory.CreateDirectory(Path.GetDirectoryName(glbPath)!);
            WriteAtomically(glbPath, tmp => GltfExporter.SaveGlb(model, tmp));
            return new ModelResult(glbPath, AssetOrigin.Converted);
        }
        catch (NifFormatException e)
        {
            return new ModelResult(null, AssetOrigin.Unsupported, $"{nifName}: {e.Message}");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            // One model the converter can't export must not take the whole zone down with it.
            return new ModelResult(null, AssetOrigin.Failed, $"{nifName}: {e.GetType().Name}: {e.Message}");
        }
    }

    // A dungeon fixture: converted with its texture names renamed (in the dungeon's scope) when the table renames any of
    // them, else as this view without the table.
    private ModelResult GetProxiedModel(string nifName, bool reconvert)
    {
        var plain = new AssetLibrary(_index, _root) { _scope = _scope, _prefer = _prefer, _modelFolders = _modelFolders };
        string glb = Path.Combine(_models, _proxyScope!, Path.GetFileNameWithoutExtension(BareName(nifName)) + ".glb");
        if (File.Exists(Path.Combine(_overrides, Path.GetFileName(glb))))
            return plain.GetModel(nifName, reconvert);
        if (!reconvert && File.Exists(glb))
            return new ModelResult(OnDiskName(glb), AssetOrigin.Converted);
        if (_index.Read(nifName, _prefer) is not byte[] bytes || _index.Locate(nifName, _prefer) is not { } at)
            return plain.GetModel(nifName, reconvert);
        NifModel model;
        try
        {
            model = NifFile.Read(bytes, _index.ReadPatches(nifName, _prefer));
        }
        catch (Exception e) when (e is NifFormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return plain.GetModel(nifName, reconvert);   // reports the same failure
        }
        if (!model.Meshes.Any(m => TextureNames(m).Any(t => !string.IsNullOrEmpty(t) && _proxy!.Apply(t!) != t)))
            return plain.GetModel(nifName, reconvert);
        var view = new AssetLibrary(_index, _root)
        {
            _scope = _proxyScope, _prefer = [_index.RelativeFolder(at), AssetIndex.GlobalTextureFolder],
            _proxy = _proxy, _proxyScope = _proxyScope, _proxied = true,
        };
        return view.GetModel(nifName, reconvert);
    }

    private static string?[] TextureNames(NifMesh m) =>
        [m.TextureFile, m.DarkTextureFile, m.DetailTextureFile, m.DecalTextureFile, m.GlowTextureFile];

    private static NifModel Renamed(NifModel model, TextureProxy proxy)
    {
        string? R(string? t) => string.IsNullOrEmpty(t) ? t : proxy.Apply(t);
        return new NifModel
        {
            Version = model.Version,
            Meshes = model.Meshes.Select(m => m with
            {
                TextureFile = R(m.TextureFile), DarkTextureFile = R(m.DarkTextureFile), DetailTextureFile = R(m.DetailTextureFile),
                DecalTextureFile = R(m.DecalTextureFile), GlowTextureFile = R(m.GlowTextureFile),
            }).ToList(),
            Joints = model.Joints,
            NodeWorlds = model.NodeWorlds,
        };
    }

    /// <summary>
    /// Ensures texture <paramref name="textureName"/> is in <c>textures/</c> (converted) unless an
    /// override exists; returns the resolved path, or null if the index doesn't have it. The lookup
    /// resolves across texture extensions (a NIF's stale <c>.tga</c> reference finds the shipped
    /// <c>.dds</c>) and the file is written under its real on-disk name so the stem-based Godot loader
    /// finds it. Copies the client's texture bytes verbatim (no transcode) so the client can load it.
    /// </summary>
    /// <remarks>With <paramref name="refresh"/> an already gathered copy is replaced.</remarks>
    public string? GatherTexture(string textureName, bool refresh = false) => GatherTexture(textureName, refresh, null);

    // With modelFolder, the texture of a model stored there, looked up as the client does (AssetIndex.LocateModelTexture).
    private string? GatherTexture(string textureName, bool refresh, string? modelFolder)
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
        string textures = Folder(_textures, textureName, texture: true, modelFolder);
        if (!refresh && Directory.Exists(textures))
            foreach (string ext in TextureExts)
            {
                string ep = Path.Combine(textures, stem + ext);
                if (File.Exists(ep))
                    return ep;
            }

        (byte[] Bytes, string FileName)? found = _index.Read(FindTexture(textureName, modelFolder));
        if (found is null)
            return null;
        Directory.CreateDirectory(textures);
        // Store under a lower-cased name so the stem-based loader finds it on case-sensitive
        // filesystems too (the NIF reference, the on-disk file and the glTF material name disagree on
        // case; the loader lower-cases the stem it looks up, so we lower-case what we write).
        string dest = Path.Combine(textures, found.Value.FileName.ToLowerInvariant());
        WriteAtomically(dest, tmp => File.WriteAllBytes(tmp, found.Value.Bytes));
        return dest;
    }

    /// <summary>
    /// Writes <paramref name="path"/> through a temporary file in the same folder, then renames it.
    /// A file that exists is therefore complete: an interrupted run leaves no half-written file
    /// that a build tool would take as up to date, and parallel conversions that gather the same
    /// texture never see each other's partial writes.
    /// </summary>
    private static void WriteAtomically(string path, Action<string> write)
    {
        string tmp = Path.Combine(Path.GetDirectoryName(path)!,
            $".{Path.GetFileNameWithoutExtension(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            write(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
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
