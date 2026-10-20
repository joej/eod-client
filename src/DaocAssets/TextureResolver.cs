// SPDX-License-Identifier: GPL-3.0-or-later
//
// Gathers the texture files a model references into one folder, from a legitimate owner's own
// client install. The .dds our models name are either loose on disk (items, zones, effects) or
// packed in an .mpk (creature skins live in figures/skins/*.mpk). This resolves either case:
// loose file first, then the archives. Owner-run tooling — the output is the client's content and
// never belongs in this repo or a release (CLAUDE.md, "Game client and assets").

using DaocAssets.Mpk;
using DaocAssets.Nif;

namespace DaocAssets;

/// <summary>The outcome of resolving one texture name.</summary>
public enum TextureSource
{
    /// <summary>Found loose on disk and copied.</summary>
    Loose,
    /// <summary>Found inside an .mpk archive and extracted.</summary>
    Archive,
    /// <summary>Not found anywhere under the client directory.</summary>
    Missing,
}

/// <summary>One resolved texture: its name, where it came from, and (when found) the archive path.</summary>
public readonly record struct TextureResult(string Name, TextureSource Source, string? FoundIn);

/// <summary>
/// Resolves model texture names to actual texture files under a client directory, copying each into
/// an output folder. Loose files are preferred; .mpk archives are scanned only when needed.
/// </summary>
public sealed class TextureResolver
{
    private readonly string _clientDir;
    // Lazily built index of loose texture files (lower-case file name -> full path), and the list of
    // .mpk archives, so repeated resolves don't re-walk the tree.
    private Dictionary<string, string>? _looseByName;
    private List<string>? _archives;

    public TextureResolver(string clientDir)
    {
        if (!Directory.Exists(clientDir))
            throw new DirectoryNotFoundException($"Client directory not found: {clientDir}");
        _clientDir = clientDir;
    }

    /// <summary>Texture file names referenced by a .nif model (deduplicated, case-insensitive).</summary>
    public static IReadOnlyList<string> TextureNames(string nifPath)
    {
        NifModel model = NifFile.Read(File.ReadAllBytes(nifPath));
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NifMesh mesh in model.Meshes)
            if (!string.IsNullOrEmpty(mesh.TextureFile) && seen.Add(mesh.TextureFile))
                names.Add(mesh.TextureFile);
        return names;
    }

    /// <summary>
    /// Resolves <paramref name="textureName"/> (a bare file name, e.g. <c>foo.dds</c>) to a file in
    /// <paramref name="outDir"/>, copying it from a loose file or extracting it from an archive.
    /// Does nothing and reports <see cref="TextureSource.Missing"/> when it can't be found.
    /// </summary>
    public TextureResult Resolve(string textureName, string outDir)
    {
        string bare = BareName(textureName);
        Directory.CreateDirectory(outDir);
        string dest = Path.Combine(outDir, bare);

        if (LooseByName.TryGetValue(bare.ToLowerInvariant(), out string? loose))
        {
            File.Copy(loose, dest, overwrite: true);
            return new TextureResult(bare, TextureSource.Loose, loose);
        }

        foreach (string archive in Archives)
        {
            try
            {
                using MpkArchive mpk = MpkArchive.Open(archive);
                MpkEntry? match = null;
                foreach (MpkEntry e in mpk.Entries)
                    if (string.Equals(BareName(e.Name), bare, StringComparison.OrdinalIgnoreCase))
                    {
                        match = e;
                        break;
                    }
                if (match is { } entry)
                {
                    File.WriteAllBytes(dest, mpk.Extract(entry));
                    return new TextureResult(bare, TextureSource.Archive, archive);
                }
            }
            catch (MpkFormatException)
            {
                // A malformed or non-standard archive: skip it, keep scanning the rest.
            }
        }

        return new TextureResult(bare, TextureSource.Missing, null);
    }

    // The file name only, stripping both Windows and Unix separators. Texture and member names are
    // authored on Windows, so they can contain backslashes regardless of the host OS running this.
    private static string BareName(string name)
    {
        int slash = name.LastIndexOfAny(['/', '\\']);
        return slash < 0 ? name : name[(slash + 1)..];
    }

    private Dictionary<string, string> LooseByName => _looseByName ??= BuildLooseIndex();
    private List<string> Archives => _archives ??=
        Directory.EnumerateFiles(_clientDir, "*.mpk", SearchOption.AllDirectories).ToList();

    private Dictionary<string, string> BuildLooseIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string ext in new[] { "*.dds", "*.tga", "*.bmp" })
            foreach (string path in Directory.EnumerateFiles(_clientDir, ext, SearchOption.AllDirectories))
                map.TryAdd(Path.GetFileName(path).ToLowerInvariant(), path); // first wins
        return map;
    }
}
