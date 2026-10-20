// SPDX-License-Identifier: GPL-3.0-or-later
//
// Finds a model file by name under a client install and returns its bytes: loose on disk first,
// then from any .mpk/.npk archive (zone fixture models are packed one-per-archive, e.g.
// zones/Nifs/keep.npk). The model equivalent of TextureResolver. Owner-run; reads the owner's
// install, output never enters the repo (see CLAUDE.md, "Game client and assets").

using DaocAssets.Mpk;

namespace DaocAssets.Zone;

/// <summary>Resolves a NIF file name to its bytes from a client directory, loose or packed.</summary>
public sealed class ModelLocator
{
    private readonly string _clientDir;
    private Dictionary<string, string>? _looseByName;
    private Dictionary<string, string>? _archivesByStem;

    public ModelLocator(string clientDir)
    {
        if (!Directory.Exists(clientDir))
            throw new DirectoryNotFoundException($"Client directory not found: {clientDir}");
        _clientDir = clientDir;
    }

    /// <summary>Returns the bytes of model <paramref name="nifName"/> (a bare file name), or null if
    /// not found. Order: loose file; then the archive named after the model (zone fixture models are
    /// packed one-per-archive, e.g. keep.nif in keep.npk) — an O(1) lookup by name; then, only if
    /// that misses, a full scan of every archive's member list. Indices are cached across calls.</summary>
    public byte[]? Read(string nifName)
    {
        string bare = BareName(nifName);
        string lower = bare.ToLowerInvariant();

        if (LooseByName.TryGetValue(lower, out string? loose))
            return File.ReadAllBytes(loose);

        // Fast path: an archive whose own name matches the model stem (keep.nif -> keep.npk/.mpk).
        string stem = Path.GetFileNameWithoutExtension(bare).ToLowerInvariant();
        if (ArchivesByStem.TryGetValue(stem, out string? namedArchive)
            && TryReadMember(namedArchive, bare, out byte[]? bytes))
            return bytes;

        // Slow path: scan every archive's members. Rare — only when the name convention doesn't hold.
        foreach (string archive in ArchivesByStem.Values)
        {
            if (archive == namedArchive)
                continue;
            if (TryReadMember(archive, bare, out byte[]? found))
                return found;
        }
        return null;
    }

    private static bool TryReadMember(string archive, string memberBareName, out byte[]? bytes)
    {
        bytes = null;
        try
        {
            using MpkArchive mpk = MpkArchive.Open(archive);
            foreach (MpkEntry e in mpk.Entries)
                if (string.Equals(BareName(e.Name), memberBareName, StringComparison.OrdinalIgnoreCase))
                {
                    bytes = mpk.Extract(e);
                    return true;
                }
        }
        catch (MpkFormatException)
        {
            // Skip a malformed/non-standard archive.
        }
        return false;
    }

    private static string BareName(string name)
    {
        int slash = name.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? name : name[(slash + 1)..];
    }

    private Dictionary<string, string> LooseByName => _looseByName ??= BuildLooseIndex();
    private Dictionary<string, string> ArchivesByStem => _archivesByStem ??= BuildArchiveIndex();

    private Dictionary<string, string> BuildLooseIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(_clientDir, "*.nif", SearchOption.AllDirectories))
            map.TryAdd(Path.GetFileName(path).ToLowerInvariant(), path); // first wins
        return map;
    }

    // Index archives by their file-name stem (keep.npk -> "keep"), so a model can be found by the
    // one-archive-per-model naming convention without opening every archive.
    private Dictionary<string, string> BuildArchiveIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(_clientDir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".mpk" or ".npk")
                map.TryAdd(Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), path); // first wins
        }
        return map;
    }
}
