// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>Login packets checked against the recorded 1.127e session (see Fixtures/README.md).</summary>
public class LoginPacketTests
{
    private static readonly List<ClientPacket> Client = ReadAll<ClientPacket>(CaptureFile.Login1127e().ClientStream, ClientPacket.TryRead);
    private static readonly List<ServerPacket> Server = ReadAll<ServerPacket>(CaptureFile.Login1127e().ServerStream, ServerPacket.TryRead);

    private delegate FrameResult TryRead<T>(ReadOnlySpan<byte> buffer, out T? packet, out int consumed);

    private static List<T> ReadAll<T>(byte[] stream, TryRead<T> tryRead)
    {
        var packets = new List<T>();
        int pos = 0;
        while (pos < stream.Length)
        {
            Assert.Equal(FrameResult.Complete, tryRead(stream.AsSpan(pos), out T? packet, out int consumed));
            packets.Add(packet!);
            pos += consumed;
        }

        return packets;
    }

    [Fact]
    public void CryptKeyRequestMatchesRecordedClient()
    {
        ClientPacket recorded = Client[0];
        Assert.Equal(CryptKeyRequest.Code, recorded.Code);
        Assert.Equal(recorded.Payload, CryptKeyRequest.Payload(ClientVersion.V1127e));
        Assert.Equal(1127, ClientVersion.V1127e.ProtocolVersion);
    }

    [Fact]
    public void LoginRequestMatchesRecordedClient()
    {
        ClientPacket recorded = Client[2];
        Assert.Equal(LoginRequest.Code, recorded.Code);

        // The recorded client appends 13 bytes the server never reads; take them from the recording.
        byte[] trailer = recorded.Payload[^13..];
        Assert.Equal(recorded.Payload, LoginRequest.Payload("rustdaoc", "rustdaoc", trailer));
    }

    [Fact]
    public void LoginRequestLengthsCountTheTerminatingZero()
    {
        byte[] payload = LoginRequest.Payload("ab", "xyz");
        Assert.Equal(new byte[] { 3, 0, 0, 0, (byte)'a', (byte)'b', 0, 4, 0, 0, 0, (byte)'x', (byte)'y', (byte)'z', 0 }, payload);
    }

    [Fact]
    public void LoginRequestRejectsEmbeddedZero() =>
        Assert.Throws<ArgumentException>(() => LoginRequest.Payload("a\0b", "x"));

    [Fact]
    public void CryptKeyReplyParsesRecordedServer()
    {
        ServerPacket recorded = Server[0];
        Assert.Equal(CryptKeyReply.Code, recorded.Code);
        CryptKeyReply reply = CryptKeyReply.Parse(recorded.Payload);
        Assert.Equal(0u, reply.KeyLength);
        Assert.Equal("1.127", reply.Version);
    }

    [Fact]
    public void LoginGrantedParsesRecordedServer()
    {
        ServerPacket recorded = Server[1];
        Assert.Equal(LoginGranted.Code, recorded.Code);
        Assert.Equal(new LoginGranted("rustdaoc", "SOLODAOC", 5, 1, false), LoginGranted.Parse(recorded.Payload));
    }

    [Fact]
    public void LoginDeniedReadsTheReason() =>
        // Layout from PacketLib168.SendLoginDenied: reason, 0x01, two version bytes, 0x00.
        Assert.Equal(LoginError.IncorrectPassword, LoginDenied.Parse([0x01, 0x01, 0x01, 0x1b, 0x00]).Error);

    [Fact]
    public void SequencerNumbersPacketsLikeTheRecordedClient()
    {
        var sequencer = new ClientPacketSequencer();
        ClientPacket first = sequencer.Next(CryptKeyRequest.Code, CryptKeyRequest.Payload(ClientVersion.V1127e));
        ClientPacket second = sequencer.Next(LoginRequest.Code, LoginRequest.Payload("a", "b"));
        sequencer.SessionId = 1;
        ClientPacket third = sequencer.Next(0x10, [0]);

        Assert.Equal((0, 0), ((int)first.Sequence, (int)first.SessionId));
        Assert.Equal((1, 0), ((int)second.Sequence, (int)second.SessionId));
        Assert.Equal((2, 1), ((int)third.Sequence, (int)third.SessionId));
        Assert.Equal(CryptKeyRequest.Code, first.Code);
        Assert.Equal(0, first.PacketId >> 8);
    }
}
