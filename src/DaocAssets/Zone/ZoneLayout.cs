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
    float Scale,        // per-cent in the CSV; stored as a ratio (100 -> 1.0)
    bool OnGround = false,  // the CSV's "On Ground" column: the object stands on the terrain
    int Collide = 1,        // the "Collide" column: 0 = not solid (the client makes it solid only when set; daoc-re RE/69)
    float Tilt = 0,         // the "3D Angle" (radians) about (AxisX, AxisY, AxisZ), zone frame, when the axis isn't vertical:
    float AxisX = 0, float AxisY = 0, float AxisZ = 0);   // retail turns the object that way INSTEAD of by AngleDeg

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

            // Column 11 is "On Ground" (header: ID, NIF #, Textual Name, X, Y, Z, A, Scale, Collide,
            // Collide Radius, Animate, On Ground, Flip, Cave, Unique Id, then in newer files 3D Angle,
            // 3D Axis X, Y, Z).
            bool onGround = cols.Length > 11 && cols[11] == "1";
            // Column 8, Collide: non-zero makes the object solid (the model's collision nodes give the shape).
            int collide = cols.Length > 8 && int.TryParse(cols[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cv) ? cv : 1;
            // Columns 15–18: an axis-angle turn. Axis (0,0,0): turn by A. Any other axis, vertical ones too, turns the object
            // by the 3D Angle INSTEAD of A (world-data rules note §2). A vertical axis's angle is −A in 9,984 of 10,091 such
            // rows (the same turn); the other ~107 sit at 0 or 180°, and retail turns them that way, not by A.
            float tilt = 0, axX = 0, axY = 0, axZ = 0;
            if (cols.Length > 18 && TryFloat(cols[15], out float th) && TryFloat(cols[16], out float ax)
                && TryFloat(cols[17], out float ay) && TryFloat(cols[18], out float az)
                && (Math.Abs(ax) > 1e-6f || Math.Abs(ay) > 1e-6f || Math.Abs(az) > 1e-6f))
                (tilt, axX, axY, axZ) = (th, ax, ay, az);
            fixtures.Add(new ZoneFixture(
                model.Name, model.Label,
                x + offsetX, y + offsetY, z,
                ((angle % 360) + 360) % 360,
                scale, onGround, collide, tilt, axX, axY, axZ));
        }

        return new ZoneLayout { Fixtures = fixtures };
    }

    /// <summary>
    /// A capital city's layout (zone 26 City of Camelot, 50 Avalon City, 120 Jordheim, 209 Tir na Nog):
    /// <c>city.csv</c> lists its pieces ("40," then "1,cam01.nif", …) with no positions. The pieces are
    /// laid out side by side in the city's own coordinates; the whole city sits at region
    /// <see cref="CityOrigin"/>, turned 180°. Measured 2026-11-03 against where the server stands each
    /// city's NPCs (OpenDAoC database): with this placement the streets and floors hold 205 of 205 in
    /// Jordheim, 1,020 of 1,020 in Avalon City, 299 of 303 in Tir na Nog and 243 of 250 in Camelot; no
    /// other orientation holds even half. The cities' gate tables (ZONEJUMP.csv) use the same frame.
    /// All four cities have the zone offset 8192, so whether the origin is fixed in the region or
    /// relative to the zone (zone-local 23808) can't be told apart; the region value is used.
    /// </summary>
    public static ZoneLayout ParseCity(string cityCsv, int offsetX, int offsetY)
    {
        _ = (offsetX, offsetY);
        var fixtures = new List<ZoneFixture>();
        foreach (string[] cols in Rows(cityCsv))
        {
            if (cols.Length < 2 || cols[1].Trim().Length == 0
                || !int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                continue;
            string file = cols[1].Trim();
            fixtures.Add(new ZoneFixture(file, Path.GetFileNameWithoutExtension(file),
                CityOrigin.X, CityOrigin.Y, CityOrigin.Z, 180, 1f));
        }
        return new ZoneLayout { Fixtures = fixtures };
    }

    /// <summary>Where a capital city's pieces go, in region coordinates (they are turned 180°): see <see cref="ParseCity"/>.</summary>
    public static readonly (float X, float Y, float Z) CityOrigin = (32000, 32000, 8000);

    /// <summary>The layout in a zone's data archive: <c>city.csv</c> for a city, else <c>nifs.csv</c> + <c>fixtures.csv</c>.</summary>
    public static ZoneLayout Read(Mpk.MpkArchive dat, int offsetX, int offsetY)
    {
        if (dat.TryGetEntry("city.csv", out Mpk.MpkEntry city))
            return ParseCity(System.Text.Encoding.Latin1.GetString(dat.Extract(city)), offsetX, offsetY);
        return Parse(Member(dat, "nifs.csv"), Member(dat, "fixtures.csv"), offsetX, offsetY);
    }

    /// <summary>
    /// The layout as the client reads it: <c>city.csv</c> for a city, else <c>nifs.csv</c> + <c>fixtures.csv</c>, both
    /// from the csv archive when it has them (they must come from one archive: the fixtures index the model list).
    /// </summary>
    public static ZoneLayout Read(ZoneArchive zone, int offsetX, int offsetY) =>
        IsCity(zone.Dat) ? Read(zone.Dat, offsetX, offsetY) : Read(zone.SourceOf("nifs.csv", "fixtures.csv"), offsetX, offsetY);

    /// <summary>True for a capital city's archive (it has <c>city.csv</c>).</summary>
    public static bool IsCity(Mpk.MpkArchive dat) => dat.TryGetEntry("city.csv", out _);

    private static string Member(Mpk.MpkArchive dat, string name) =>
        dat.TryGetEntry(name, out Mpk.MpkEntry e)
            ? System.Text.Encoding.Latin1.GetString(dat.Extract(e))
            : throw new Mpk.MpkFormatException($"Zone archive has no member '{name}'.");

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
