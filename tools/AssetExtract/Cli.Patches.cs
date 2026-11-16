// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk patches: every model in the install that has patch files (<name>_p00.nif, … beside it; NifFile.Patches), and what
// each patch does to it: which node it merges into, which of that node's children it replaces and which it adds. The
// report describes the owner's client files: write it under private/.

using System.Text;
using DaocAssets.Library;
using DaocAssets.Nif;

internal static partial class Cli
{
    private static int PatchesCmd(string[] args)
    {
        if (Options.Parse(args, 1, ["--index", "--out"]) is not { } o || o["--index"] is not string indexPath)
            return Usage();
        AssetIndex index = AssetIndex.Load(indexPath);
        var models = index.Names
            .Where(n => n.EndsWith("_p00.nif", StringComparison.Ordinal))
            .Select(n => n[..^"_p00.nif".Length] + ".nif")
            .Order(StringComparer.Ordinal)
            .ToList();
        var sb = new StringBuilder();
        var kinds = new Dictionary<string, int>();
        int patches = 0, replaced = 0, added = 0, missingTargets = 0, noBase = 0, unreadableBase = 0;
        sb.AppendLine("| Model | Patch | Kind | Into | Replaces | Adds |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (string model in models)
        {
            if (index.Read(model, null) is not byte[] bytes)
            {
                noBase++;
                sb.AppendLine($"| {model} | | no base model | | | |");
                continue;
            }
            IReadOnlyList<byte[]> files = index.ReadPatches(model, null);
            IReadOnlyList<NifPatchEffect> effects;
            try { effects = NifFile.DescribePatches(bytes, files); }
            catch (NifFormatException e)
            {
                unreadableBase++;
                sb.AppendLine($"| {model} | | base unreadable: {e.Message} | | | |");
                continue;
            }
            for (int n = 0; n < effects.Count; n++)
            {
                NifPatchEffect e = effects[n];
                patches++;
                string kind = e.Kind.StartsWith("__patch_", StringComparison.OrdinalIgnoreCase) ? "__patch_" : e.Kind;
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                replaced += e.Replaced.Count;
                added += e.Added.Count;
                missingTargets += e.Targets.Count(t => t.EndsWith("(missing)", StringComparison.Ordinal));
                sb.AppendLine($"| {model} | _p{n:00} | {e.Kind}{(e.Error is null ? "" : ": " + e.Error)} | {string.Join("; ", e.Targets)} | " +
                    $"{string.Join(", ", e.Replaced)} | {string.Join(", ", e.Added)} |");
            }
        }
        string summary =
            $"{models.Count} models with a _p00 ({noBase} without the base model, {unreadableBase} unreadable), {patches} patches applied: " +
            string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}")) +
            $"; {replaced} children replaced, {added} added; {missingTargets} targets missing from the base.";
        string report = "# Model patch files\n\n" + summary + "\n\n" + sb;
        if (o["--out"] is string outPath)
        {
            File.WriteAllText(outPath, report);
            Console.WriteLine(summary);
        }
        else
            Console.Write(report);
        return 0;
    }
}
