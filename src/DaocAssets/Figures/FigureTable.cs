// SPDX-License-Identifier: GPL-3.0-or-later
//
// models/figures.csv and models/catacombs.csv, written by `mpk figures` beside the converted models and
// read by the client: which model ids were converted, at what scale, from which figure.

using System.Globalization;

namespace DaocAssets.Figures;

/// <summary>One converted model id: its scale, the figure file it came from (no extension) and, for a
/// Catacombs model, its race's options file number (fig3; the client draws it as cata_fig&lt;fig3&gt;.glb).</summary>
public sealed record FigureRow(int Id, float Scale, string Figure, int? Fig3 = null);

/// <summary>Writes and reads the figure tables. Numbers are invariant-culture both ways.</summary>
public static class FigureTable
{
    /// <summary>Header of figures.csv (classic figures).</summary>
    public const string ClassicHeader = "id,scale,figure";

    /// <summary>Header of catacombs.csv (Catacombs figures, with fig3).</summary>
    public const string CatacombsHeader = "id,scale,figure,fig3";

    /// <summary>One line (no newline): three columns, or four with <see cref="FigureRow.Fig3"/>.</summary>
    public static string Row(FigureRow r) => r.Fig3 is int fig3
        ? FormattableString.Invariant($"{r.Id},{r.Scale},{r.Figure},{fig3}")
        : FormattableString.Invariant($"{r.Id},{r.Scale},{r.Figure}");

    /// <summary>Reads either table by model id; the header and malformed rows are skipped.</summary>
    public static Dictionary<int, FigureRow> Read(string csv)
    {
        var rows = new Dictionary<int, FigureRow>();
        foreach (string raw in csv.Split('\n'))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 3
                || !int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                || !float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float scale))
                continue;
            int? fig3 = c.Length >= 4 && int.TryParse(c[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int f) ? f : null;
            rows[id] = new FigureRow(id, scale, c[2], fig3);
        }
        return rows;
    }

    /// <summary>Header of strides.csv: per model id, the classic figure's strides, then the Catacombs figure's (empty
    /// when it has none).</summary>
    public const string StridesHeader = "id,run,walk,back,strafe,cata_run,cata_walk,cata_back,cata_strafe";

    public static string StridesRow(int id, Strides? classic, Strides? cata)
    {
        static string Four(Strides? s) => s is null ? ",,," : FormattableString.Invariant($"{s.Run},{s.Walk},{s.Back},{s.Strafe}");
        return $"{id},{Four(classic)},{Four(cata)}";
    }

    /// <summary>Reads strides.csv by model id: (classic, Catacombs), either null when not given.</summary>
    public static Dictionary<int, (Strides? Classic, Strides? Catacombs)> ReadStrides(string csv)
    {
        var rows = new Dictionary<int, (Strides?, Strides?)>();
        foreach (string raw in csv.Split('\n'))
        {
            string[] c = raw.Trim().Split(',');
            if (c.Length < 9 || !int.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                continue;
            Strides? Four(int at) => int.TryParse(c[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int run)
                && int.TryParse(c[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int walk)
                && int.TryParse(c[at + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int back)
                && int.TryParse(c[at + 3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int strafe)
                ? new Strides(run, walk, back, strafe) : null;
            rows[id] = (Four(1), Four(5));
        }
        return rows;
    }
}
