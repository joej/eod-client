// SPDX-License-Identifier: GPL-3.0-or-later
//
// mpk: reads a DAoC client install (MPK/NPK archives, NIF models, zones, figures) and converts it
// into the files the Godot client loads. Owner-run tooling — point it at your own client install.
// The extracted files are the client's content, not ours; they never belong in this repo or a
// release (see CLAUDE.md, "Game client and assets").
//
// One file per subject: Cli.Archive (list, extract, index), Cli.Nif (nifinfo, convert, textures,
// model), Cli.Zone (zonedeps, zone, terrain, water, zonecheck), Cli.Sky, Cli.Loading, Cli.Maps, Cli.Figures (figure,
// figures, cata; CataConverter), Cli.Audit (audit, worldaudit). Options parses every command's options.

return Cli.Run(args);

internal static partial class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
            return Usage();

        string command = args[0];

        if (command == "textures")
            return Textures(args);

        if (command == "zone")
            return ZoneCmd(args);

        if (command == "index")
            return IndexCmd(args);

        if (command == "terrain")
            return TerrainCmd(args);

        if (command == "audit")
            return AuditCmd(args);

        if (command == "worldaudit")
            return WorldAuditCmd(args);

        if (command == "zonecheck")
            return ZoneCheckCmd(args);

        if (command == "water")
            return WaterCmd(args);

        if (command == "sky")
            return SkyCmd(args);

        if (command == "loading")
            return LoadingCmd(args);

        if (command == "maps")
            return MapsCmd(args);

        if (command == "icons")
            return IconsCmd(args);

        if (command == "model")
            return ModelCmd(args);

        if (command == "zonedeps")
            return ZoneDepsCmd(args);

        if (command == "figure")
            return FigureCmd(args);

        if (command == "figures")
            return FiguresCmd(args);

        if (command == "cata")
            return CataCmd(args);

        string inputPath = args[1];
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"No such file: {inputPath}");
            return 1;
        }

        if (command == "convert")
            return Convert(inputPath, args);

        if (command == "nifinfo")
            return NifInfo(inputPath);

        try
        {
            using var mpk = MpkArchive.Open(inputPath);
            return command switch
            {
                "list" => List(mpk),
                "extract" => Extract(mpk, args),
                _ => Usage(),
            };
        }
        catch (MpkFormatException e)
        {
            Console.Error.WriteLine($"{inputPath}: {e.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              mpk list     <archive.mpk>
              mpk extract  <archive.mpk> [member] [--out DIR]
              mpk convert  <model.nif> [--out FILE.glb]
              mpk nifinfo  <model.nif>
              mpk textures <client-dir> <model.nif | dir-of-nifs> --out DIR
              mpk index    <client-dir> --out index.json
              mpk model    <name.nif> --index index.json --library LIBDIR
              mpk figure   <model-id> --gamedata gamedata.mpk --index index.json --library LIBDIR
              mpk figures  --gamedata gamedata.mpk --index index.json --library LIBDIR   (every model id: classic figures, and Catacombs versions)
              mpk cata     <model-id> --gamedata gamedata.mpk --index index.json --library LIBDIR   (its Catacombs figure)
              mpk zonedeps <dat<NNN>.mpk> --index index.json --var NAME
              mpk zone     <dat<NNN>.mpk> --index index.json --library LIBDIR --offset X Y --out DIR [--size W H]
              mpk terrain  <dat<NNN>.mpk> --offset X Y --out FILE.glb [--size W H] [--tiles N] [--index index.json --library LIBDIR]
              mpk audit    <client-dir> --out FILE.md
              mpk worldaudit <client-dir> --out FILE.md
              mpk zonecheck <dat<NNN>.mpk> --zone DIR --library LIBDIR --offset X Y [--size W H] [--known FILE] [--out FILE.md]
              mpk water    <dat<NNN>.mpk> --offset X Y --out DIR [--size W H] [--index index.json --library LIBDIR]
              mpk sky      <dat<NNN>.mpk> --out DIR [--name albion] [--skympk <zones/sky/sky.mpk>] [--index index.json --library LIBDIR]
              mpk loading  <client-dir> --out DIR   (region loading screens: pictures, tips, bar, font)
              mpk maps     <client-dir> --out DIR   (zone and region map pictures, regions.dat, areas.dat)
              mpk icons    <client-dir> --out DIR   (spell, style, item and skill icon sheets, for the quickbars)

            With no member, extract writes every member. Default --out is the current directory.
            convert writes a glTF binary next to the .nif unless --out is given.
            textures gathers the .dds a model references (loose on disk or packed in an .mpk) into
            one folder, for the Godot client to load. Owner-run: point it at your own install.
            zone converts a zone's static objects and writes zone.csv (a placement manifest the
            Godot client loads). The offset is the zone's region offsetX/offsetY (zones.tsv x 8192).
            index walks the install once and records where every .nif/.dds lives (loose path or
            archive+member) to a JSON file, so later builds and lookups never re-scan.
            audit surveys every NIF (and .kfa animation) in the install and reports each block type and feature,
            how the converter treats it, and whether our byte-level decode of it holds. The report
            describes your client's files: write it under private/, not into the repo.
            """);
        return 2;
    }
}
