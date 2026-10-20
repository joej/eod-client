// SPDX-License-Identifier: GPL-3.0-or-later
//
// Owner-run: builds a zone's renderable asset set from the owner's install — converts each model a
// zone's fixtures reference to .glb, gathers their textures, and writes a placement manifest the
// Godot client loads to populate the scene. Output is client-derived content and never enters the
// repo or a release (see CLAUDE.md, "Game client and assets").

using System.Globalization;
using System.Text;
using DaocAssets.Mpk;
using DaocAssets.Nif;

namespace DaocAssets.Zone;

/// <summary>Writes a zone's objects (.glb + textures) and a placement manifest to an output folder.</summary>
public static class ZoneExport
{
    /// <summary>
    /// Reads zone &lt;id&gt;'s fixtures/nifs CSVs from <paramref name="zoneDatMpk"/>, converts each
    /// referenced model found under <paramref name="clientDir"/> to a .glb, gathers its textures,
    /// and writes <c>zone.csv</c> (the placement manifest) into <paramref name="outDir"/>. Returns a
    /// short report. <paramref name="offsetX"/>/<paramref name="offsetY"/> are the zone's region
    /// offset (from the zone table), in region units.
    /// </summary>
    public static ZoneExportReport Export(
        string clientDir, string zoneDatMpk, int offsetX, int offsetY, string outDir, string? searchDir = null)
    {
        // Zone fixture models (packed one-per-archive) and their loose textures live under the zone's
        // Nifs directory; search there by default rather than walking the whole install, which is slow
        // over a mounted drive. Fall back to the client root when no Nifs dir is found.
        string assetDir = searchDir ?? FindAssetDir(zoneDatMpk, clientDir);

        string nifsCsv, fixturesCsv;
        using (MpkArchive dat = MpkArchive.Open(zoneDatMpk))
        {
            nifsCsv = ReadMember(dat, "nifs.csv");
            fixturesCsv = ReadMember(dat, "fixtures.csv");
        }

        ZoneLayout layout = ZoneLayout.Parse(nifsCsv, fixturesCsv, offsetX, offsetY);
        Directory.CreateDirectory(outDir);

        // Resolve + convert each distinct model once. Model files are packed one-per-archive under
        // the install (e.g. zones/Nifs/keep.npk); textures are gathered beside the .glb.
        var resolver = new TextureResolver(assetDir);
        var modelIndex = new ModelLocator(assetDir);
        var glbByNif = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int converted = 0, missingModel = 0, failed = 0;

        foreach (string nif in layout.Models)
        {
            string stem = Path.GetFileNameWithoutExtension(nif);
            string glbName = stem + ".glb";
            string glbPath = Path.Combine(outDir, glbName);

            byte[]? nifBytes = modelIndex.Read(nif);
            if (nifBytes is null)
            {
                missingModel++;
                continue;
            }
            try
            {
                NifModel model = NifFile.Read(nifBytes);
                GltfExporter.SaveGlb(model, glbPath);
                glbByNif[nif] = glbName;
                converted++;
                foreach (NifMesh mesh in model.Meshes)
                    if (!string.IsNullOrEmpty(mesh.TextureFile))
                        resolver.Resolve(mesh.TextureFile!, outDir);
            }
            catch (NifFormatException)
            {
                failed++;
            }
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

    // The zone's dat mpk sits at zones/<ZoneNNN>/datNNN.mpk; fixture models and their loose textures
    // live at zones/Nifs. Prefer that Nifs dir (small, fast to index); fall back to the client root.
    private static string FindAssetDir(string zoneDatMpk, string clientDir)
    {
        string? zoneDir = Path.GetDirectoryName(Path.GetFullPath(zoneDatMpk)); // zones/ZoneNNN
        string? zonesDir = zoneDir is null ? null : Path.GetDirectoryName(zoneDir); // zones/
        foreach (string? candidate in new[] { zonesDir, zoneDir })
        {
            if (candidate is null)
                continue;
            string nifs = Path.Combine(candidate, "Nifs");
            if (Directory.Exists(nifs))
                return nifs;
        }
        return clientDir;
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
