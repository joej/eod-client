// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>Character-screen packets checked against the recorded 1.127e session (see Fixtures/README.md).</summary>
public class CharacterPacketTests
{
    private static readonly CaptureFile Capture = CaptureFile.Login1127e();

    private static List<ClientPacket> Client() => Capture.ClientPackets();

    private static List<ServerPacket> Server() => Capture.ServerPackets();

    [Fact]
    public void RequestsMatchRecordedClient()
    {
        List<ClientPacket> client = Client();
        // Recorded order after login: 0x10, 0xFC realm 0, 0x10, 0xFC realm 1 (packets 4–7).
        (byte Code, byte[] Payload)[] expected =
        [
            (CharacterSelectRequest.Code, CharacterSelectRequest.Payload()),
            (CharacterOverviewRequest.Code, CharacterOverviewRequest.Payload(Realm.None)),
            (CharacterSelectRequest.Code, CharacterSelectRequest.Payload()),
            (CharacterOverviewRequest.Code, CharacterOverviewRequest.Payload(Realm.Albion)),
        ];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Code, client[4 + i].Code);
            Assert.Equal(expected[i].Payload, client[4 + i].Payload);
        }
    }

    [Fact]
    public void SessionIdIsLittleEndian()
    {
        ServerPacket recorded = Server()[3];
        Assert.Equal(SessionId.Code, recorded.Code);
        Assert.Equal(new byte[] { 0x01, 0x00 }, recorded.Payload);
        Assert.Equal(1, SessionId.Parse(recorded.Payload));
        // ...and the recorded client really used session id 1 afterwards.
        Assert.Equal(1, Client()[5].SessionId);
    }

    [Fact]
    public void RealmReplyParsesRecordedServer()
    {
        ServerPacket recorded = Server()[4];
        Assert.Equal(RealmReply.Code, recorded.Code);
        Assert.Equal(Realm.None, RealmReply.Parse(recorded.Payload));
    }

    [Fact]
    public void OverviewParsesRecordedServer()
    {
        ServerPacket recorded = Server()[6];
        Assert.Equal(CharacterOverview.Code, recorded.Code);

        CharacterOverview overview = CharacterOverview.Parse(recorded.Payload);

        Assert.Equal(1u, overview.Flags);
        // Expected values decoded independently from the recording.
        CharacterSummary character = Assert.Single(overview.Characters);
        Assert.Equal(new CharacterSummary(
            Slot: 0, Level: 50, Name: "Lilillyn", Location: "Camelot Hills", ClassName: "Paladin",
            RaceName: "Highlander", Model: 55562, Region: 1, ClassId: 1, Realm: Realm.Albion, Race: 3, Female: false),
            character);
    }

    [Fact]
    public void EmptyAccountGetsHeaderOnly()
    {
        // PacketLib1126.SendCharacterOverview with no characters: four zero u32s, no slots.
        CharacterOverview overview = CharacterOverview.Parse(new byte[16]);
        Assert.Empty(overview.Characters);
    }

    [Fact]
    public void OverviewRejectsTruncatedAndPaddedPayloads()
    {
        byte[] payload = Server()[6].Payload;
        Assert.Throws<InvalidDataException>(() => CharacterOverview.Parse(payload.AsSpan(0, payload.Length - 1)));
        Assert.Throws<InvalidDataException>(() => CharacterOverview.Parse([.. payload, 0]));
    }
}
