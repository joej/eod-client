// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.World;

namespace DaocProtocol.Tests;

/// <summary>World-entry and keep-alive packets checked against the recorded 1.127e session (see Fixtures/README.md).</summary>
public class WorldPacketTests
{
    private static readonly CaptureFile Capture = CaptureFile.Login1127e();
    private static readonly List<ClientPacket> Client = Capture.ClientPackets();
    private static readonly List<ServerPacket> Server = Capture.ServerPackets();

    [Theory]
    [InlineData(13, 125)]     // world entry
    [InlineData(1415, 170)]   // later in the session (a speed buff or mount)
    public void MaxSpeedIsAPercentOfTheBaseRunSpeed(int index, int percent)
    {
        Assert.Equal(MaxSpeed.Code, Server[index].Code);
        MaxSpeed speed = MaxSpeed.Parse(Server[index].Payload);
        Assert.Equal(new MaxSpeed((ushort)percent, false, 0), speed);
        Assert.Equal(191f * percent / 100, speed.UnitsPerSecond);
    }

    [Fact]
    public void AnObjectCreateRequestCarriesTheIdAsTheServerReadsIt()
    {
        // OpenDAoC (1.126+) reads the id as u16 little-endian from the payload's start.
        byte[] payload = CreateObjectRequest.Payload(0x2A93);
        Assert.Equal(new byte[] { 0x93, 0x2A, 0, 0 }, payload);
        Assert.Equal(0x2A93, payload[0] | payload[1] << 8);
    }

    [Fact]
    public void AJumpPointRequestCarriesTheZonePointAsTheServerReadsIt()
    {
        // OpenDAoC 1.126+ (PlayerRegionChangeRequestHandler): ReadShortLowEndian, then the zonepoint table.
        // Zone point 6 is Camelot Hills' gate into Camelot City.
        Assert.Equal(0x90, RegionChangeRequest.Code);
        Assert.Equal(new byte[] { 0x06, 0x00 }, RegionChangeRequest.Payload(6));
        Assert.Equal(new byte[] { 0x2A, 0x01 }, RegionChangeRequest.Payload(0x012A));
    }

    [Fact]
    public void RequestsMatchRecordedClient()
    {
        // Recorded order after the character list: 9D A3 10 D4 E8 BF (packets 8–13), slot 0 in Albion.
        Assert.Equal(RegionListRequest.Code, Client[8].Code);
        Assert.Equal(RegionListRequest.Payload(Realm.Albion, 0), Client[8].Payload[..1]); // the server reads only this byte
        // Compare arrays directly: inside a tuple, byte[] compares by reference.
        Assert.Equal(WorldInitRequest.Code, Client[11].Code);
        Assert.Equal(WorldInitRequest.Payload(Realm.Albion, 0), Client[11].Payload);
        Assert.Equal(PlayerInitRequest.Code, Client[12].Code);
        Assert.Equal(PlayerInitRequest.Payload(), Client[12].Payload);
        Assert.Equal(GameOpenRequest.Code, Client[13].Code);
        Assert.Equal(GameOpenRequest.Payload(), Client[13].Payload);
    }

    [Fact]
    public void PingReplyEchoesTheTimestamp()
    {
        ClientPacket ping = Client[3];
        Assert.Equal(PingRequest.Code, ping.Code);
        uint timestamp = new PayloadReader(ping.Payload[4..]).U32BE();
        // The server skips the first 4 bytes (the recorded client sends 00 00 22 00 there); the rest must match.
        Assert.Equal(ping.Payload[4..], PingRequest.Payload(timestamp)[4..]);

        ServerPacket reply = Server[2];
        Assert.Equal(PingRequest.ReplyCode, reply.Code);
        Assert.Equal(timestamp, PingRequest.ParseReply(reply.Payload));
        // ...followed by 4 zero bytes and the ping's sequence number + 1 (PacketLib168.SendPingReply).
        Assert.Equal(ping.Sequence + 1, new PayloadReader(reply.Payload[8..]).U16BE());
    }

    [Theory]
    [InlineData(1000u, 1180u, 180)]
    [InlineData(0xFFFF_FF00u, 0x0000_0064u, 356)]   // across TickCount's wrap
    public void PingRoundTripIsTheTimeSinceTheEchoedStamp(uint sent, uint now, int ms) =>
        Assert.Equal(ms, PingRequest.RoundTrip(sent, now));

    [Fact]
    public void PositionParsesRecordedServer()
    {
        ServerPacket recorded = Server[11];
        Assert.Equal(PositionAndObjectId.Code, recorded.Code);

        Assert.Equal(new PositionAndObjectId(560871f, 511685f, 2344f, ObjectId: 0x4167, Heading: 1512, RegionId: 1, Flags: 0x80),
            PositionAndObjectId.Parse(recorded.Payload));
    }

    [Fact]
    public void ZoneOfRecordedPositionIsWhatTheClientReported()
    {
        PositionAndObjectId position = PositionAndObjectId.Parse(Server[11].Payload);
        Zone zone = Zones.Find(position.RegionId, position.X, position.Y)!;
        Assert.Equal("Camelot Hills", zone.Name);

        ClientPacket firstUpdate = Client.First(p => p.Code == PositionUpdate.Code);
        Assert.Equal(zone.Id, new PayloadReader(firstUpdate.Payload[24..]).U16BE());
    }

    [Fact]
    public void ZoneEdgesAreHalfOpen()
    {
        // Camelot Hills: offsets 67, 59, size 8 × 8, in units of 8192.
        Zone camelotHills = Zones.All.Single(z => z.RegionId == 1 && z.Id == 0);
        Assert.True(camelotHills.Contains(67 * 8192, 59 * 8192));
        Assert.False(camelotHills.Contains(75 * 8192, 60 * 8192));
        Assert.Equal(1, Zones.Find(1, 70 * 8192, 67 * 8192)!.Id); // Salisbury Plains starts where Camelot Hills ends
        Assert.Null(Zones.Find(1, 0, 0));
        Assert.Equal(481, Zones.All.Count);
    }

    [Fact]
    public void EveryRecordedPositionUpdateRebuildsByteForByte()
    {
        List<ClientPacket> updates = Client.Where(p => p.Code == PositionUpdate.Code).ToList();
        Assert.Equal(92, updates.Count);
        foreach (ClientPacket recorded in updates)
        {
            // Read the fields the way the server does (PlayerPositionUpdateHandler, 1.124+ branch).
            var r = new PayloadReader(recorded.Payload);
            float x = r.F32LE(), y = r.F32LE(), z = r.F32LE(), speed = r.F32LE(), verticalSpeed = r.F32LE();
            ushort session = r.U16BE(), objectId = r.U16BE(), zone = r.U16BE();
            byte state = r.U8();
            r.Skip(3);
            ushort heading = r.U16BE();
            byte action = r.U8();
            r.Skip(2);
            byte health = r.U8();

            byte[] rebuilt = PositionUpdate.Payload(x, y, z, session, objectId, zone, heading, speed, verticalSpeed, state, action, health);
            Assert.Equal(recorded.Payload, rebuilt);
        }
    }

    [Fact]
    public void ALandingUpdateCarriesTheImpactSpeedAndTheLandedBit()
    {
        // Falling 20 m at 9.8 m/s² lands at 19.8 m/s = 791 units/s; OpenDAoC charges fall damage when bit
        // 15 of the falling-damage field (offset 28, big-endian) is set, from the vertical speed (offset 16).
        byte[] landing = PositionUpdate.Payload(1, 2, 3, 7, 0, 26, 1024, verticalSpeed: -791f, landed: true);
        Assert.Equal(-791f, BitConverter.ToSingle(landing, 16));
        Assert.Equal(new byte[] { 0x80, 0x00 }, landing[28..30]);
        // Otherwise the field stays 0, as the recorded client always sends it.
        Assert.Equal(new byte[] { 0x00, 0x00 }, PositionUpdate.Payload(1, 2, 3, 7, 0, 26, 1024, verticalSpeed: -40f)[28..30]);
    }

    [Fact]
    public void CommandMatchesRecordedClient()
    {
        ClientPacket recorded = Client[15];
        Assert.Equal(CommandRequest.Code, recorded.Code);
        Assert.Equal(recorded.Payload, CommandRequest.Payload("&gc info 1"));
        Assert.Throws<ArgumentException>(() => CommandRequest.Payload("/quit"));
    }

    [Fact]
    public void QuitReplySaysWhereTheClientGoes()
    {
        // PacketLib168.SendPlayerQuit: totalOut, level.
        Assert.False(QuitReply.LeavesGame([0x00, 0x01]));
        Assert.True(QuitReply.LeavesGame([0x01, 0x32]));
    }

    [Fact]
    public void SlotsAreCheckedBeforeSending()
    {
        Assert.Equal(13, WorldInitRequest.Payload(Realm.Midgard, 3)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldInitRequest.Payload(Realm.Albion, 10));
        Assert.Throws<ArgumentException>(() => RegionListRequest.Payload(Realm.None, 0));
    }

    // No recording has a server-made move; these bytes are written by hand from the server writers
    // (PacketLib168.SendPlayerJump, PacketLib174.SendRegionChanged), all big-endian.
    [Fact]
    public void ParsesCharacterJumpAsTheServerWritesIt()
    {
        byte[] payload =
        [
            0x00, 0x08, 0x8E, 0xD0,   // X 560848
            0x00, 0x07, 0xCE, 0xC5,   // Y 511685
            0x41, 0x67,               // object id
            0x09, 0x28,               // Z 2344
            0x05, 0xE8,               // heading 1512
            0x00, 0x00,               // not in a house
        ];
        var jump = CharacterJump.Parse(payload);
        Assert.Equal(new CharacterJump(560848, 511685, 0x4167, 2344, 1512, 0), jump);
        Assert.False(jump.HeadingOnly);
        Assert.Equal(payload, jump.ToBytes());
        Assert.True(new CharacterJump(0, 0, 0x4167, 0, 99, 0).HeadingOnly);
    }

    [Fact]
    public void ParsesRegionChangedAsTheServerWritesIt()
    {
        byte[] payload = [0x00, 0xC8, 0x00, 0xD0, 0x00, 0x00, 0x00, 0x01, 0x0C, 0x00, 0xFF, 0xBF];
        var changed = RegionChanged.Parse(payload);
        Assert.Equal(new RegionChanged(RegionId: 200, ZoneId: 208), changed);
        Assert.Equal(payload, changed.ToBytes());
    }
}
