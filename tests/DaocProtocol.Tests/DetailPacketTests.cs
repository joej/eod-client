// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;

namespace DaocProtocol.Tests;

/// <summary>
/// The item window: 0xD8 DetailRequest (checked against the one in the 1.127e capture), the 0xC4 text window that answers
/// it (laid out as OpenDAoC writes it; the capture has none), the item con grid and the mini_info lines for the capture's items.
/// </summary>
public class DetailPacketTests
{
    private static readonly List<InventoryItem> CaptureItems = CaptureFile.Login1127e().ServerPackets()
        .Where(p => p.Code == InventoryUpdate.Code)
        .SelectMany(p => InventoryUpdate.Parse(p.Payload).Items.Select(i => i.Item)).ToList();

    private static InventoryItem Item(string name) => CaptureItems.First(i => i.Name == name);

    [Fact]
    public void TheRequestIsTypePaddingIdExtra()
    {
        // The capture's one 0xD8: a spell delve, type 24, id 30125, with padding bytes the server doesn't read.
        ClientPacket recorded = CaptureFile.Login1127e().ClientPackets().Single(p => p.Code == DetailRequest.Code);
        Assert.Equal(12, recorded.Payload.Length);
        byte[] ours = DetailRequest.Payload(24, 30125);
        Assert.Equal(recorded.Payload[..2], ours[..2]);
        Assert.Equal(recorded.Payload[4..], ours[4..]);
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 0, 0, 41, 0, 0, 0, 0 }, DetailRequest.Payload(DetailRequest.InventoryItem, 41));
    }

    [Fact]
    public void ATextWindowReadsCaptionAndLines()
    {
        // mode 0, more 0, "practice sword", lines 1 and 2, the closing 0.
        byte[] payload = [0, 0, 14, .. "practice sword"u8, 1, 3, .. "Abc"u8, 2, 0, 0];
        DetailWindow w = DetailWindow.Parse(payload);
        Assert.Equal("practice sword", w.Caption);
        Assert.Equal(["Abc", ""], w.Lines);
        Assert.Equal(payload, w.ToBytes());
    }

    [Fact]
    public void AContinuedWindowIsFlaggedToTheNextOne()
    {
        var bridge = new NetworkBridge();
        var seen = new List<(string, bool)>();
        bridge.DetailWindowArrived += (w, continues) => seen.Add((w.Lines[0], continues));
        foreach ((byte more, string line) in new[] { ((byte)1, "a"), ((byte)0, "b"), ((byte)0, "c") })
            bridge.Dispatch(new ServerPacket(DetailWindow.Code, new DetailWindow(0, more, "x", [line]).ToBytes()));
        Assert.Equal([("a", false), ("b", true), ("c", false)], seen);
    }

    [Theory]
    // A level-10 player: OpenDAoC's item grid row 10 (4 grey, 2 green, 3 blue, 3 yellow, 2 orange, 3 red, then purple).
    [InlineData(10, 3, ConColor.Grey)]
    [InlineData(10, 4, ConColor.Green)]
    [InlineData(10, 8, ConColor.Blue)]
    [InlineData(10, 9, ConColor.Yellow)]
    [InlineData(10, 13, ConColor.Orange)]
    [InlineData(10, 16, ConColor.Red)]
    [InlineData(10, 17, ConColor.Purple)]
    // Level 1: nothing below yellow; level 50: 33 grey levels.
    [InlineData(1, 0, ConColor.Yellow)]
    [InlineData(50, 32, ConColor.Grey)]
    [InlineData(50, 33, ConColor.Green)]
    public void ItemsConByTheItemGrid(int player, int item, ConColor expected) =>
        Assert.Equal(expected, ConColors.OfItem(player, item));

    [Fact]
    public void TheSwordsLines()
    {
        string[] l = ItemInfo.MiniInfo(Item("practice sword"));
        Assert.Equal(["practice sword", "Slash Weapon", "Slash Damage", "Dps: 16.5  Spd: 2.5", "Weight: 1.0 lbs"], l[..5]);
        Assert.StartsWith("Qua: 90%", l[6]);
    }

    [Fact]
    public void TheShieldsLines()
    {
        InventoryItem shield = Item("small training shield");
        Assert.Equal((byte)1, shield.Value1);   // small
        Assert.Equal("Small Shield", ItemInfo.MiniInfo(shield)[1]);
    }

    [Fact]
    public void EveryRecordedItemHasEightLinesAndAName()
    {
        foreach (InventoryItem item in CaptureItems.Where(i => !i.IsEmpty))
        {
            string[] l = ItemInfo.MiniInfo(item);
            Assert.Equal(8, l.Length);
            Assert.Equal(item.Name, l[0]);
            Assert.All(l, s => Assert.NotNull(s));
        }
    }
}
