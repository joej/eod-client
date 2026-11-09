// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: names over heads (roadmap M9). Each creature and player in view gets its name, and under it the
// NPC's role or the player's guild in angle brackets: the create packets' guild field, which is where
// OpenDAoC (and retail) put an NPC's trade ("Hastener", "Weapon Merchant", "Teleporter", "Healer"). So a
// merchant, a teleporter and a plain townsperson read apart at a glance. Colours: a monster by how it cons to
// you (ConColors: grey up to purple); your realm's NPCs and players pale; another realm's in its realm colour.
// The NPC's "hide name" flag hides it. Shown within NameRange metres; the dev overlay's "Names" switch turns
// them all off; Settings → Game has retail's "Names: Colored/White" and Display Names switches (ClientSettings). Retail (daoc-re, 2026-11-07): "[prefix ]name[ lastname]", then "<guild>"; Minion Pro Bold 20 with
// an outline layer (ui/fonts/MinionPro-Bold.ttf, which `mpk loading` copies into the library); no plate on
// yourself by default. Its palette colours, range and occlusion are still open (W50).

using DaocProtocol.Packets;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const float NameRange = 45f;          // metres
    private static bool _namesVisible = true;     // the dev switch, kept across region changes

    /// <summary>The entered character's level and realm (the character list), for con colours and "friendly".</summary>
    public byte PlayerLevel { get; set; } = 1;
    public Realm PlayerRealm { get; set; } = Realm.None;

    private void NameNpc(Entity e, NpcCreate npc)
    {
        int realm = npc.Flags >> 6;
        int level = npc.Level & 0x7F;   // 0x80: a statue
        Color color = realm == 0 ? ConColour(ConColors.Of(PlayerLevel, level))
            : realm == (int)PlayerRealm || PlayerRealm == Realm.None ? Friendly : RealmNameColour(realm);
        (e.Level, e.NameColor) = (level, color);   // for the target windows
        if ((npc.Flags2 & 0x02) != 0 || npc.Untargetable || npc.Name.Length == 0)
            return;
        e.Nameplate = Nameplate(e.Node, HeadHeight(e), npc.Name, npc.GuildName, color, ClientSettings.NameKinds.Monsters);
    }

    private void NamePlayer(Entity e, PlayerCreate p)
    {
        int realm = (p.Flags >> 2) & 0x03;
        string name = string.Join(' ', new[] { p.Prefix, p.Name, p.LastName }.Where(s => s.Length > 0));
        bool enemy = realm != 0 && PlayerRealm != Realm.None && realm != (int)PlayerRealm;
        e.NameColor = enemy ? RealmNameColour(realm) : FriendlyPlayer;
        e.Nameplate = Nameplate(e.Node, HeadHeight(e), name, p.GuildName, enemy ? RealmNameColour(realm) : FriendlyPlayer,
            enemy ? ClientSettings.NameKinds.Enemies : ClientSettings.NameKinds.Players);
    }

    private static readonly Color Friendly = new(0.92f, 0.90f, 0.82f);
    private static readonly Color FriendlyPlayer = new(0.65f, 0.95f, 0.65f);

    private static Color RealmNameColour(int realm) => realm switch
    {
        1 => new Color(1f, 0.45f, 0.4f),     // Albion
        2 => new Color(0.45f, 0.65f, 1f),    // Midgard
        3 => new Color(0.5f, 0.9f, 0.45f),   // Hibernia
        _ => Friendly,
    };

    // The colours retail's con system names.
    private static Color ConColour(ConColor c) => c switch
    {
        ConColor.Grey => new Color(0.62f, 0.62f, 0.62f),
        ConColor.Green => new Color(0.35f, 0.85f, 0.35f),
        ConColor.Blue => new Color(0.4f, 0.6f, 1f),
        ConColor.Yellow => new Color(1f, 0.92f, 0.3f),
        ConColor.Orange => new Color(1f, 0.6f, 0.2f),
        ConColor.Red => new Color(1f, 0.25f, 0.2f),
        _ => new Color(0.8f, 0.4f, 1f),   // purple
    };

    private static Font? _nameFont;
    private static bool _nameFontTried;

    // The install's Minion Pro Bold (converted to <library>/loading), else Godot's default.
    private Font? NameFont()
    {
        if (!_nameFontTried && _libraryPath is string lib)
        {
            _nameFontTried = true;
            string path = System.IO.Path.Combine(lib, "loading", "minionpro-bold.ttf");
            var font = new FontFile();
            if (System.IO.File.Exists(path) && font.LoadDynamicFont(path) == Error.Ok)
                _nameFont = font;
        }
        return _nameFont;
    }

    /// <summary>The entered character's name ("Your Name", off by default as in retail).</summary>
    public string PlayerName { get; set; } = "";
    private Label3D? _ownNameplate;

    private void NameSelf()
    {
        if (PlayerName.Length > 0)
            _ownNameplate = Nameplate(_body, HeadHeight(_body), PlayerName, "", FriendlyPlayer, ClientSettings.NameKinds.Self);
    }

    // A plate over a head: the name, the role or guild in angle brackets under it. It keeps its kind (which
    // Display Names switch governs it) and its colour (for "Names: White" and back) as metadata.
    private Label3D Nameplate(Node3D owner, Vector3 head, string name, string role, Color color, ClientSettings.NameKinds kind)
    {
        var label = new Label3D
        {
            Text = role.Length > 0 ? $"{name}\n<{role}>" : name,
            Font = NameFont(),
            OutlineModulate = new Color(0, 0, 0, 0.85f),
            OutlineSize = 8,
            FontSize = 32,
            // The same size on screen near or far (about 18 px a line at 900 px high), as a HUD label.
            FixedSize = true,
            PixelSize = 0.0009f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            VerticalAlignment = VerticalAlignment.Bottom,
            // Above the head, under a quest glyph (0.9 m up).
            Position = head + new Vector3(0, 0.3f, 0),
            VisibilityRangeEnd = NameRange,
            // Over the figure's own hair and helmet, still hidden by walls in front.
            RenderPriority = 1,
        };
        label.SetMeta("kind", (int)kind);
        label.SetMeta("colour", color);
        StyleNameplate(label);
        owner.AddChild(label);
        return label;
    }

    private static void StyleNameplate(Label3D label)
    {
        var kind = (ClientSettings.NameKinds)label.GetMeta("kind").AsInt32();
        label.Visible = _namesVisible && ClientSettings.ShowNames.HasFlag(kind);
        label.Modulate = ClientSettings.ColouredNames ? label.GetMeta("colour").AsColor() : Colors.White;
    }

    // After a settings change: each plate's visibility and colour again.
    private void RestyleNames()
    {
        foreach (Entity e in _entities.Values)
            if (e.Nameplate is not null && IsInstanceValid(e.Nameplate))
                StyleNameplate(e.Nameplate);
        if (_ownNameplate is not null)
            StyleNameplate(_ownNameplate);
    }

    private void ShowNames(bool on)
    {
        _namesVisible = on;
        RestyleNames();
    }
}
