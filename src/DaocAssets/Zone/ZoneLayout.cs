// SPDX-License-Identifier: GPL-3.0-or-later
//
// Reads a DAoC zone's static-object placement from the client's own CSV data (fixtures.csv +
// nifs.csv, inside the zone's dat<NNN>.mpk). fixtures.csv lists each placed object as a NIF index
// plus position/angle/scale in zone-local coordinates; nifs.csv maps the index to a model file
// name. We join them and convert positions to region-global coordinates using the zone's offset
// (from the OpenDAoC zone table), so a fixture lands in the same space as a player position packet.
//
// Owner-run: the CSVs and models come from a legitimate owner's install; output never enters the
// repo or a release (see CLAUDE.md, "Game client and assets"). The format is plain CSV — no
// reverse engineering — shipped by the client for its own world building (headers name the fields).

using System.Globalization;

namespace DaocAssets.Zone;

/// <summary>One placed static object: which model, and where it sits in region-global coordinates.</summary>
public readonly record struct ZoneFixture(
    string NifName,     // model file name, e.g. "keep.nif"
    string Label,       // textual name from the CSV, e.g. "Keep"
    float X, float Y, float Z,   // region-global position (zone offset already applied)
    int AngleDeg,       // rotation about the vertical axis, 0..359 degrees
    float Scale);       // per-cent in the CSV; stored as a ratio (100 -> 1.0)

/// <summary>A zone's static-object layout: the fixtures and the distinct models they reference.</summary>
public sealed class ZoneLayout
{
    public required IReadOnlyList<ZoneFixture> Fixtures { get; init; }

    /// <summary>Distinct model file names referenced by the fixtures (case-insensitive).</summary>
    public IReadOnlyList<string> Models
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (ZoneFixture f in Fixtures)
                if (seen.Add(f.NifName))
                    list.Add(f.NifName);
            return list;
        }
    }

    /// <summary>
    /// Parses a zone layout from the two CSV texts, applying the zone's region-coordinate offset
    /// (<paramref name="offsetX"/>, <paramref name="offsetY"/> in region units) to each fixture so
    /// positions are region-global. Rows that don't parse are skipped (the files have header and
    /// occasionally blank/partial lines).
    /// </summary>
    public static ZoneLayout Parse(string nifsCsv, string fixturesCsv, int offsetX, int offsetY)
    {
        Dictionary<int, (string Name, string Label)> models = ParseNifs(nifsCsv);
        var fixtures = new List<ZoneFixture>();

        foreach (string[] cols in Rows(fixturesCsv))
        {
            // fixtures.csv columns: ID, NIF #, Textual Name, X, Y, Z, A, Scale, ...
            if (cols.Length < 8
                || !int.TryParse(cols[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nifIndex)
                || !TryFloat(cols[3], out float x)
                || !TryFloat(cols[4], out float y)
                || !TryFloat(cols[5], out float z))
                continue;
            if (!models.TryGetValue(nifIndex, out var model))
                continue; // a fixture whose NIF index isn't in nifs.csv (ignore it)

            int.TryParse(cols[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int angle);
            float scale = TryFloat(cols[7], out float s) && s > 0 ? s / 100f : 1f;

            fixtures.Add(new ZoneFixture(
                model.Name, model.Label,
                x + offsetX, y + offsetY, z,
                ((angle % 360) + 360) % 360,
                scale));
        }

        return new ZoneLayout { Fixtures = fixtures };
    }

    // nifs.csv columns: NIF (index), Textual Name, Filename, ... We keep index -> (filename, label).
    private static Dictionary<int, (string, string)> ParseNifs(string nifsCsv)
    {
        var map = new Dictionary<int, (string, string)>();
        foreach (string[] cols in Rows(nifsCsv))
        {
            if (cols.Length < 3
                || !int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                continue;
            string label = cols[1].Trim();
            string file = cols[2].Trim();
            if (file.Length == 0)
                continue;
            map[index] = (file, label);
        }
        return map;
    }

    // Splits CSV text into rows of trimmed cells. Simple split on commas: these client CSVs have no
    // quoted/embedded-comma fields (verified against the real files).
    private static IEnumerable<string[]> Rows(string csv)
    {
        foreach (string line in csv.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0)
                continue;
            string[] cols = trimmed.Split(',');
            for (int i = 0; i < cols.Length; i++)
                cols[i] = cols[i].Trim();
            yield return cols;
        }
    }

    private static bool TryFloat(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
