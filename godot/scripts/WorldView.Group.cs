// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the stats panel's Group page (retail's ui/atlantis/stats_group_window.xml, 192 x 416) and the server's
// dialogs (0x81), which bring group invites.
//
// Group page, at the skin's positions: eight member rows 38 pixels apart (leader first, as the server lists them; 0x16
// subcode 6), each with the name at 6, 4, the class at 93, the level at 170, health (red), endurance and power (thin
// green and yellow) bars 72 wide at y 17 / 24 / 28, and six effect icons from x 81 at y 19 (0x70). Clicking a row
// targets that member. Under them the skin's 14 buttons: INVITE (the target, 0x87) and DISBAND (0x9F: the target if it is
// in our group and we lead it, else ourselves); ASSIST, FOLLOW, STICK, FACE and MAP as the target window's and the menu
// bar's; MINI (the floating group window), FIND and SET LFG (looking for group), BENEFITS, WARMAP, CGF (casual group
// finder) and OPTIONS open windows not built yet and are dimmed. A member's name shows its state as the client does
// (daoc-re's group-page note, Q1): the first of its 0x70 state bits in the order link dead, dead, mezzed, diseased,
// poisoned, nearsight, other region, 0x80 wraps the name in that state's marker (*name*, =name=, ...) and colours it;
// none: white. Clicking a row, or a group key, targets that member (an empty row clears the target; nothing while dead).
// Effect icons are spells.csv ids (Q3).
//
// Dialog (retail's dialog_sm, 256 x 96, centred): up to 7 centred lines and ACCEPT / DECLINE, or OK, 58 x 16 at y 76
// (Q6); the answer goes back as 0x82 with the dialog's data words. A new dialog replaces one still open, unanswered.

using System.Collections.Generic;
using System.Linq;
using DaocProtocol.Packets;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int GroupRows = 8;

    private sealed record GroupRow(Panel Back, Label Name, Label Class, Label Level, HudBar Health, HudBar Endurance, HudBar Power,
        TextureRect[] Icons);

    private readonly List<GroupRow> _groupRows = [];
    private Button? _groupInvite, _groupDisband, _groupAssist;

    private HudWindow? _dialogWindow;
    private Label? _dialogText;
    private Button? _dialogYes, _dialogNo, _dialogOk;
    private Dialog? _dialog;
    private bool _groupSubscribed;
    private bool _acceptDialogs;   // --accept-dialogs

    private void BuildGroupPage(Control p, float s)
    {
        _groupRows.Clear();
        for (int i = 0; i < GroupRows; i++)
        {
            int row = i;
            float y = 3 + 38 * i;
            var back = new Panel { Position = new Vector2(3, y) * s, Size = new Vector2(185, 35) * s, MouseFilter = Control.MouseFilterEnum.Stop };
            back.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    TargetGroupMember(row);
                    back.AcceptEvent();
                }
            };
            p.AddChild(back);
            var icons = new TextureRect[6];
            for (int k = 0; k < icons.Length; k++)
            {
                icons[k] = new TextureRect
                {
                    Position = new Vector2(81 + 18 * k, y + 16) * s, Size = new Vector2(16, 16) * s,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                };
                p.AddChild(icons[k]);
            }
            _groupRows.Add(new GroupRow(back,
                PageLabel(p, s, new Vector2(6, y + 1), new Vector2(86, 11), 9, colour: Color.Color8(200, 200, 200)),
                PageLabel(p, s, new Vector2(93, y + 1), new Vector2(76, 11), 9),
                PageLabel(p, s, new Vector2(168, y + 1), new Vector2(19, 11), 9, align: HorizontalAlignment.Right),
                GroupBar(p, s, new Vector2(5, y + 14), 5, new Color(0.85f, 0.15f, 0.12f)),
                GroupBar(p, s, new Vector2(5, y + 21), 3, new Color(0.2f, 0.75f, 0.25f)),
                GroupBar(p, s, new Vector2(5, y + 25), 3, new Color(0.9f, 0.8f, 0.2f)),
                icons));
        }

        // The skin's buttons, 58 wide at x 5 / 66 / 127, page y 308 to 398.
        Button B(string text, int x, int y, System.Action? pressed)
        {
            var b = new Button { Text = text, Position = new Vector2(x, y) * s, FocusMode = Control.FocusModeEnum.None, ClipText = true };
            StyleButton(b, s, new Vector2(58, 17));
            if (pressed is null)
            {
                b.Disabled = true;
                b.TooltipText = "Not built yet";
            }
            else
                b.Pressed += pressed;
            p.AddChild(b);
            return b;
        }
        _groupInvite = B("INVITE", 5, 308, () => GroupSend(() => _session!.InviteToGroupAsync(), "invite"));
        _groupDisband = B("DISBAND", 66, 308, () => GroupSend(() => _session!.DisbandFromGroupAsync(), "disband"));
        B("MINI", 127, 308, null);
        B("FIND", 5, 326, null);
        B("SET LFG", 66, 326, null);
        B("BENEFITS", 127, 326, null);
        _groupAssist = B("ASSIST", 5, 344, () => TargetCommand("/assist"));
        B("WARMAP", 66, 344, null);
        B("MAP", 127, 344, ToggleMap);
        B("FOLLOW", 5, 380, () => Follow(5f));
        B("STICK", 66, 380, () => Follow(2f));
        B("FACE", 127, 380, FaceTarget);
        B("CGF", 5, 398, null);
        B("OPTIONS", 66, 398, null);
        _groupInvite.TooltipText = "Invite your target into the group";
        _groupDisband.TooltipText = "Remove your target from the group (as its leader), or leave it";

        if (!_groupSubscribed)
        {
            _groupSubscribed = true;
            int logged = -1;
            _bridge.GroupChanged += g =>
            {
                _statsDirty = true;
                foreach (var (id, e) in _entities)   // group members' plates take the group colour
                    if (e.IsPlayer)
                        RestylePlate(id);
                if (g.Members.Count != logged)
                    GD.Print($"[world] group: {(g.InGroup ? string.Join(", ", g.Members.Select(m => $"{m.Name} ({m.ClassName} {m.Level})")) : "none")}");
                logged = g.Members.Count;
            };
            _bridge.DialogArrived += ShowDialog;
        }
    }

    private static HudBar GroupBar(Control p, float s, Vector2 at, float height, Color colour)
    {
        var bar = new HudBar(72 * s, height * s, colour) { Position = at * s };
        p.AddChild(bar);
        return bar;
    }

    private void FillGroup()
    {
        GroupState g = _bridge.Group;
        for (int i = 0; i < _groupRows.Count; i++)
        {
            GroupRow row = _groupRows[i];
            bool used = i < g.Members.Count;
            GroupMemberInfo? m = used ? g.Members[i] : null;
            GroupMemberStatus? st = used ? g.Status(i) : null;
            var (open, close, colour) = NameMarker(st?.Flags ?? GroupMemberFlags.None);
            row.Name.Text = m is null ? "" : open + m.Name + close;
            row.Name.AddThemeColorOverride("font_color", colour);
            row.Level.Text = m is null ? "" : m.Level.ToString();
            row.Class.Text = m?.ClassName ?? "";
            row.Health.Set(st is null ? -1 : st.Health);
            row.Endurance.Set(st is null ? -1 : st.Endurance);
            row.Power.Set(st is null ? -1 : st.Mana == 0xFF ? 0 : st.Mana);   // 0xff: no power value, shown as 0
            IReadOnlyList<ushort> icons = used ? g.Icons(i) : [];
            for (int k = 0; k < row.Icons.Length; k++)
                row.Icons[k].Texture = k < icons.Count ? IconPictureTexture(icons[k]) : null;
            bool targeted = m is not null && m.ObjectId == _target;
            row.Back.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, used ? 0.4f : 0.2f),
                BorderColor = targeted ? new Color(1f, 0.75f, 0f, 0.8f) : new Color(0.55f, 0.85f, 1f, 0.2f),
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            });
            row.Back.MouseDefaultCursorShape = used ? Control.CursorShape.PointingHand : Control.CursorShape.Arrow;
        }
        // Dimmed where the server would refuse: INVITE needs a player of our realm not in our group, while we lead or have
        // no group; DISBAND needs a group; ASSIST a target of our realm (as the target window's).
        Entity? t = _target != 0 && _entities.TryGetValue(_target, out Entity? e) ? e : null;
        bool ours = t is not null && t.Realm != 0 && (PlayerRealm == Realm.None || t.Realm == (int)PlayerRealm);
        bool leading = !g.InGroup || g.Members[0].Name == (_info?.Name ?? PlayerName);
        Dim(_groupInvite, !(ours && t!.IsPlayer && g.IndexOf(_target) < 0 && leading && g.Members.Count < GroupRows));
        Dim(_groupDisband, !g.InGroup);
        Dim(_groupAssist, !ours);
    }

    /// <summary>A member name's marker and colour: the first state bit set, in the client's order (daoc-re, group page Q1).</summary>
    internal static (string Open, string Close, Color Colour) NameMarker(GroupMemberFlags f)
    {
        foreach (var (bit, open, close, r, g, b) in NameMarkers)
            if ((f & bit) != 0)
                return (open, close, Color.Color8(r, g, b));
        return ("", "", Colors.White);
    }

    private static readonly (GroupMemberFlags Bit, string Open, string Close, byte R, byte G, byte B)[] NameMarkers =
    [
        (GroupMemberFlags.LinkDead, "*", "*", 99, 99, 251),
        (GroupMemberFlags.Dead, "=", "=", 255, 255, 255),
        (GroupMemberFlags.Mezzed, "!", "!", 0, 254, 250),
        (GroupMemberFlags.Diseased, "-", "-", 254, 131, 49),
        (GroupMemberFlags.Poisoned, "$", "$", 156, 255, 133),
        (GroupMemberFlags.Nearsighted, "^", "^", 210, 127, 252),
        (GroupMemberFlags.OtherRegion, "[", "]", 147, 145, 145),
        (GroupMemberFlags.Flag80, ">", "<", 255, 255, 0),
    ];

    // The client reads a 0x70 icon as a spells.csv id and draws that spell's icon (daoc-re, group page Q3). OpenDAoC writes
    // a spell effect's spell.Icon, which holds the spells.csv id (its DB column is misnamed); only its built-in ability
    // effects (Berserk 479, Engage 421, ...) send icons.csv numbers, which come out as the wrong picture here, as in retail.
    private Texture2D? IconPictureTexture(ushort spell)
    {
        _iconTable ??= LoadIconTable();
        return _iconTable.Resolve(DaocAssets.Ui.IconKind.Spell, spell) is DaocAssets.Ui.IconPicture p ? IconTexture(p.Base) : null;
    }

    private void TargetGroupMember(int index)
    {
        if (_session?.IsDead == true)
            return;
        GroupState g = _bridge.Group;
        SetTarget(index < g.Members.Count ? g.Members[index].ObjectId : (ushort)0);   // an empty row clears the target
        _statsDirty = true;
    }

    private void GroupSend(System.Func<System.Threading.Tasks.Task> send, string what)
    {
        if (_session is null)
            return;
        GD.Print($"[world] group {what} (target {_target})");
        _ = Send(send, "group " + what);
    }

    // ----- dialogs -----

    private void BuildDialog(CanvasLayer overlay)
    {
        _dialogWindow = new HudWindow("dialog", new Vector2(256, 96), UiScale, screen => new Vector2(screen.X / 2 - 128, screen.Y / 2 - 80)) { Visible = false };
        _dialogText = _dialogWindow.Label(new Vector2(8, 6), new Vector2(240, 68), 8, centred: true);
        _dialogText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _dialogText.ClipText = false;
        _dialogText.MaxLinesVisible = 7;
        _dialogYes = _dialogWindow.Button("ACCEPT", new Vector2(69, 76), 58, () => AnswerDialog(true), 16);
        _dialogNo = _dialogWindow.Button("DECLINE", new Vector2(129, 76), 58, () => AnswerDialog(false), 16);
        _dialogOk = _dialogWindow.Button("OK", new Vector2(99, 76), 58, () => AnswerDialog(true), 16);
        _dialogOk.Visible = false;   // one set at a time: YES / NO, or OK
        overlay.AddChild(_dialogWindow);
        if (_dialog is Dialog open)
            ShowDialog(open);
    }

    private void ShowDialog(Dialog d)
    {
        if (_dialogWindow is null || !IsInstanceValid(_dialogWindow))
            return;
        _dialog = d;
        _dialogText!.Text = d.Text;
        _dialogText.AutowrapMode = d.Wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off;
        _dialogYes!.Visible = _dialogNo!.Visible = d.YesNo;
        _dialogOk!.Visible = !d.YesNo;
        _dialogWindow.Visible = true;
        GD.Print($"[world] dialog {d.DialogCode} ({(d.YesNo ? "yes/no" : "ok")}): {d.Text.Replace('\n', ' ')}");
        if (_acceptDialogs)
            AnswerDialog(true);
    }

    private void AnswerDialog(bool yes)
    {
        if (_dialog is not Dialog d)
            return;
        _dialog = null;
        if (_dialogWindow is not null)
            _dialogWindow.Visible = false;
        GD.Print($"[world] dialog {d.DialogCode} answered {(yes ? "yes" : "no")}");
        if (_session is not null)
            _ = Send(() => _session.AnswerDialogAsync(d, yes), "dialog answer");
    }

    private bool GroupWindowsHave(Vector2 at) => _dialogWindow is { Visible: true } && IsInstanceValid(_dialogWindow) && _dialogWindow.GetGlobalRect().HasPoint(at);
}
