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
    public void SlotsAreCheckedBeforeSending()
    {
        Assert.Equal(13, WorldInitRequest.Payload(Realm.Midgard, 3)[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldInitRequest.Payload(Realm.Albion, 10));
        Assert.Throws<ArgumentException>(() => RegionListRequest.Payload(Realm.None, 0));
    }
}
