// SPDX-License-Identifier: GPL-3.0-or-later
//
// Debug travel (GM accounts): pick a zone by name and go there. Lists every zone of the server's zone
// table (World/zones.tsv) with its number, name and region, marks the ones converted on this machine
// (private/zones/zoneNNN), filters as you type, and keeps the last destinations at the top. Going sends
// the GM command "/jump to X Y Z REGION" to the zone's landing spot (written by the converter where the
// middle is a bad place, e.g. a city's gate) or its middle; the server moves the character and the
// client loads that zone as for any other move. A non-GM account gets the server's refusal in chat.

using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class TravelDialog : AcceptDialog
{
    private const int MaxRecent = 6;
    private const string SettingsPath = "user://settings.cfg";

    private string? _zonesDir;
    private System.Action<Zone>? _go;
    private readonly ItemList _list = new() { CustomMinimumSize = new Vector2(560, 380), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly LineEdit _filter = new() { PlaceholderText = "Filter: name or number (e.g. camelot, 26)" };
    private readonly CheckBox _convertedOnly = new() { Text = "Converted zones only", ButtonPressed = true };
    private readonly List<Zone> _shown = [];

    /// <summary>Opens the window over <paramref name="parent"/>'s scene (one at a time); <paramref name="go"/>
    /// is called with the zone chosen.</summary>
    public static TravelDialog Open(Node parent, string? zonesDir, System.Action<Zone> go)
    {
        foreach (Node n in parent.GetTree().Root.GetChildren())
            if (n is TravelDialog open)
            {
                open._filter.GrabFocus();
                return open;
            }
        var dialog = new TravelDialog { _zonesDir = zonesDir, _go = go };
        parent.GetTree().Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(600, 520));
        return dialog;
    }

    /// <summary>Types <paramref name="text"/> into the filter, as a player would (for --travel).</summary>
    public void Filter(string text)
    {
        _filter.Text = text;
        Fill();
    }

    /// <summary>Goes to the first zone listed, as Enter in the filter does (for --travel).</summary>
    public void GoFirst() => Go(_shown.Count > 0 ? 0 : -1);

    public override void _Ready()
    {
        Title = "Travel (GM)";
        OkButtonText = "Go";
        Exclusive = false;
        Confirmed += () => Go(_list.GetSelectedItems() is [int i, ..] ? i : -1);
        Canceled += QueueFree;

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        AddChild(column);
        column.AddChild(_filter);
        column.AddChild(_convertedOnly);
        column.AddChild(_list);
        column.AddChild(new Label
        {
            Text = "Double-click or Enter to go. Uses the GM /jump command; other accounts are refused by the server.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(560, 0),
        });
        _filter.TextChanged += _ => Fill();
        _filter.TextSubmitted += _ => Go(_list.GetSelectedItems() is [int i, ..] ? i : _shown.Count > 0 ? 0 : -1);
        _convertedOnly.Toggled += _ => Fill();
        _list.ItemActivated += index => Go((int)index);
        Fill();
        _filter.GrabFocus();
    }

    // The list: recent destinations first, then every zone matching the filter, by number.
    private void Fill()
    {
        _list.Clear();
        _shown.Clear();
        string f = _filter.Text.Trim();
        bool Match(Zone z) =>
            (f.Length == 0 || z.Name.Contains(f, System.StringComparison.OrdinalIgnoreCase) || z.Id.ToString() == f.TrimStart('0'))
            && (!_convertedOnly.ButtonPressed || Converted(z));
        var recent = Recent().Select(id => Zones.All.FirstOrDefault(z => z.Id == id)).OfType<Zone>().Where(Match).ToList();
        foreach (Zone z in recent)
            Add(z, "recent  ");
        foreach (Zone z in Zones.All.Where(Match).Where(z => !recent.Contains(z)).OrderBy(z => z.Id))
            Add(z, "");
        if (_shown.Count > 0)
            _list.Select(0);

        void Add(Zone z, string tag)
        {
            _list.AddItem($"{tag}{z.Id,3}  {z.Name}   (region {z.RegionId}){(Converted(z) ? "" : "   – not converted")}");
            _shown.Add(z);
        }
    }

    private bool Converted(Zone z) =>
        _zonesDir is not null && System.IO.File.Exists(System.IO.Path.Combine(_zonesDir, $"zone{z.Id:D3}", "zone.csv"));

    private void Go(int index)
    {
        if (index < 0 || index >= _shown.Count)
            return;
        Zone zone = _shown[index];
        Remember(zone.Id);
        _go?.Invoke(zone);
        QueueFree();
    }

    // Destination for a zone: the converter's landing spot (zoneNNN/landing.txt: "x y z") where it wrote
    // one, else the zone's middle at height `z` (outdoors the character lands on the terrain from there).
    public static (float X, float Y, float Z, string Where) Target(Zone zone, string? zonesDir, float z)
    {
        (float x, float y) = zone.Centre;
        string landing = zonesDir is null ? "" : System.IO.Path.Combine(zonesDir, $"zone{zone.Id:D3}", "landing.txt");
        if (landing.Length > 0 && System.IO.File.Exists(landing)
            && System.IO.File.ReadAllText(landing).Split(' ', System.StringSplitOptions.RemoveEmptyEntries) is [var sx, var sy, var sz, ..]
            && float.TryParse(sx, System.Globalization.CultureInfo.InvariantCulture, out float lx)
            && float.TryParse(sy, System.Globalization.CultureInfo.InvariantCulture, out float ly)
            && float.TryParse(sz, System.Globalization.CultureInfo.InvariantCulture, out float lz))
            return (lx, ly, lz, "its landing spot");
        return (x, y, z, "the middle");
    }

    private static List<int> Recent()
    {
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath);
        return cfg.GetValue("travel", "recent", "").AsString()
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out int id) ? id : -1)
            .Where(id => id >= 0).ToList();
    }

    private static void Remember(int zoneId)
    {
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath);
        var recent = Recent().Where(id => id != zoneId).Prepend(zoneId).Take(MaxRecent);
        cfg.SetValue("travel", "recent", string.Join(',', recent));
        cfg.Save(SettingsPath);
    }
}
