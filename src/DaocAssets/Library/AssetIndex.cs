// SPDX-License-Identifier: GPL-3.0-or-later
//
// An index of where every model/texture lives in a client install: by bare file name (lower-case)
// to a location — a loose file path, or an archive path + member name. Built once by walking the
// install (opening each .mpk/.npk once to read its member list), saved as JSON under the owner's
// private area, and reused so later library builds and lookups never re-scan. It also documents the
// install layout. Owner-run; the index describes a legitimate owner's own files and, like the
// converted library, stays owner-local — never in the repo or a release (CLAUDE.md, assets policy).

using System.Text.Json;
using DaocAssets.Mpk;

namespace DaocAssets.Library;

/// <summary>Where one asset lives: a loose file, or a member inside an archive.</summary>
public readonly record struct AssetLocation(string? LoosePath, string? Archive, string? Member)
{
    public bool InArchive => Archive is not null;
    public static AssetLocation Loose(string path) => new(path, null, null);
    public static AssetLocation InArchiveAt(string archive, string member) => new(null, archive, member);
}

/// <summary>
/// A name → location index over a client install, for models (.nif) and textures (.dds/.tga/.bmp).
/// Build with <see cref="Build"/>, persist with <see cref="Save"/>, reload with <see cref="Load"/>,
/// resolve bytes with <see cref="Read"/>.
/// </summary>
public sealed class AssetIndex
{
    // Indexed extensions. Models and the texture formats the client ships / we can decode.
    private static readonly string[] IndexedExtensions = [".nif", ".dds", ".tga", ".bmp"];
    private static readonly string[] ArchiveExtensions = [".mpk", ".npk"];
    // Texture extensions, in preferred resolve order. The client's zone NIFs often name a texture
    // with a .tga that was later re-saved to .dds on disk (the whole zones/Nifs surface was converted
    // to DDS), so a texture reference must resolve across these, not by exact file name.
    private static readonly string[] TextureExtensions = [".dds", ".tga", ".bmp"];

    // Bare lower-case file name -> location. Loose files win over archived ones; among archives,
    // first seen wins (the install rarely duplicates, and the per-model archive is found by name).
    private readonly Dictionary<string, AssetLocation> _byName;

    /// <summary>Root the index was built against (loose paths are absolute; archive paths too).</summary>
    public string Root { get; }
    public int Count => _byName.Count;

    private AssetIndex(string root, Dictionary<string, AssetLocation> byName)
    {
        Root = root;
        _byName = byName;
    }

    /// <summary>
    /// Walks <paramref name="clientDir"/> once, recording every indexed loose file and every indexed
    /// member of each .mpk/.npk archive. <paramref name="progress"/>, if given, is called with the
    /// archive count as they are opened (the slow part over a mounted drive).
    /// </summary>
    public static AssetIndex Build(string clientDir, Action<int>? progress = null)
    {
        if (!Directory.Exists(clientDir))
            throw new DirectoryNotFoundException($"Client directory not found: {clientDir}");
        string root = Path.GetFullPath(clientDir);
        var map = new Dictionary<string, AssetLocation>(StringComparer.Ordinal);

        // Loose files first so they win over archived copies of the same name.
        foreach (string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!IsIndexed(path))
                continue;
            string key = Key(path);
            if (!map.ContainsKey(key))
                map[key] = AssetLocation.Loose(Path.GetFullPath(path));
        }

        // Then archive members (don't overwrite a loose entry).
        int archives = 0;
        foreach (string archive in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!IsArchive(archive))
                continue;
            archives++;
            progress?.Invoke(archives);
            try
            {
                using MpkArchive mpk = MpkArchive.Open(archive);
                string abs = Path.GetFullPath(archive);
                foreach (MpkEntry e in mpk.Entries)
                {
                    if (!IsIndexed(e.Name))
                        continue;
                    string key = Key(e.Name);
                    if (!map.ContainsKey(key))
                        map[key] = AssetLocation.InArchiveAt(abs, e.Name);
                }
            }
            catch (MpkFormatException)
            {
                // Skip a malformed/non-standard archive; keep indexing the rest.
            }
        }

        return new AssetIndex(root, map);
    }

    /// <summary>Resolves <paramref name="name"/> (a bare or path-qualified file name) to its bytes,
    /// or null if not indexed. Opens the holding archive only for an archived asset.</summary>
    public byte[]? Read(string name)
    {
        if (!_byName.TryGetValue(KeyFromAny(name), out AssetLocation loc))
            return null;
        if (loc.LoosePath is not null)
            return File.Exists(loc.LoosePath) ? File.ReadAllBytes(loc.LoosePath) : null;
        if (loc.Archive is not null && loc.Member is not null && File.Exists(loc.Archive))
        {
            using MpkArchive mpk = MpkArchive.Open(loc.Archive);
            return mpk.TryGetEntry(loc.Member, out MpkEntry e) ? mpk.Extract(e) : null;
        }
        return null;
    }

    /// <summary>True if the index has an entry for this name.</summary>
    public bool Contains(string name) => _byName.ContainsKey(KeyFromAny(name));

    /// <summary>
    /// Resolves a texture reference to its bytes and the actual on-disk file name, trying the named
    /// extension first and then the other texture extensions against the same stem. Returns null if no
    /// texture with that stem is indexed. This is how a NIF's stale <c>.tga</c> reference finds the
    /// <c>.dds</c> the client actually ships (see <see cref="TextureExtensions"/>).
    /// </summary>
    public (byte[] Bytes, string FileName)? ReadTexture(string name)
    {
        // Try the exact name first (covers a correct reference and an already-bare name with ext).
        string exact = KeyFromAny(name);
        if (Path.GetExtension(exact).Length > 0 && _byName.ContainsKey(exact))
        {
            byte[]? b = Read(exact);
            if (b is not null)
                return (b, BareName(name));
        }

        // Fall back by stem across the texture extensions, preferring .dds.
        string stem = Path.GetFileNameWithoutExtension(exact);
        foreach (string ext in TextureExtensions)
        {
            string candidate = stem + ext;
            if (_byName.TryGetValue(candidate, out AssetLocation loc))
            {
                byte[]? b = Read(candidate);
                if (b is not null)
                {
                    // Name the output by the real file (e.g. the .dds), so the stem-based loader finds it.
                    string real = loc.LoosePath is not null
                        ? Path.GetFileName(loc.LoosePath)
                        : loc.Member is not null ? BareName(loc.Member) : candidate;
                    return (b, real);
                }
            }
        }
        return null;
    }

    // --- persistence (JSON under private/) ---

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var dto = new IndexDto(Root, _byName.ToDictionary(
            kv => kv.Key,
            kv => new LocationDto(kv.Value.LoosePath, kv.Value.Archive, kv.Value.Member)));
        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
    }

    public static AssetIndex Load(string path)
    {
        IndexDto dto = JsonSerializer.Deserialize<IndexDto>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidDataException($"Empty or invalid index: {path}");
        var map = dto.Entries.ToDictionary(
            kv => kv.Key,
            kv => new AssetLocation(kv.Value.LoosePath, kv.Value.Archive, kv.Value.Member),
            StringComparer.Ordinal);
        return new AssetIndex(dto.Root, map);
    }

    /// <summary>Counts of loose vs archived entries, for reporting.</summary>
    public (int Loose, int Archived) Breakdown()
    {
        int loose = 0, arch = 0;
        foreach (AssetLocation l in _byName.Values)
            if (l.InArchive) arch++; else loose++;
        return (loose, arch);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static bool IsIndexed(string path) =>
        IndexedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    private static bool IsArchive(string path) =>
        ArchiveExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string Key(string pathOrName) => BareName(pathOrName).ToLowerInvariant();
    private static string KeyFromAny(string name) => BareName(name).ToLowerInvariant();

    private static string BareName(string name)
    {
        int slash = name.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? name : name[(slash + 1)..];
    }

    // JSON DTOs (kept internal to the file; the index's public surface is AssetLocation).
    private sealed record IndexDto(string Root, Dictionary<string, LocationDto> Entries);
    private sealed record LocationDto(string? LoosePath, string? Archive, string? Member);
}
