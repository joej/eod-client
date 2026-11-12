// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: names over heads (roadmap M9). Each creature and player in view gets its name, and under it the
// NPC's role or the player's guild in angle brackets: the create packets' guild field, which is where
// OpenDAoC (and retail) put an NPC's trade ("Hastener", "Weapon Merchant", "Teleporter", "Healer"). So a
// merchant, a teleporter and a plain townsperson read apart at a glance. Colours: a monster by how it cons to
// you; the colours and the rule are retail's (RE/56, icons-con-combat "Two colour sets"; PlateColour).
// The NPC's "hide name" flag hides it. Shown within NameRange metres; the dev overlay's "Names" switch turns
// them all off; Settings → Game has retail's "Names: Colored/White" and Display Names switches (ClientSettings). Retail (daoc-re, 2026-11-07): "[prefix ]name[ lastname]", then "<guild>"; Minion Pro Bold 20 with
// an outline layer (ui/fonts/MinionPro-Bold.ttf, which `mpk loading` copies into the library); no plate on
// yourself by default. Range and occlusion are still open (W50).

using System.Linq;
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
        (e.Level, e.Realm) = (level, realm);
        e.NameColor = PlateColour(e, false);   // for the target windows
        if ((npc.Flags2 & 0x02) != 0 || npc.Untargetable || npc.Name.Length == 0)
            return;
        e.Nameplate = Nameplate(e.Node, HeadHeight(e), npc.Name, npc.GuildName, e.NameColor, ClientSettings.NameKinds.Monsters);
    }

    private void NamePlayer(Entity e, PlayerCreate p)
    {
        int realm = (p.Flags >> 2) & 0x03;
        string name = string.Join(' ', new[] { p.Prefix, p.Name, p.LastName }.Where(s => s.Length > 0));
        bool enemy = IsEnemyRealm(realm);
        (e.Realm, e.IsPlayer, e.Guild) = (realm, true, p.GuildName);
        e.NameColor = PlateColour(e, false);
        e.Nameplate = Nameplate(e.Node, HeadHeight(e), name, p.GuildName, e.NameColor,
            enemy ? ClientSettings.NameKinds.Enemies : ClientSettings.NameKinds.Players);
    }

    private bool IsEnemyRealm(int realm) => realm != 0 && PlayerRealm != Realm.None && realm != (int)PlayerRealm;

    // Retail's plate colours with their alpha (text and outline): icons-con-combat "Two colour sets".
    private static readonly Color OwnRealmPlayer = Color.Color8(128, 128, 255, 191), EnemyPlayer = Color.Color8(204, 51, 51, 153),
        GroupPlate = Color.Color8(255, 191, 128, 191), GuildPlate = Color.Color8(0, 128, 128, 153),
        FriendlyNpc = Color.Color8(128, 255, 255, 204), DeadPlate = Color.Color8(128, 64, 64, 102),
        Highlighted = Color.Color8(255, 255, 255, 255);

    private static Color ConPlate(ConColor c) => c switch
    {
        ConColor.Grey => Color.Color8(128, 128, 128, 128),
        ConColor.Green => Color.Color8(128, 255, 128, 191),
        ConColor.Blue => Color.Color8(77, 77, 255, 153),
        ConColor.Yellow => Color.Color8(255, 255, 0, 153),
        ConColor.Orange => Color.Color8(255, 128, 64, 153),
        ConColor.Red => Color.Color8(255, 0, 0, 204),
        _ => Color.Color8(128, 0, 255, 204),   // purple
    };

    /// <summary>
    /// Retail's choice (RE/56): dead → the dead colour; the target → white (unless <paramref name="ignoreTarget"/>); a player
    /// in our group, our guild, an enemy realm, or else our realm; an NPC of our realm friendly; any other NPC (monsters,
    /// enemy-realm guards) its con colour.
    /// </summary>
    private Color PlateColour(Entity e, bool targeted)
    {
        if (e.Dead)
            return DeadPlate;
        if (targeted)
            return Highlighted;
        if (e.IsPlayer)
        {
            if (_bridge.Group.Members.Any(m => m.Name == e.Name) && _bridge.Group.InGroup)
                return GroupPlate;
            string ours = _info is PlayerInfo i ? PlayerInfo.Shown(i.GuildName) : "";
            if (ours.Length > 0 && e.Guild == ours)
                return GuildPlate;
            return IsEnemyRealm(e.Realm) ? EnemyPlayer : OwnRealmPlayer;
        }
        bool friendly = e.Realm != 0 && (e.Realm == (int)PlayerRealm || PlayerRealm == Realm.None);
        return friendly ? FriendlyNpc : ConPlate(ConColors.Of(PlayerLevel, e.Level));
    }

    // A plate's colour again after its entity's state changed (targeted, dead, grouped).
    private void RestylePlate(ushort id)
    {
        if (!_entities.TryGetValue(id, out Entity? e))
            return;
        e.NameColor = PlateColour(e, false);
        if (e.Nameplate is not null && IsInstanceValid(e.Nameplate))
        {
            e.Nameplate.SetMeta("colour", PlateColour(e, id == _target));
            StyleNameplate(e.Nameplate);
        }
    }

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
            _ownNameplate = Nameplate(_body, HeadHeight(_body), PlayerName, "", OwnRealmPlayer, ClientSettings.NameKinds.Self);
        ShowOwnBody();
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
        Color c = label.GetMeta("colour").AsColor();
        label.Modulate = ClientSettings.ColouredNames ? c : new Color(1, 1, 1, c.A);
        label.OutlineModulate = new Color(0, 0, 0, 0.85f * c.A);
    }

    // After a settings change: each plate's visibility and colour again.
    private void RestyleNames()
    {
        foreach (Entity e in _entities.Values)
            if (e.Nameplate is not null && IsInstanceValid(e.Nameplate))
                StyleNameplate(e.Nameplate);
        if (_ownNameplate is not null)
            StyleNameplate(_ownNameplate);
        ShowOwnBody();   // first person keeps your own plate hidden
    }

    private void ShowNames(bool on)
    {
        _namesVisible = on;
        RestyleNames();
    }
}
