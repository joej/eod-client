// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;
using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>Merchants: 0x17 pages from the server, 0x78 / 0x79 / 0xE0 from the client.</summary>
public sealed class MerchantPacketTests
{
    // PacketLib1125.SendMerchantWindow's bytes for one item, written out by hand (field order from OpenDAoC @f0aaa1e).
    private static byte[] ServerItem(byte index, byte level, byte v1, byte v2, byte handByte, byte typeByte, byte canUse,
        ushort weight, uint price, ushort model, string name)
    {
        var b = new List<byte> { index, level, v1, v2, handByte, typeByte, canUse };
        b.AddRange(BitConverter.GetBytes(weight));          // WriteShortLowEndian
        b.AddRange(BitConverter.GetBytes(price));           // WriteIntLowEndian
        b.AddRange(BitConverter.GetBytes(model));           // WriteShortLowEndian
        byte[] text = Encoding.Latin1.GetBytes(name);       // WritePascalStringIntLE: length + 1, bytes, 0
        b.AddRange(BitConverter.GetBytes((uint)text.Length + 1));
        b.AddRange(text);
        b.Add(0);
        return [.. b];
    }

    [Fact]
    public void APageIsReadAsOpenDaocWritesIt()
    {
        var bytes = new List<byte> { 2, MerchantWindowType.Normal, 1 };
        // A one-handed slashing sword (type 3, slash damage 2): DPS 16.5 → 165, speed 30, hand 0.
        bytes.AddRange(ServerItem(4, 5, 165, 30, 0 << 6, 2 << 6 | 3, 1, 30, 1234, 0x0003, "Short Sword"));
        // 20 arrows (type 43): value1 = pack size, weight = pack × weight; can't use.
        bytes.AddRange(ServerItem(29, 1, 20, 0, 0, 43, 0, 20, 99, 0x01FA, "Rough Arrows"));
        MerchantPage page = MerchantPage.Parse(bytes.ToArray());

        Assert.Equal(1, page.Page);
        Assert.Equal(MerchantWindowType.Normal, page.WindowType);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new MerchantItem(4, 5, 165, 30, 0, 2, 3, true, 30, 1234, 3, "Short Sword"), page.Items[0]);
        Assert.Equal(new MerchantItem(29, 1, 20, 0, 0, 0, 43, false, 20, 99, 0x01FA, "Rough Arrows"), page.Items[1]);
        Assert.Equal(1, page.Items[0].PackSize);
        Assert.Equal(20, page.Items[1].PackSize);
        Assert.Equal(bytes.ToArray(), page.ToBytes());
    }

    [Fact]
    public void AnEmptyMerchantSendsOnePageWithAPadByte()
    {
        // The else branch: count 0, window type, page 0, one more 0.
        MerchantPage page = MerchantPage.Parse([0, 0, 0, 0]);
        Assert.Empty(page.Items);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, page.ToBytes());
    }

    [Fact]
    public void BuySellAndAppraiseAreReadAsOpenDaocReadsThem()
    {
        // PlayerBuyRequestHandler: ReadInt x, ReadInt y (big-endian), ReadShort id, ReadShort slot, ReadByte count, ReadByte menu.
        byte[] buy = BuyRequest.Payload(561000.7f, 511700.2f, 0x0042, 1 * 30 + 4, 3, MerchantWindowType.Normal);
        Assert.Equal(16, buy.Length);                                       // retail: two unwritten bytes at the end
        Assert.Equal(561000u, BinaryPrimitives.ReadUInt32BigEndian(buy));
        Assert.Equal(511700u, BinaryPrimitives.ReadUInt32BigEndian(buy.AsSpan(4)));
        Assert.Equal(0x42, BinaryPrimitives.ReadUInt16BigEndian(buy.AsSpan(8)));
        Assert.Equal(34, BinaryPrimitives.ReadUInt16BigEndian(buy.AsSpan(10)));    // GameMerchant: page 34 / 30, index 34 % 30
        Assert.Equal(3, buy[12]);
        Assert.Equal(0, buy[13]);
        Assert.Equal(new byte[] { 0, 0 }, buy[14..]);

        // PlayerSellRequestHandler / PlayerAppraiseItemRequestHandler: ReadInt x, ReadInt y, ReadShort id, ReadShort slot.
        byte[] sell = SellRequest.Payload(100f, 200f, 0x0042, 41);
        Assert.Equal(new byte[] { 0, 0, 0, 100, 0, 0, 0, 200, 0, 0x42, 0, 41 }, sell);
        Assert.Equal(0x79, SellRequest.Code);
        Assert.Equal(0xE0, SellRequest.AppraiseCode);
        Assert.Equal(0x78, BuyRequest.Code);
    }

    [Fact]
    public void TheBridgeReportsEachPage()
    {
        var bridge = new NetworkBridge();
        var pages = new List<MerchantPage>();
        bridge.MerchantPageReceived += pages.Add;
        bridge.Dispatch(new ServerPacket(MerchantPage.Code, [0, 0, 0, 0]));
        Assert.Single(pages);
        Assert.False(bridge.UnhandledCounts.ContainsKey(MerchantPage.Code));
    }
}
