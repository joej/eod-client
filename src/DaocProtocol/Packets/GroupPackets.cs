// SPDX-License-Identifier: GPL-3.0-or-later
//
// Groups (roadmap M10: the stats panel's Group page) and the server's yes/no dialogs, which carry group invites. Layouts
// from OpenDAoC-Core @f0aaa1e, GPL-3.0 (field order only): PacketLib1125.SendGroupWindowUpdate (0x16 subcode 6),
// PacketLib1125.WriteGroupMemberUpdate and WriteGroupMemberIconsUpdate inside PacketLib168.SendGroupMembersUpdate and
// SendGroupMembersIconsUpdate (0x70), PacketLib168.SendDialogBox / SendGroupInviteCommand / SendCustomDialog (0x81),
// DialogResponseHandler (0x82), InviteToGroupHandler (0x87) and DisbandFromGroupHandler (0x9F). The 1.127e capture
// has none of them (its character was never in a group).

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>One member as the group list names it.</summary>
public sealed record GroupMemberInfo(string Name, string ClassName, ushort ObjectId, byte Level);

/// <summary>
/// S→C 0x16 subcode 6, the group list: subcode, member count, then per member u8-length name, u8-length class name ("NPC"
/// for a pet or hireling), u16 BE object id, u8 level. The leader comes first; a member's place in the list is the index
/// that <see cref="GroupMemberUpdate"/> uses. Count 0: not in a group (sent at world entry and on leaving one).
/// </summary>
public sealed record GroupWindow(IReadOnlyList<GroupMemberInfo> Members)
{
    public const byte Subcode = 6;

    public static GroupWindow Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        r.U8();
        int count = r.U8();
        var members = new List<GroupMemberInfo>(count);
        for (int i = 0; i < count; i++)
            members.Add(new GroupMemberInfo(r.StringU8(), r.StringU8(), r.U16BE(), r.U8()));
        return new GroupWindow(members);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8(Subcode).U8((byte)Members.Count);
        foreach (GroupMemberInfo m in Members)
            w.StringU8(m.Name).StringU8(m.ClassName).U16BE(m.ObjectId).U8(m.Level);
        return w.ToArray();
    }
}

/// <summary>A group member's state flags (0x70).</summary>
[Flags]
public enum GroupMemberFlags : byte
{
    None = 0, Dead = 0x01, Mezzed = 0x02, Diseased = 0x04, Poisoned = 0x08, LinkDead = 0x10, OtherRegion = 0x20,
    /// <summary>A range debuff (the server's comment: "NS", nearsight).</summary>
    Nearsighted = 0x40,
}

/// <summary>One member's health, power and endurance (percent) and state, from 0x70.</summary>
public sealed record GroupMemberStatus(byte Index, byte Health, byte Mana, byte Endurance, GroupMemberFlags Flags);

/// <summary>One member's effect icons (icons.csv ids), from 0x70.</summary>
public sealed record GroupMemberIcons(byte Index, IReadOnlyList<ushort> Icons);

/// <summary>
/// S→C 0x70 GroupMemberUpdate: records until a 0 byte. A record's first byte is a kind in the high bits and the member's
/// index (0 = the leader, as listed by <see cref="GroupWindow"/>) in the low five: 0x20 | index, then health, mana and
/// endurance percent and a <see cref="GroupMemberFlags"/> byte (a member in another region: all 0 and the 0x20 flag);
/// 0x80 | index, then a count and per effect a 0 byte and a u16 BE icon (count 0 for another region). One packet holds
/// records of one kind, but the reader takes them mixed.
/// </summary>
public sealed record GroupMemberUpdate(IReadOnlyList<GroupMemberStatus> Statuses, IReadOnlyList<GroupMemberIcons> Icons)
{
    public const byte Code = 0x70;
    public const byte StatusKind = 0x20, IconsKind = 0x80;

    public static GroupMemberUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        var statuses = new List<GroupMemberStatus>();
        var icons = new List<GroupMemberIcons>();
        while (r.Remaining > 0)
        {
            byte head = r.U8();
            if (head == 0)
                break;
            byte index = (byte)(head & 0x1F);
            if ((head & IconsKind) != 0)
            {
                int count = r.U8();
                var list = new List<ushort>(count);
                for (int i = 0; i < count; i++)
                {
                    r.U8();
                    list.Add(r.U16BE());
                }
                icons.Add(new GroupMemberIcons(index, list));
            }
            else if ((head & StatusKind) != 0)
                statuses.Add(new GroupMemberStatus(index, r.U8(), r.U8(), r.U8(), (GroupMemberFlags)r.U8()));
            else
                throw new InvalidDataException($"0x70: unknown record kind 0x{head:X2}.");
        }
        return new GroupMemberUpdate(statuses, icons);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter();
        foreach (GroupMemberStatus s in Statuses)
            w.U8((byte)(StatusKind | s.Index)).U8(s.Health).U8(s.Mana).U8(s.Endurance).U8((byte)s.Flags);
        foreach (GroupMemberIcons i in Icons)
        {
            w.U8((byte)(IconsKind | i.Index)).U8((byte)i.Icons.Count);
            foreach (ushort icon in i.Icons)
                w.U8(0).U16BE(icon);
        }
        return w.U8(0).ToArray();
    }
}

/// <summary>
/// S→C 0x81 Dialog, a message box: u8 0, u8 code (what it is about: 5 a group invite, 3 a guild invite, 6 a server
/// script's question, ...), three u16 BE data words the answer echoes (a group invite: data1 = the inviter's session id), a
/// u16 BE the client ignores, u8 type (0 an OK box, 1 yes/no), u8 wrap (1: wrap the text; 0: it has its own line
/// breaks), the text and a closing 0.
/// </summary>
public sealed record Dialog(byte DialogCode, ushort Data1, ushort Data2, ushort Data3, bool YesNo, bool Wrap, string Text)
{
    public const byte Code = 0x81;
    public const byte GroupInvite = 5, GuildInvite = 3, Custom = 6;

    public static Dialog Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        r.U8();
        byte code = r.U8();
        ushort d1 = r.U16BE(), d2 = r.U16BE(), d3 = r.U16BE();
        r.U16BE();
        bool yesNo = r.U8() != 0, wrap = r.U8() != 0;
        return new Dialog(code, d1, d2, d3, yesNo, wrap, r.StringRestNulTerminated());
    }

    public byte[] ToBytes() =>
        new PayloadWriter().U8(0).U8(DialogCode).U16BE(Data1).U16BE(Data2).U16BE(Data3).U16BE(0).U8((byte)(YesNo ? 1 : 0))
            .U8((byte)(Wrap ? 1 : 0)).Bytes(System.Text.Encoding.Latin1.GetBytes(Text)).U8(0).ToArray();

    /// <summary>C→S 0x82 DialogResponse: the three data words back, the code, then the answer (1 yes / OK, 0 no).</summary>
    public const byte ResponseCode = 0x82;

    public byte[] Response(bool yes) =>
        new PayloadWriter().U16BE(Data1).U16BE(Data2).U16BE(Data3).U8(DialogCode).U8((byte)(yes ? 1 : 0)).ToArray();
}

/// <summary>
/// C→S group requests. Neither has a payload the server reads: 0x87 invites the current target (the server checks it is
/// a player of ours not in a group, and that we lead ours or have none); 0x9F disbands the current target if it is in our
/// group and we lead it, otherwise ourselves (leaving the group).
/// </summary>
public static class GroupRequest
{
    public const byte InviteCode = 0x87, DisbandCode = 0x9F;
}
