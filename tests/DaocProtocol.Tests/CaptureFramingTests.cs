// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using DaocProtocol.Net;

namespace DaocProtocol.Tests;

/// <summary>
/// Framing checked against real traffic: a recorded 1.127e login session (see Fixtures/README.md).
/// The expected values come from the recording, not from our own reading of the protocol.
/// </summary>
public class CaptureFramingTests
{
    private static readonly CaptureFile Capture = CaptureFile.Login1127e();

    private static List<(ClientPacket Packet, byte[] Raw)> ReadClientStream()
    {
        var packets = new List<(ClientPacket, byte[])>();
        ReadOnlySpan<byte> rest = Capture.ClientStream;
        while (!rest.IsEmpty)
        {
            FrameResult result = ClientPacket.TryRead(rest, out ClientPacket? packet, out int consumed);
            Assert.True(result == FrameResult.Complete, $"packet {packets.Count}: {result}");
            packets.Add((packet!, rest[..consumed].ToArray()));
            rest = rest[consumed..];
        }

        return packets;
    }

    private static List<ServerPacket> ReadServerStream()
    {
        var packets = new List<ServerPacket>();
        ReadOnlySpan<byte> rest = Capture.ServerStream;
        while (!rest.IsEmpty)
        {
            FrameResult result = ServerPacket.TryRead(rest, out ServerPacket? packet, out int consumed);
            Assert.True(result == FrameResult.Complete, $"packet {packets.Count}: {result}");
            packets.Add(packet!);
            rest = rest[consumed..];
        }

        return packets;
    }

    [Fact]
    public void ClientStreamSplitsIntoPacketsWithValidChecksums() =>
        Assert.Equal(171, ReadClientStream().Count);

    [Fact]
    public void ServerStreamSplitsExactly() =>
        Assert.Equal(1886, ReadServerStream().Count);

    [Fact]
    public void ClientPacketsRebuildByteForByte()
    {
        foreach (var (packet, raw) in ReadClientStream())
            Assert.Equal(raw, packet.ToBytes());
    }

    [Fact]
    public void ServerPacketsRebuildByteForByte()
    {
        Assert.Equal(Capture.ServerStream, ReadServerStream().SelectMany(p => p.ToBytes()).ToArray());
    }

    [Fact]
    public void LoginSequenceOpensAsRecorded()
    {
        byte[] expected = [0xF4, 0xF4, 0xA7, 0xA3, 0x10, 0xFC, 0x10, 0xFC, 0x9D, 0xA3, 0x10, 0xD4, 0xE8, 0xBF];
        Assert.Equal(expected, ReadClientStream().Take(expected.Length).Select(p => p.Packet.Code).ToArray());
    }

    [Fact]
    public void ClientSequenceNumbersCountUpFromZero()
    {
        var sequences = ReadClientStream().Select(p => (int)p.Packet.Sequence).ToList();
        Assert.Equal(Enumerable.Range(0, sequences.Count), sequences);
    }

    [Fact]
    public void ServerAnswersWithItsVersion()
    {
        ServerPacket first = ReadServerStream()[0];
        Assert.Equal(0x22, first.Code);
        Assert.Contains("1.127", Encoding.ASCII.GetString(first.Payload));
    }

    [Fact]
    public void CorruptedClientPacketIsRejected()
    {
        byte[] raw = ReadClientStream()[2].Raw; // the login request
        raw[ClientPacket.HeaderSize] ^= 0x01;
        Assert.Equal(FrameResult.BadChecksum, ClientPacket.TryRead(raw, out ClientPacket? packet, out int consumed));
        Assert.Null(packet);
        Assert.Equal(raw.Length, consumed);
    }

    [Fact]
    public void TruncatedPacketsAreIncomplete()
    {
        byte[] client = ReadClientStream()[0].Raw;
        for (int length = 0; length < client.Length; length++)
            Assert.Equal(FrameResult.Incomplete, ClientPacket.TryRead(client.AsSpan(0, length), out _, out _));

        byte[] server = ReadServerStream()[0].ToBytes();
        for (int length = 0; length < server.Length; length++)
            Assert.Equal(FrameResult.Incomplete, ServerPacket.TryRead(server.AsSpan(0, length), out _, out _));
    }
}
