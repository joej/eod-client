// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk icons: the install's icon sheets (icons/*.bmp: spells, styles, items, skills) into the library, for the
// quickbars and, later, the spell, style and inventory windows.

internal static partial class Cli
{
    // icons <client-dir> --out DIR: every icon sheet of icons/ copied as is, names lower-cased (the client finds a
    // sheet by its first icon number: DaocAssets.Ui.IconSheets).
    private static int IconsCmd(string[] args)
    {
        string client = args[1];
        if (Options.Parse(args, 2, ["--out"]) is not { } o || o["--out"] is not string outDir)
            return Usage();
        string? source = Directory.Exists(client)
            ? Directory.EnumerateDirectories(client).FirstOrDefault(d => Path.GetFileName(d).Equals("icons", StringComparison.OrdinalIgnoreCase))
            : null;
        if (source is null)
        {
            Console.Error.WriteLine("icons: no icons/ folder in the install");
            return 2;
        }
        Dictionary<int, string> sheets = DaocAssets.Ui.IconSheets.Index(Directory.EnumerateFiles(source));
        Directory.CreateDirectory(outDir);
        foreach (string sheet in sheets.Values)
            File.Copy(Path.Combine(source, sheet), Path.Combine(outDir, sheet.ToLowerInvariant()), overwrite: true);
        // The border frames laid over some icons (icons.csv borderId) and the style marks over those (wgsm_0: 13 cells
        // of 10 x 10, daoc-re RE/68).
        foreach (string extra in new[] { "brdr_0.bmp", "wgsm_0.bmp" })
            if (Directory.EnumerateFiles(source).FirstOrDefault(f => Path.GetFileName(f).Equals(extra, StringComparison.OrdinalIgnoreCase)) is string file)
                File.Copy(file, Path.Combine(outDir, extra), overwrite: true);
        // From the server's number to a picture: icons.csv, spells.csv, styles.csv (DaocAssets.Ui.IconTable).
        if (FindInstallFile(client, "gamedata.mpk") is not string gamedata)
        {
            Console.Error.WriteLine("icons: no gamedata.mpk in the install");
            return 2;
        }
        using (var mpk = DaocAssets.Mpk.MpkArchive.Open(gamedata))
            foreach (string table in new[] { "icons.csv", "spells.csv", "styles.csv" })
                File.WriteAllBytes(Path.Combine(outDir, table), mpk.Extract(table));
        var lookup = DaocAssets.Ui.IconTable.Parse(File.ReadAllText(Path.Combine(outDir, "icons.csv"), System.Text.Encoding.Latin1),
            File.ReadAllText(Path.Combine(outDir, "spells.csv"), System.Text.Encoding.Latin1),
            File.ReadAllText(Path.Combine(outDir, "styles.csv"), System.Text.Encoding.Latin1));
        Console.WriteLine($"icons: {sheets.Count} sheets (icons {string.Join(", ", sheets.Keys.Order().Select(k => $"{k}–{k + 99}"))}); " +
                          $"{lookup.Count} icon ids");
        return sheets.Count > 0 ? 0 : 2;
    }
}
