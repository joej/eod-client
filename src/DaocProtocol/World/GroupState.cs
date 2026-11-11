// SPDX-License-Identifier: GPL-3.0-or-later
//
// The group as the client knows it: the list (0x16 subcode 6) and each member's latest state and effect icons (0x70),
// matched by the member's index in the list.

using DaocProtocol.Packets;

namespace DaocProtocol.World;

public sealed class GroupState
{
    private readonly Dictionary<byte, GroupMemberStatus> _status = [];
    private readonly Dictionary<byte, IReadOnlyList<ushort>> _icons = [];

    /// <summary>The members, leader first; empty when not in a group.</summary>
    public IReadOnlyList<GroupMemberInfo> Members { get; private set; } = [];

    public bool InGroup => Members.Count > 0;

    /// <summary>A new list replaces the old. Statuses and icons are kept by index (the server sends them before the
    /// list when someone joins); leaving the group clears them.</summary>
    public void Set(GroupWindow window)
    {
        Members = window.Members;
        if (Members.Count == 0)
        {
            _status.Clear();
            _icons.Clear();
        }
    }

    public void Apply(GroupMemberUpdate update)
    {
        foreach (GroupMemberStatus s in update.Statuses)
            _status[s.Index] = s;
        foreach (GroupMemberIcons i in update.Icons)
            _icons[i.Index] = i.Icons;
    }

    /// <summary>The member's state, or null before the server has sent one.</summary>
    public GroupMemberStatus? Status(int index) => _status.GetValueOrDefault((byte)index);

    public IReadOnlyList<ushort> Icons(int index) => _icons.GetValueOrDefault((byte)index) ?? [];

    /// <summary>The member's index, or -1 when that object isn't in the group.</summary>
    public int IndexOf(ushort objectId)
    {
        for (int i = 0; i < Members.Count; i++)
            if (Members[i].ObjectId == objectId)
                return i;
        return -1;
    }
}
