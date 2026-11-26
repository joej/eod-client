// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk speffects: the spell effects the client draws (DaocAssets.Effects.SpellEffectTables). Every speffects.csv row whose
// NIF is an spnifs.csv effect (ids under 1500) is converted into the library (models/effects/<name>.glb, with its
// particles beside it), and models/speffects.csv lists each effect for the client: id, model, attach node, duration (ms),
// rate (%), track angle, auto scale, spin axis, angle (degrees), focus effect, freq (ms), speed (units/s), timeout (ms),
// target effect. A row whose NIF isn't drawn yet (weapon trails, flying items, MonEffects NIFs) or isn't in the install
// keeps an empty model. The client takes the spells' own columns from spells.csv (in icons/, `mpk icons`).

using DaocAssets.Effects;
using DaocAssets.Library;

internal static partial class Cli
{
    private static int SpEffectsCmd(string[] args)
    {
        if (FigureOptions(args, 1) is not var (gamedata, indexPath, libDir)) return Usage();
        using var mpk = DaocAssets.Mpk.MpkArchive.Open(gamedata);
        string Table(string name) => System.Text.Encoding.Latin1.GetString(mpk.Extract(name));
        Dictionary<int, SpEffect> effects = SpellEffectTables.ParseEffects(Table("speffects.csv"));
        Dictionary<int, SpNif> nifs = SpellEffectTables.ParseNifs(Table("spnifs.csv"));

        var index = AssetIndex.Load(indexPath);
        var root = new AssetLibrary(index, libDir);
        // Each folder its own search: effects/, frontiers/nifs/, phousing/effects/ (spnifs' folder column).
        AssetLibrary Scope(int folder) => folder switch
        {
            1 => root.Scoped("effects", ["phousing/effects/", "phousing/"]),
            2 => root.Scoped("effects", ["frontiers/nifs/", "frontiers/"]),
            _ => root.Scoped("effects", EffectFolders),
        };
        string models = Path.Combine(libDir, "models");
        Directory.CreateDirectory(models);

        var stems = new Dictionary<int, string?>();
        int converted = 0, absent = 0, unsupported = 0, failed = 0, notDrawn = 0;
        string? Stem(int nifId)
        {
            if (stems.TryGetValue(nifId, out string? known))
                return known;
            string? stem = null;
            if (nifId is > 0 and < 1500 && nifs.TryGetValue(nifId, out SpNif? nif) && !nif.File.Equals("empty", StringComparison.OrdinalIgnoreCase))
            {
                ModelResult r = Scope(nif.Folder).GetModel(nif.File + ".nif", reconvert: true);
                switch (r.Origin)
                {
                    case AssetOrigin.Converted or AssetOrigin.Override: converted++; stem = ModelStem(models, r.GlbPath!); break;
                    case AssetOrigin.Missing: absent++; break;
                    case AssetOrigin.Unsupported: unsupported++; Console.Error.WriteLine($"  effect nif {nifId}: {r.Error}"); break;
                    default: failed++; Console.Error.WriteLine($"  effect nif {nifId}: {r.Error}"); break;
                }
            }
            else if (nifId >= 1500)
                notDrawn++;
            return stems[nifId] = stem;
        }

        static string I(FormattableString f) => FormattableString.Invariant(f);
        var csv = new System.Text.StringBuilder("id,model,node,duration,rate,track,autoscale,spin,angle,focus_effect,focus_freq,focus_speed,focus_timeout,target_fx\n");
        foreach (SpEffect e in effects.Values.OrderBy(e => e.Id))
            csv.Append(I($"{e.Id},{Stem(e.Nif) ?? ""},{e.Node.Replace(',', ' ')},{e.DurationMs},{e.Rate},{e.TrackAngle},{(e.AutoScale ? 1 : 0)},{e.SpinAxis},{e.Angle},{e.FocusEffect},{e.FocusFreqMs},{e.FocusSpeed},{e.FocusTimeoutMs},{e.TargetFx}\n"));
        File.WriteAllText(Path.Combine(models, "speffects.csv"), csv.ToString());
        Console.WriteLine($"speffects: {effects.Count} effects, {converted} effect models converted; {absent} not in the install, " +
            $"{notDrawn} effects of kinds not drawn yet (trails, items, MonEffects), {unsupported} unsupported, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
