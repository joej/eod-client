// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk collision: how many of the models the zones place carry collision volumes ("collidee", "collider",
// "collision" nodes: NifMesh.Collision), measured before the client is taught to collide with them (roadmap M9,
// solid world).

using DaocAssets.Library;
using DaocAssets.Nif;

internal static partial class Cli
{
    // collision <zones-dir> --index INDEX [--out REPORT.md]: every model the zones' zone.csv place, read from the
    // install, with its collision shapes and triangles; the most-placed models without any listed for review.
    private static int CollisionCmd(string[] args)
    {
        string zonesDir = args[1];
        if (Options.Parse(args, 2, ["--index", "--out"]) is not { } o || o["--index"] is not string indexPath)
            return Usage();
        AssetIndex index = AssetIndex.Load(indexPath);

        // Placements per model, across all zones.
        var placed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string csv in Directory.EnumerateFiles(zonesDir, "zone.csv", SearchOption.AllDirectories))
            foreach (string line in File.ReadLines(csv).Skip(1))
            {
                string glb = line.Split(',')[0];
                string stem = Path.GetFileNameWithoutExtension(glb[(glb.LastIndexOf('/') + 1)..]);
                if (stem.Length > 0)
                    placed[stem] = placed.GetValueOrDefault(stem) + 1;
            }

        var rows = new List<(string Model, int Placed, int Shapes, int Tris, int CollShapes, int CollTris, int Textured, string Nodes)>();
        int unreadable = 0;
        foreach ((string model, int count) in placed)
        {
            byte[]? bytes = index.Read(model + ".nif");
            if (bytes is null) { unreadable++; continue; }
            NifModel nif;
            try { nif = NifFile.Read(bytes); }
            catch (Exception e) when (e is NifFormatException or InvalidDataException or IndexOutOfRangeException) { unreadable++; continue; }
            var coll = nif.Meshes.Where(m => m.Collision).ToList();
            var drawn = nif.Meshes.Where(m => !m.Helper).ToList();
            rows.Add((model, count, drawn.Count, drawn.Sum(m => m.Triangles.Length), coll.Count, coll.Sum(m => m.Triangles.Length),
                coll.Count(m => !m.Helper), string.Join(" ", coll.Select(m => m.Name).Distinct().Take(4))));
        }

        int with = rows.Count(r => r.CollShapes > 0);
        long placements = rows.Sum(r => (long)r.Placed), placementsWith = rows.Where(r => r.CollShapes > 0).Sum(r => (long)r.Placed);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Collision volumes in the zones' models").AppendLine();
        sb.AppendLine($"{rows.Count} models placed {placements} times ({unreadable} not read). " +
                      $"**{with} models ({100.0 * with / Math.Max(1, rows.Count):0}%) carry collision shapes**, covering " +
                      $"{placementsWith} placements ({100.0 * placementsWith / Math.Max(1, placements):0}%); " +
                      $"{rows.Sum(r => r.CollTris)} collision triangles against {rows.Sum(r => r.Tris)} drawn. " +
                      $"{rows.Count(r => r.Textured > 0)} models have a drawn (textured) shape under a collision node.").AppendLine();
        sb.AppendLine("## With collision, by placements").AppendLine();
        sb.AppendLine("| Model | Placed | Drawn shapes / tris | Collision shapes / tris | Node names |").AppendLine("|---|---:|---|---|---|");
        foreach (var r in rows.Where(r => r.CollShapes > 0).OrderByDescending(r => r.Placed).Take(60))
            sb.AppendLine($"| {r.Model} | {r.Placed} | {r.Shapes} / {r.Tris} | {r.CollShapes} / {r.CollTris} | {r.Nodes} |");
        sb.AppendLine().AppendLine("## Without, by placements (what would stay walk-through)").AppendLine();
        sb.AppendLine("| Model | Placed | Drawn shapes / tris |").AppendLine("|---|---:|---|");
        foreach (var r in rows.Where(r => r.CollShapes == 0).OrderByDescending(r => r.Placed).Take(80))
            sb.AppendLine($"| {r.Model} | {r.Placed} | {r.Shapes} / {r.Tris} |");

        string report = sb.ToString();
        if (o["--out"] is string outPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, report);
        }
        Console.WriteLine(report.Split('\n')[2]);
        return 0;
    }
}
