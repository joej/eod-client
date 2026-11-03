// SPDX-License-Identifier: GPL-3.0-or-later
//
// Checks a converted zone the way a player would see it, without anyone walking it: every placement
// in zone.csv is put where the client puts it (WorldView: Godot position = (X, Z, Y) x 0.025, a turn
// of -angle about the vertical, the per-placement scale) with the bounds of its converted model, and
// compared with the terrain under it. Counts of converted objects say nothing about where they land;
// this does. Findings:
//   error    missing model, model with no geometry, object entirely under the ground
//   warning  object's origin sunk below the ground, object floating above it, outside its zone,
//            implausible size
// Thresholds are in region units (40 = 1 m) and are deliberately loose: a finding should be a real
// fault worth looking at, so a clean report means something.

using System.Globalization;
using System.Numerics;
using System.Text;

namespace DaocAssets.Zone;

/// <summary>One zone.csv row: model and placement as the client reads them.</summary>
public readonly record struct ZonePlacement(string Glb, float X, float Y, float Z, float AngleDeg, float Scale);

/// <summary>A model's bounds in its glb (metres, glTF axes: Y up), as the client draws it.</summary>
/// <param name="Sample">Up to a few thousand of the model's vertices (metres, as drawn), for pieces too
/// large to judge by their box: landscape pieces modelled in zone coordinates.</param>
public readonly record struct ModelBounds(Vector3 Min, Vector3 Max, Vector3[]? Sample = null);

public enum CheckSeverity { Warning, Error }

public sealed record ZoneFinding(CheckSeverity Severity, string Kind, ZonePlacement Placement, string Detail)
{
    /// <summary>Why this finding was reviewed and believed intended (tools/zonecheck-known.txt), or null.</summary>
    public string? Known { get; init; }
}

public sealed class ZoneCheckReport
{
    public required int Placements { get; init; }
    public required IReadOnlyList<ZoneFinding> Findings { get; init; }
    public int Errors => Findings.Count(f => f.Known is null && f.Severity == CheckSeverity.Error);
    public int Warnings => Findings.Count(f => f.Known is null && f.Severity == CheckSeverity.Warning);
    public int KnownCount => Findings.Count(f => f.Known is not null);

    public string Summary()
    {
        var open = Findings.Where(f => f.Known is null).ToList();
        return $"{Placements} placements: {Errors} error(s), {Warnings} warning(s)" +
            (open.Count == 0 ? "" : " (" + string.Join(", ",
                open.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")) + ")") +
            (KnownCount > 0 ? $"; {KnownCount} known" : "");
    }

    public string ToMarkdown(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}").AppendLine().AppendLine(Summary()).AppendLine();
        foreach (var kind in Findings.GroupBy(f => (f.Known is null ? "" : "known: ") + f.Kind)
                     .OrderBy(g => g.Key.StartsWith("known") ? 2 : g.First().Severity == CheckSeverity.Error ? 0 : 1))
        {
            sb.AppendLine($"## {kind.Key} ({kind.Count()}, {kind.First().Severity.ToString().ToLowerInvariant()})").AppendLine();
            foreach (var byModel in kind.GroupBy(f => f.Placement.Glb).OrderByDescending(g => g.Count()))
            {
                ZoneFinding first = byModel.First();
                sb.AppendLine($"- **{byModel.Key}** x{byModel.Count()}: e.g. at {first.Placement.X:0} {first.Placement.Y:0} {first.Placement.Z:0}: {first.Detail}" +
                    (first.Known is null ? "" : $" ({first.Known})"));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

public static class ZoneCheck
{
    private const float UnitsPerMetre = 40f;           // 0.025 m per region unit
    private const float SunkUnits = 2 * UnitsPerMetre; // origin more than 2 m below the ground under it
    private const float FloatUnits = 1 * UnitsPerMetre;
    private const float BuriedUnits = 1 * UnitsPerMetre;  // top more than 1 m under the ground
    private const float MaxSizeMetres = 300f, MinSizeMetres = 0.05f;

    /// <summary>Reads zone.csv ("glb,x,y,z,angle,scale"; header and malformed rows skipped).</summary>
    public static List<ZonePlacement> ReadManifest(string zoneCsv)
    {
        var list = new List<ZonePlacement>();
        foreach (string raw in zoneCsv.Split('\n'))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 6 || c[0] == "glb") continue;
            if (F(c[1], out float x) && F(c[2], out float y) && F(c[3], out float z) && F(c[4], out float a) && F(c[5], out float s))
                list.Add(new ZonePlacement(c[0], x, y, z, a, s));
        }
        return list;

        static bool F(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>
    /// Checks every placement. <paramref name="groundAt"/> gives the terrain height at a region-global
    /// (x, y), or is null for a zone without terrain (a capital city): the ground checks are skipped;
    /// <paramref name="bounds"/> gives a model's bounds (null = missing or empty);
    /// the zone spans [originX, originX + width) x [originY, originY + height) in region units.
    /// </summary>
    public static ZoneCheckReport Check(IReadOnlyList<ZonePlacement> placements, Func<float, float, float>? groundAt,
        Func<string, ModelBounds?> bounds, float originX, float originY, float width, float height)
    {
        var findings = new List<ZoneFinding>();
        foreach (ZonePlacement p in placements)
        {
            if (bounds(p.Glb) is not { } b)
            {
                findings.Add(new(CheckSeverity.Error, "missing or empty model", p, "no geometry to draw"));
                continue;
            }
            if (p.X < originX || p.Y < originY || p.X >= originX + width || p.Y >= originY + height)
                findings.Add(new(CheckSeverity.Warning, "outside its zone", p, "position is outside the zone's square"));

            // A piece at the zone's origin is modelled in zone coordinates (cliffs, harbours, castles
            // laid into the landscape): its origin and box say nothing about the ground it meets, so
            // judge it vertex by vertex against the ground directly beneath each one.
            if (p.X == originX && p.Y == originY && b.Sample is { Length: > 0 } sample)
            {
                if (groundAt is not null)
                    CheckZoneSpacePiece(p, sample, groundAt, originX, originY, width, height, findings);
                continue;
            }

            Vector3 size = (b.Max - b.Min) * p.Scale;
            float largest = Math.Max(size.X, Math.Max(size.Y, size.Z));
            if (largest > MaxSizeMetres || largest < MinSizeMetres)
                findings.Add(new(CheckSeverity.Warning, "implausible size", p, $"{size.X:0.##} x {size.Y:0.##} x {size.Z:0.##} m"));

            // The model's box placed as the client places it: region offsets of its 8 corners.
            var corners = new List<Vector3>(8);
            float th = -p.AngleDeg * MathF.PI / 180f, cos = MathF.Cos(th), sin = MathF.Sin(th);
            foreach (float mx in new[] { b.Min.X, b.Max.X })
            foreach (float my in new[] { b.Min.Y, b.Max.Y })
            foreach (float mz in new[] { b.Min.Z, b.Max.Z })
            {
                float gx = (mx * cos + mz * sin) * p.Scale, gz = (-mx * sin + mz * cos) * p.Scale, gy = my * p.Scale;
                corners.Add(new Vector3(gx, gz, gy) * UnitsPerMetre);   // region (dX, dY, dZ)
            }
            float bottom = p.Z + corners.Min(c => c.Z), top = p.Z + corners.Max(c => c.Z);

            if (groundAt is null)
                continue;   // no terrain: nothing to stand on or sink into

            // The ground under the object: its centre and the four corners of its footprint.
            float x0 = corners.Min(c => c.X), x1 = corners.Max(c => c.X), y0 = corners.Min(c => c.Y), y1 = corners.Max(c => c.Y);
            float[] ground =
            [
                groundAt(p.X, p.Y), groundAt(p.X + x0, p.Y + y0), groundAt(p.X + x1, p.Y + y0),
                groundAt(p.X + x0, p.Y + y1), groundAt(p.X + x1, p.Y + y1),
            ];
            float gMin = ground.Min(), gMax = ground.Max(), gCentre = ground[0];

            if (top < gCentre - BuriedUnits)
                findings.Add(new(CheckSeverity.Error, "under the ground", p,
                    $"top {(gCentre - top) / UnitsPerMetre:0.#} m below the ground"));
            else if (p.Z < gCentre - SunkUnits)
                // The origin, not the geometry: DAoC trees reach far below their origin on purpose
                // (BigHibTree 32 m, elm1 6 m) so they stay planted on slopes. What placement can get
                // wrong is where the origin is.
                findings.Add(new(CheckSeverity.Warning, "sunk into the ground", p,
                    $"origin {(gCentre - p.Z) / UnitsPerMetre:0.#} m below the ground under it (model {(top - bottom) / UnitsPerMetre:0.#} m tall)"));
            else if (bottom > gMax + FloatUnits)
                findings.Add(new(CheckSeverity.Warning, "floating", p,
                    $"base {(bottom - gMax) / UnitsPerMetre:0.#} m above the highest ground under it"));
        }
        return new ZoneCheckReport { Placements = placements.Count, Findings = findings };
    }

    // A landscape piece should meet the terrain: floating if every vertex is above the ground under
    // it, under the ground if every vertex is below. Vertices outside the zone are skipped.
    private static void CheckZoneSpacePiece(ZonePlacement p, Vector3[] sample, Func<float, float, float> groundAt,
        float originX, float originY, float width, float height, List<ZoneFinding> findings)
    {
        float th = -p.AngleDeg * MathF.PI / 180f, cos = MathF.Cos(th), sin = MathF.Sin(th);
        float lowest = float.MaxValue, highest = float.MinValue;
        foreach (Vector3 v in sample)
        {
            float x = p.X + (v.X * cos + v.Z * sin) * p.Scale * UnitsPerMetre;
            float y = p.Y + (-v.X * sin + v.Z * cos) * p.Scale * UnitsPerMetre;
            if (x < originX || y < originY || x >= originX + width || y >= originY + height)
                continue;
            float dz = p.Z + v.Y * p.Scale * UnitsPerMetre - groundAt(x, y);
            lowest = Math.Min(lowest, dz);
            highest = Math.Max(highest, dz);
        }
        if (lowest == float.MaxValue)
            findings.Add(new(CheckSeverity.Warning, "outside its zone", p, "no vertex over the zone's ground"));
        else if (lowest > FloatUnits)
            findings.Add(new(CheckSeverity.Warning, "floating", p,
                $"every vertex above the ground beneath it, the lowest by {lowest / UnitsPerMetre:0.#} m (a zone-coordinate piece)"));
        else if (highest < -BuriedUnits)
            findings.Add(new(CheckSeverity.Error, "under the ground", p,
                $"every vertex below the ground above it, the highest by {-highest / UnitsPerMetre:0.#} m (a zone-coordinate piece)"));
    }

    /// <summary>
    /// Reads the reviewed-findings list: lines "model.glb | kind | reason" ('#' comments). A finding
    /// whose model and kind match is reported as known, not counted, so a clean check means "nothing
    /// new" rather than "nothing we haven't got used to".
    /// </summary>
    public static Dictionary<(string Glb, string Kind), string> ReadKnown(string text)
    {
        var known = new Dictionary<(string, string), string>(new KnownComparer());
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Split('#')[0].Trim();
            string[] f = line.Split('|', StringSplitOptions.TrimEntries);
            if (f.Length == 3 && f[0].Length > 0)
                known[(f[0], f[1])] = f[2];
        }
        return known;
    }

    private sealed class KnownComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) a, (string, string) b) =>
            string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && a.Item2 == b.Item2;
        public int GetHashCode((string, string) k) => HashCode.Combine(k.Item1.ToLowerInvariant(), k.Item2);
    }

    /// <summary>Marks findings that match the reviewed list.</summary>
    public static ZoneCheckReport ApplyKnown(ZoneCheckReport report, IReadOnlyDictionary<(string Glb, string Kind), string> known) =>
        new()
        {
            Placements = report.Placements,
            Findings = report.Findings.Select(f => known.TryGetValue((f.Placement.Glb, f.Kind), out string? why) ? f with { Known = why } : f).ToList(),
        };

    /// <summary>A converted model's bounds as the client draws it: every triangle evaluated through
    /// the glb's node transforms and skins (SharpGLTF), in metres. Null if the glb has no geometry.</summary>
    public static ModelBounds? GlbBounds(string glbPath)
    {
        var model = SharpGLTF.Schema2.ModelRoot.Load(glbPath);
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        var all = new List<Vector3>();
        foreach (var (a, b, c, _) in SharpGLTF.Schema2.Toolkit.EvaluateTriangles(model.DefaultScene))
            foreach (Vector3 v in new[] { a.GetGeometry().GetPosition(), b.GetGeometry().GetPosition(), c.GetGeometry().GetPosition() })
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
                all.Add(v);
            }
        if (all.Count == 0)
            return null;
        // An even spread of at most 4,096 vertices (every vertex appears once per triangle using it).
        int stride = Math.Max(1, all.Count / 4096);
        Vector3[] sample = all.Where((_, i) => i % stride == 0).ToArray();
        return new ModelBounds(min, max, sample);
    }
}
