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
            RaceName: "Highlander", Model: 55562, Region: 1, ClassId: 1, Realm: Realm.Albion, Race: 3, Female: false)
        {
            // Bytes 44 44 20 01 00 20, then 00 00, step 02, mood 00 (read off the recording by hand).
            Look = new CharacterLook(0x44, 0x44, 0x20, 0x01, 0x00, 0x20, 0x00),
            CustomisationStep = 2,
        },
            character);
    }

    [Fact]
    public void AppearanceBytesDecodeOneSettingPerHalfByte()
    {
        var look = new CharacterLook(EyeSize: 0x37, LipSize: 0x52, EyeColor: 0x63, HairColor: 0x0B,
            FaceType: 0x40, HairStyle: 0x50, MoodType: 0);
        Assert.Equal((4, 5, 11, 6, 3), (look.Face, look.Hair, look.HairColour, look.EyeColour, look.SkinTone));
        Assert.Equal([7, 3, 5, 2, 4], Enumerable.Range(0, 5).Select(look.Slider));
    }

    [Fact]
    public void PlayerCreateCarriesTheAppearanceInItsOwnOrder()
    {
        // PacketLib1124.SendPlayerCreate: x y z (f32 LE), session, object, heading, model (u16 BE), level,
        // flags, then eye size, lip size, MOOD, eye colour, hair colour, face, hair style, 3 bytes, name.
        byte[] payload = new PayloadWriter()
            .F32LE(1f).F32LE(2f).F32LE(3f).U16BE(7).U16BE(0x1234).U16BE(1024).U16BE(32).U8(20).U8(0x04)
            .U8(0x44).U8(0x45).U8(0x09).U8(0x21).U8(0x03).U8(0x10).U8(0x20)
            .Zeros(3).U8(7).Bytes("Plooker"u8).U8(0).ToArray();
        PlayerCreate p = PlayerCreate.Parse(payload);
        Assert.Equal(("Plooker", (ushort)32, (ushort)0x1234), (p.Name, p.Model, p.ObjectId));
        Assert.Equal(new CharacterLook(0x44, 0x45, 0x21, 0x03, 0x10, 0x20, 0x09), p.Look);
        Assert.Equal((1, 2, 3, 2, 1), (p.Look.Face, p.Look.Hair, p.Look.HairColour, p.Look.EyeColour, p.Look.SkinTone));
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
