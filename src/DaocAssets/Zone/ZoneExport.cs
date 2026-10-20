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
        AssetLibrary library, string zoneDatMpk, int offsetX, int offsetY, string outDir)
    {
        string nifsCsv, fixturesCsv;
        using (MpkArchive dat = MpkArchive.Open(zoneDatMpk))
        {
            nifsCsv = ReadMember(dat, "nifs.csv");
            fixturesCsv = ReadMember(dat, "fixtures.csv");
        }

        ZoneLayout layout = ZoneLayout.Parse(nifsCsv, fixturesCsv, offsetX, offsetY);
        Directory.CreateDirectory(outDir);

        // Ensure each distinct model is in the library (converted once, or an override); the library
        // also gathers each model's textures. Lookups are index-backed — no install scan.
        var glbByNif = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int converted = 0, missingModel = 0, failed = 0;

        foreach (string nif in layout.Models)
        {
            ModelResult r = library.GetModel(nif);
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
              .Append(f.Z.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(f.AngleDeg.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(f.Scale.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
            placed++;
        }
        File.WriteAllText(Path.Combine(outDir, "zone.csv"), sb.ToString());

        return new ZoneExportReport(layout.Fixtures.Count, layout.Models.Count, converted, missingModel, failed, placed);
    }

    private static string ReadMember(MpkArchive mpk, string name)
    {
        if (!mpk.TryGetEntry(name, out MpkEntry entry))
            throw new MpkFormatException($"Zone archive has no member '{name}'.");
        return Encoding.Latin1.GetString(mpk.Extract(entry));
    }
}

/// <summary>Summary of a zone export run.</summary>
public readonly record struct ZoneExportReport(
    int Fixtures, int Models, int Converted, int MissingModel, int Failed, int Placed);
