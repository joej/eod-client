// SPDX-License-Identifier: GPL-3.0-or-later
//
// Aggregates NifSurvey results over many files into the coverage report `mpk audit` writes: which
// block types and rendering features occur, how often and where, how the converter treats each one,
// and whether our byte-level decode of each type held on every block.

using System.Text;

namespace DaocAssets.Nif;

public sealed class NifAudit
{
    private sealed class TypeStats
    {
        public int Blocks, Exact, Failed, Splits;
        public readonly HashSet<string> Files = [];
        public readonly Dictionary<string, int> FilesByFolder = [];
        public readonly HashSet<string> Families = [];
        public readonly List<string> FailedExamples = [];
    }

    private readonly Dictionary<string, TypeStats> _types = [];
    private readonly Dictionary<string, (int Blocks, HashSet<string> Files)> _features = [];
    private readonly Dictionary<string, int> _versions = [];
    private readonly Dictionary<string, int> _folders = [];
    private readonly Dictionary<(string Folder, string Family), int> _folderFamilies = [];
    private int _falseNameFiles;
    private readonly List<string> _boundaryMismatches = [];
    private readonly List<(string Source, string Error)> _unreadable = [];
    // 10.1 sequential decodes: per folder (complete, stopped, failed); stops and failures by type.
    private readonly Dictionary<string, (int Complete, int Stopped, int Failed)> _seqByFolder = [];
    private readonly Dictionary<string, int> _stoppedBy = [];
    private readonly Dictionary<string, List<string>> _failedBy = [];
    private readonly Dictionary<string, int> _failedCount = [];

    public int Files { get; private set; }
    public int Unreadable => _unreadable.Count;
    public int BoundaryMismatches => _boundaryMismatches.Count;

    /// <summary>Records one surveyed file. <paramref name="folder"/> groups the report (e.g.
    /// "zones"); <paramref name="source"/> identifies the file in examples.</summary>
    public void Add(string folder, string source, NifSurvey s)
    {
        Files++;
        Bump(_versions, s.Version.ToString());
        Bump(_folders, folder);
        var ff = (folder, Family(s.Version));
        _folderFamilies[ff] = _folderFamilies.TryGetValue(ff, out int fn) ? fn + 1 : 1;
        if (s.FalseTypeNames.Count > 0)
            _falseNameFiles++;
        if (!s.BoundariesMatchHeader)
            _boundaryMismatches.Add($"{source} (header {s.DeclaredBlocks} blocks, scan finds {s.ScannedBlocks}" +
                (s.FalseTypeNames.Count > 0 ? $"; false type names: {string.Join(", ", s.FalseTypeNames.Distinct())}" : "") + ")");

        if (s.Sequential != NifSurvey.SequentialDecode.NotApplicable)
        {
            var e = _seqByFolder.GetValueOrDefault(folder);
            _seqByFolder[folder] = s.Sequential switch
            {
                NifSurvey.SequentialDecode.Complete => (e.Complete + 1, e.Stopped, e.Failed),
                NifSurvey.SequentialDecode.Stopped => (e.Complete, e.Stopped + 1, e.Failed),
                _ => (e.Complete, e.Stopped, e.Failed + 1),
            };
            if (s.Sequential == NifSurvey.SequentialDecode.Stopped)
                Bump(_stoppedBy, s.StopType!);
            if (s.Sequential == NifSurvey.SequentialDecode.Failed)
            {
                Bump(_failedCount, s.StopType!);
                if (!_failedBy.TryGetValue(s.StopType!, out var list))
                    _failedBy[s.StopType!] = list = [];
                if (list.Count < 5)
                    list.Add($"{source} ({s.StopReason})");
            }
        }

        string family = Family(s.Version);
        foreach (string type in s.BlockTypes)
        {
            TypeStats t = Stats(type);
            t.Blocks++;
            t.Families.Add(family);
            if (t.Files.Add(source))
                Bump(t.FilesByFolder, folder);
        }
        foreach ((string type, int n) in s.ExactDecodes)
            Stats(type).Exact += n;
        foreach ((string type, int n) in s.ScannerSplits)
            Stats(type).Splits += n;
        foreach ((string type, int n) in s.FailedDecodes)
        {
            TypeStats t = Stats(type);
            t.Failed += n;
            if (t.FailedExamples.Count < 5)
                t.FailedExamples.Add(source);
        }
        foreach ((string feature, int n) in s.Features)
        {
            if (!_features.TryGetValue(feature, out var entry))
                _features[feature] = entry = (0, []);
            entry.Files.Add(source);
            _features[feature] = (entry.Blocks + n, entry.Files);
        }
    }

    /// <summary>Records a file the survey could not read at all.</summary>
    public void AddUnreadable(string folder, string source, string error)
    {
        Files++;
        Bump(_folders, folder);
        _unreadable.Add((source, error));
    }

    // Groups unreadable files: SpeedTree data saved under a .nif name, and NIF errors by message.
    private static string Cause(string error) =>
        error.Contains("__IdvSpt", StringComparison.Ordinal) ? "SpeedTree file (IDV), not a NIF"
        : error.Split('(', ':')[0].Trim();

    private TypeStats Stats(string type)
    {
        if (!_types.TryGetValue(type, out TypeStats? t))
            _types[type] = t = new TypeStats();
        return t;
    }

    private static string Family(NifVersion v) =>
        v.A < 4 ? "2.x/3.x" : v >= new NifVersion(10, 1, 0, 0) ? "10.1" : "4.x";

    private static void Bump(Dictionary<string, int> d, string key) =>
        d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;

    /// <summary>The report as Markdown.</summary>
    public string ToMarkdown(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine();
        sb.AppendLine($"{Files} NIF file(s) surveyed; {Unreadable} unreadable; " +
            $"{BoundaryMismatches} 4.x file(s) where the block scan disagrees with the header count.");
        sb.AppendLine();
        sb.AppendLine("Folders: " + string.Join(", ", _folders.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        sb.AppendLine();
        sb.AppendLine("Versions: " + string.Join(", ", _versions.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        sb.AppendLine();
        sb.AppendLine("| Folder | 4.x | 10.1 | 2.x/3.x |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (string folder in _folders.OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
            sb.AppendLine($"| {folder} | {_folderFamilies.GetValueOrDefault((folder, "4.x"))} | " +
                $"{_folderFamilies.GetValueOrDefault((folder, "10.1"))} | {_folderFamilies.GetValueOrDefault((folder, "2.x/3.x"))} |");
        sb.AppendLine();

        sb.AppendLine("## Block types").AppendLine();
        sb.AppendLine("Coverage is how `NifFile.Read` (the converter) uses the type, per format family. " +
            "\"Decoded\" is the byte-level check: blocks whose full decode ended exactly at the next block, " +
            "plus (after +) blocks that decoded exactly to a later real boundary because the block scanner " +
            "had split them at a false \"Ni…\" string (4.x only). 10.1 blocks count as exact only when their " +
            "whole file decoded in sequence to the footer (see Integrity); 2.x/3.x bodies aren't decoded.").AppendLine();
        sb.AppendLine("| Type | Coverage (by format) | Files | Blocks | Formats | Decoded exactly | Where (files) | Converter drops |");
        sb.AppendLine("|---|---|---:|---:|---|---|---|---|");
        var rows = _types.Select(kv => (Type: kv.Key, S: kv.Value, Cov: CoverageOf(kv.Key, kv.Value)))
            .OrderBy(r => r.Cov.Rank).ThenByDescending(r => r.S.Files.Count);
        foreach (var (type, s, cov) in rows)
        {
            int total = s.Exact + s.Failed + s.Splits;
            string decoded = total == 0 ? "-" : $"{s.Exact}{(s.Splits > 0 ? $"+{s.Splits}" : "")}/{total}";
            string where = string.Join(", ", s.FilesByFolder.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} {kv.Value}"));
            sb.AppendLine($"| {type} | {cov.Label} | {s.Files.Count} | {s.Blocks} | {string.Join("+", s.Families.Order())} | {decoded} | {where} | {cov.Note} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Rendering features").AppendLine();
        sb.AppendLine("4.x features are unprefixed; [10.1] features come only from files that decoded completely.").AppendLine();
        sb.AppendLine("| Feature | Blocks | Files |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var (feature, (blocks, files)) in _features.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"| {feature} | {blocks} | {files.Count} |");
        sb.AppendLine();

        sb.AppendLine("## Integrity").AppendLine();
        var failedTypes = _types.Where(kv => kv.Value.Failed > 0).OrderByDescending(kv => kv.Value.Failed).ToList();
        int splitBlocks = _types.Values.Sum(t => t.Splits);
        if (failedTypes.Count == 0)
            sb.AppendLine("Every decoded block ended exactly at a real block boundary.");
        if (splitBlocks > 0)
            sb.AppendLine($"{splitBlocks} decoded block(s) ran past the scanner's next block to a real boundary: " +
                "the scanner split them at a false \"Ni…\" string inside the body.");
        foreach (var (type, s) in failedTypes)
            sb.AppendLine($"- **{type}**: {s.Failed} block(s) did not decode exactly, e.g. {string.Join("; ", s.FailedExamples)}");
        sb.AppendLine();
        if (_seqByFolder.Count > 0)
        {
            sb.AppendLine("Gamebryo 10.1 files, decoded block by block to the footer:").AppendLine();
            sb.AppendLine("| Folder | Complete | Stopped at an undecoded type | Failed |");
            sb.AppendLine("|---|---:|---:|---:|");
            foreach (var (folder, (complete, stopped, failedN)) in _seqByFolder.OrderByDescending(kv => kv.Value.Complete + kv.Value.Stopped + kv.Value.Failed))
                sb.AppendLine($"| {folder} | {complete} | {stopped} | {failedN} |");
            sb.AppendLine();
            if (_stoppedBy.Count > 0)
                sb.AppendLine("Stopped at (files): " + string.Join(", ",
                    _stoppedBy.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))).AppendLine();
            foreach (var (type, n) in _failedCount.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"- **10.1 {type}**: {n} file(s) failed, e.g. {string.Join("; ", _failedBy[type])}");
            if (_failedCount.Count > 0)
                sb.AppendLine();
        }
        if (_boundaryMismatches.Count > 0)
        {
            sb.AppendLine($"Block-scan mismatches ({_boundaryMismatches.Count}; {_falseNameFiles} with an impossible " +
                "type name such as \"NiMAXPSysConverter flag\", the rest with an identifier-like false match), first 20:");
            foreach (string m in _boundaryMismatches.Take(20))
                sb.AppendLine($"- {m}");
            sb.AppendLine();
        }
        if (_unreadable.Count > 0)
        {
            sb.AppendLine($"Unreadable ({_unreadable.Count}), by cause:");
            foreach (var g in _unreadable.GroupBy(u => Cause(u.Error)).OrderByDescending(g => g.Count()))
                sb.AppendLine($"- **{g.Key}** ({g.Count()}): {string.Join(", ", g.Take(8).Select(u => u.Source))}" +
                    (g.Count() > 8 ? ", …" : ""));
        }
        return sb.ToString();
    }

    // A type can occur in several format families with different coverage: list each, and rank the
    // row by the weakest so the gaps sort first.
    private static (int Rank, string Label, string Note) CoverageOf(string type, TypeStats s)
    {
        int rank = 3;
        var labels = new List<string>();
        var notes = new List<string>();
        foreach (string family in s.Families.Order())
        {
            NifVersion v = family switch
            {
                "10.1" => new NifVersion(10, 1, 0, 0),
                "2.x/3.x" => new NifVersion(3, 0, 0, 0),
                _ => new NifVersion(4, 2, 2, 0),
            };
            NifCoverage c = NifSurvey.Coverage(type, v);
            rank = Math.Min(rank, c switch { NifCoverage.Unhandled => 0, NifCoverage.Partial => 1, _ => 2 });
            labels.Add($"{family} {c}");
            string note = NifSurvey.CoverageNote(type, v);
            if (note.Length > 0)
                notes.Add(s.Families.Count > 1 ? $"{family}: {note}" : note);
        }
        return (rank, string.Join(", ", labels), string.Join("; ", notes));
    }

}
