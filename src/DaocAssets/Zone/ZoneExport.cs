// SPDX-License-Identifier: GPL-3.0-or-later
//
// Owner-run: builds a zone's renderable asset set from the owner's install — converts each model a
// zone's fixtures reference to .glb, gathers their textures, and writes a placement manifest the
// Godot client loads to populate the scene. Output is client-derived content and never enters the
// repo or a release (see CLAUDE.md, "Game client and assets").

using System.Globalization;
using System.Text;
using DaocAssets.Library;
using DaocAssets.Mpk;

namespace DaocAssets.Zone;

/// <summary>Writes a zone placement manifest, populating the shared asset library with its objects.</summary>
public static class ZoneExport
{
    /// <summary>
    /// Reads the zone's fixtures/nifs CSVs from <paramref name="zoneDatMpk"/>, ensures each referenced
    /// model is in the <paramref name="library"/> (converting it once, override-aware), and writes
    /// <c>zone.csv</c> (the placement manifest) into <paramref name="outDir"/>. The manifest names
    /// models by their library glb name; the client resolves them against the library. Returns a
    /// short report. <paramref name="offsetX"/>/<paramref name="offsetY"/> are the zone's region
    /// offset (from the zone table), in region units.
    /// </summary>
    public static ZoneExportReport Export(
        AssetLibrary library, string zoneDatMpk, int offsetX, int offsetY, string outDir, int sizeX = 8, int sizeY = 8)
    {
        ZoneLayout layout;
        (float X, float Y, float Z)? landing = null;
        List<ZoneJump> jumpPoints = [];
        using (MpkArchive dat = MpkArchive.Open(zoneDatMpk))
        {
            layout = ZoneLayout.Read(dat, offsetX, offsetY);
            if (dat.TryGetEntry("zonejump.csv", out MpkEntry jumps))
            {
                string csv = Encoding.Latin1.GetString(dat.Extract(jumps));
                jumpPoints = ZoneJumps.Parse(csv, offsetX, offsetY);
                if (ZoneLayout.IsCity(dat))
                    landing = CityLanding(csv, offsetX, offsetY);
            }
        }
        Directory.CreateDirectory(outDir);
        // The zone's doorways (city gates, dungeon entrances): the client sends their zone point on
        // walking through one (C->S 0x90).
        File.WriteAllText(Path.Combine(outDir, "jumps.csv"), ZoneJumps.ToCsv(jumpPoints));
        // Where play.sh --zone puts the character: a city's middle can be inside a wall or on a roof.
        string landingPath = Path.Combine(outDir, "landing.txt");
        if (landing is { } l)
            File.WriteAllText(landingPath, string.Create(CultureInfo.InvariantCulture, $"{l.X:0} {l.Y:0} {l.Z:0}\n"));
        else if (File.Exists(landingPath))
            File.Delete(landingPath);

        // "On Ground" objects stand on the terrain: their stored Z is stale (Camelot Hills: 546 are 0,
        // the other 975 sit a median 726 units, up to 1,583, below the ground), while objects without
        // the flag match the terrain to within ~40 units. So an On Ground object gets the terrain
        // height under its position; the rest keep their Z (a bridgehouse sits 315 below on purpose).
        // A Z of 0 means the ground too, flag or not: across all 87 outdoor zones 287 unflagged rows
        // have Z = 0, and they are dead trees, coral, catacomb entrances, ant hills and stones. Except
        // at the zone's origin (X = Y = 0): 104 of those rows, in 11 zones, are pieces modelled in zone
        // coordinates (Cothrom Gorge's cliffs, Aegir's Landing, Caer Sidi) and belong exactly there.
        // Lifted onto the ground at the zone's corner they floated up to 200 m in the air (2026-11-03).
        ZoneTerrain? terrain = null;
        try { terrain = ZoneTerrain.Read(zoneDatMpk, sizeX, sizeY); }
        catch (Exception e) when (e is FormatException or InvalidDataException or MpkFormatException or KeyNotFoundException) { }
        int grounded = 0;
        float Z(ZoneFixture f)
        {
            float z = PlacementZ(f, offsetX, offsetY, terrain is null ? null : terrain.HeightAt);
            if (z != f.Z) grounded++;
            return z;
        }

        // Ensure each distinct model is in the library (converted once, or an override); the library
        // also gathers each model's textures. Lookups are index-backed — no install scan.
        var glbByNif = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int converted = 0, missingModel = 0, failed = 0;
        var failures = new List<string>();

        foreach (string nif in layout.Models)
        {
            ModelResult r = library.GetModel(nif);
            if (r.Origin == AssetOrigin.Failed)
            {
                failed++;
                failures.Add(r.Error!);
                continue;
            }
            if (r.GlbPath is null)
            {
                missingModel++;
                continue;
            }
            glbByNif[nif] = Path.GetFileName(r.GlbPath);
            converted++;
        }

        // Manifest: one line per placement, "glb,x,y,z,angleDeg,scale". Only fixtures whose model
        // converted are written (so the client never references a missing .glb).
        var sb = new StringBuilder();
        sb.Append("glb,x,y,z,angle,scale\n");
        int placed = 0;
        foreach (ZoneFixture f in layout.Fixtures)
        {
            if (!glbByNif.TryGetValue(f.NifName, out string? glb))
                continue;
            sb.Append(glb).Append(',')
              .Append(f.X.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Y.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(Z(f).ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(f.AngleDeg.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Scale.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            placed++;
        }
        File.WriteAllText(Path.Combine(outDir, "zone.csv"), sb.ToString());

        return new ZoneExportReport(layout.Fixtures.Count, layout.Models.Count, converted, missingModel, failed, placed)
        {
            Grounded = grounded,
            Failures = failures,
        };
    }

    /// <summary>The distinct models (bare NIF names, first spelling wins) the zone's fixtures place:
    /// what <see cref="Export"/> converts. The Makefile builds one library target per name.</summary>
    public static IReadOnlyList<string> ModelNames(string zoneDatMpk)
    {
        ZoneLayout layout;
        using (MpkArchive dat = MpkArchive.Open(zoneDatMpk))
            layout = ZoneLayout.Read(dat, 0, 0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (string nif in layout.Models)
        {
            string bare = nif[(nif.LastIndexOfAny(['/', '\\']) + 1)..];
            if (seen.Add(bare))
                names.Add(bare);
        }
        return names;
    }

    private static string ReadMember(MpkArchive mpk, string name)
    {
        if (!mpk.TryGetEntry(name, out MpkEntry entry))
            throw new MpkFormatException($"Zone archive has no member '{name}'.");
        return Encoding.Latin1.GetString(mpk.Extract(entry));
    }

    /// <summary>
    /// A landing spot in a capital city: its first gate. <c>ZONEJUMP.csv</c> rows are "id, target,
    /// x1, y1, x2, y2, z1, z2, zone": a doorway line (zone-local, the same frame the city's NPCs
    /// confirm for its pieces; see ZoneLayout.ParseCity) from floor (z1) to top (z2) that
    /// leads out of the city. The spot is the doorway's middle, 1 m above its floor (so a ray looking
    /// for the floor starts under the arch). Region-global; null if no row parses.
    /// </summary>
    public static (float X, float Y, float Z)? CityLanding(string zoneJumpCsv, int offsetX, int offsetY)
    {
        foreach (string raw in zoneJumpCsv.Split('\n'))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 8) continue;
            float[] v = new float[6];
            bool ok = true;
            for (int k = 0; k < 6; k++)
                ok &= float.TryParse(c[2 + k], NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]);
            if (ok)
                return (offsetX + (v[0] + v[2]) / 2, offsetY + (v[1] + v[3]) / 2, v[4] + 40);
        }
        return null;
    }

    /// <summary>
    /// A placement's height: the terrain under it (<paramref name="groundAt"/>, zone-local X and Y) when
    /// it is flagged On Ground or stored at Z = 0, except a piece at the zone's origin, which is modelled
    /// in zone coordinates and keeps its Z (see the rule's notes where it is used).
    /// </summary>
    internal static float PlacementZ(ZoneFixture f, int offsetX, int offsetY, Func<float, float, float>? groundAt)
    {
        bool zoneSpacePiece = f.X == offsetX && f.Y == offsetY;
        if (groundAt is null || !(f.OnGround || (f.Z == 0 && !zoneSpacePiece)))
            return f.Z;
        return groundAt(f.X - offsetX, f.Y - offsetY);
    }
}

/// <summary>Summary of a zone export run.</summary>
public readonly record struct ZoneExportReport(
    int Fixtures, int Models, int Converted, int MissingModel, int Failed, int Placed)
{
    /// <summary>Placements stood on the terrain because they are flagged "On Ground".</summary>
    public int Grounded { get; init; }
    /// <summary>Models whose conversion threw (converter bugs), one line each.</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];
}
