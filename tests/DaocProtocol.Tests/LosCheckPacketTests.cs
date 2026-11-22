// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>0xD0 both ways: the server's line-of-sight question and the client's answer.</summary>
public sealed class LosCheckPacketTests
{
    [Fact]
    public void TheQuestionIsReadAsOpenDaocWritesIt()
    {
        // LosCheckHandler.StartLosCheck: source id, target id (WriteShort, big-endian), then two zero shorts.
        Assert.Equal(new LosCheck(0x1234, 0x0042), LosCheck.Parse([0x12, 0x34, 0x00, 0x42, 0, 0, 0, 0]));
    }

    [Fact]
    public void TheAnswerSetsBit0x100OnlyWhenInSight()
    {
        // CheckLosResponseHandler: ReadShort source, ReadShort target, (ReadShort & 0x100) == 0x100 means in sight.
        Assert.Equal(0xD0, LosCheckReply.Code);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x00, 0x42, 0x01, 0x00, 0, 0 }, LosCheckReply.Payload(0x1234, 0x42, inSight: true));
        Assert.Equal(new byte[] { 0x12, 0x34, 0x00, 0x42, 0x00, 0x00, 0, 0 }, LosCheckReply.Payload(0x1234, 0x42, inSight: false));
    }

    [Fact]
    public void TheBridgeReportsTheQuestion()
    {
        var bridge = new NetworkBridge();
        LosCheck? seen = null;
        bridge.LosCheckAsked += c => seen = c;
        bridge.Dispatch(new ServerPacket(LosCheck.Code, [0x00, 0x07, 0x00, 0x09, 0, 0, 0, 0]));
        Assert.Equal(new LosCheck(7, 9), seen);
        Assert.False(bridge.UnhandledCounts.ContainsKey(LosCheck.Code));
    }
}
