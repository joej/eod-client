// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>
/// The group packets and dialogs, as OpenDAoC @f0aaa1e writes and reads them (the 1.127e capture has none): the bytes
/// below follow PacketLib1125.SendGroupWindowUpdate, WriteGroupMemberUpdate / WriteGroupMemberIconsUpdate,
/// PacketLib168.SendGroupInviteCommand and DialogResponseHandler field by field.
/// </summary>
public class GroupPacketTests
{
    private static byte[] Pascal(string s) => [(byte)s.Length, .. Encoding.Latin1.GetBytes(s)];

    // A two-member list: Testjoe (Armsman 20, object 0x0123) leading, Quitcheck (Fighter 5, object 0x0456).
    private static readonly byte[] TwoMembers =
        [6, 2, .. Pascal("Testjoe"), .. Pascal("Armsman"), 0x01, 0x23, 20, .. Pascal("Quitcheck"), .. Pascal("Fighter"), 0x04, 0x56, 5];

    [Fact]
    public void TheGroupListReadsLeaderFirst()
    {
        GroupWindow w = GroupWindow.Parse(TwoMembers);
        Assert.Equal([new GroupMemberInfo("Testjoe", "Armsman", 0x0123, 20), new GroupMemberInfo("Quitcheck", "Fighter", 0x0456, 5)], w.Members);
        Assert.Equal(TwoMembers, w.ToBytes());
        Assert.Empty(GroupWindow.Parse([6, 0]).Members);   // "a 06 00 packet is sent when logging in"
    }

    [Fact]
    public void StatusAndIconRecordsReadByIndex()
    {
        // Member 1 at 80% health, 50% power, full endurance, mezzed; member 2 in another region; then the icon records.
        byte[] status = [0x21, 80, 50, 100, 0x02, 0x22, 0, 0, 0, 0x20, 0];
        GroupMemberUpdate u = GroupMemberUpdate.Parse(status);
        Assert.Equal([new GroupMemberStatus(1, 80, 50, 100, GroupMemberFlags.Mezzed), new GroupMemberStatus(2, 0, 0, 0, GroupMemberFlags.OtherRegion)], u.Statuses);
        Assert.Equal(status, u.ToBytes());

        byte[] icons = [0x80, 2, 0, 0x00, 0x10, 0, 0x01, 0x2C, 0x81, 0, 0];
        u = GroupMemberUpdate.Parse(icons);
        Assert.Equal(2, u.Icons.Count);
        Assert.Equal([(ushort)0x10, (ushort)0x12C], u.Icons[0].Icons);
        Assert.Empty(u.Icons[1].Icons);
        Assert.Equal(icons, u.ToBytes());

        Assert.Throws<InvalidDataException>(() => GroupMemberUpdate.Parse([0x41, 0]));
    }

    [Fact]
    public void TheBridgeKeepsTheGroupAndClearsItOnLeaving()
    {
        var bridge = new NetworkBridge();
        int changes = 0;
        bridge.GroupChanged += _ => changes++;
        bridge.Dispatch(new ServerPacket(GroupMemberUpdate.Code, [0x21, 80, 50, 100, 0, 0]));   // the join's status comes first
        bridge.Dispatch(new ServerPacket(0x16, TwoMembers));
        Assert.Equal(2, changes);
        Assert.True(bridge.Group.InGroup);
        Assert.Equal(1, bridge.Group.IndexOf(0x0456));
        Assert.Equal((byte)80, bridge.Group.Status(1)!.Health);
        bridge.Dispatch(new ServerPacket(0x16, [6, 0]));
        Assert.False(bridge.Group.InGroup);
        Assert.Null(bridge.Group.Status(1));
    }

    [Fact]
    public void AnInviteDialogIsAnsweredWithItsDataWords()
    {
        // SendGroupInviteCommand: 0, code 5, the inviter's session id, 6 zero bytes, yes/no, no wrap, the text, 0.
        const string text = "Testjoe has invited you to join\nhis group. Do you wish to join?";
        byte[] payload = [0, 5, 0x00, 0x07, 0, 0, 0, 0, 0, 0, 1, 0, .. Encoding.Latin1.GetBytes(text), 0];
        Dialog d = Dialog.Parse(payload);
        Assert.Equal(new Dialog(Dialog.GroupInvite, 7, 0, 0, true, false, text), d);
        Assert.Equal(payload, d.ToBytes());
        // DialogResponseHandler reads data1, data2, data3 (u16 BE), the code, the answer (1 accepts).
        Assert.Equal(new byte[] { 0, 7, 0, 0, 0, 0, 5, 1 }, d.Response(true));
        Assert.Equal(new byte[] { 0, 7, 0, 0, 0, 0, 5, 0 }, d.Response(false));
    }
}
