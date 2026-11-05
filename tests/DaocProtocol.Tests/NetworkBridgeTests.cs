// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>Packet dispatch to typed events, over the recorded 1.127e session's server packets.</summary>
public class NetworkBridgeTests
{
    private static readonly List<ServerPacket> Recorded = CaptureFile.Login1127e().ServerPackets();

    [Fact]
    public void RoutesTheRecordedSessionToItsEvents()
    {
        var logged = new List<string>();
        var bridge = new NetworkBridge(logged.Add);
        int moved = 0, npcs = 0, pings = 0;
        bridge.ObjectMoved += _ => moved++;
        bridge.NpcSpawned += _ => npcs++;
        bridge.PingReplied += () => pings++;

        foreach (ServerPacket p in Recorded)
            bridge.Dispatch(p);

        Assert.Equal(Recorded.Count, bridge.PacketCount);
        Assert.Equal(Recorded.Count(p => p.Code == ObjectUpdate.Code), moved);
        Assert.Equal(Recorded.Count(p => p.Code == NpcCreate.Code), npcs);
        Assert.Equal(Recorded.Count(p => p.Code == PingRequest.ReplyCode), pings);
        Assert.True(moved > 0 && npcs > 0 && pings > 0);
        Assert.Empty(logged);   // every recorded packet we handle parses
        // Opcodes without a handler are counted, not lost: e.g. the login-time 0x2A.
        Assert.Equal(Recorded.Count(p => p.Code == LoginGranted.Code), bridge.UnhandledCounts[LoginGranted.Code]);
    }

    [Fact]
    public void AMalformedPacketIsLoggedAndTheNextOneStillArrives()
    {
        var logged = new List<string>();
        var bridge = new NetworkBridge(logged.Add);
        int moved = 0;
        bridge.ObjectMoved += _ => moved++;

        bridge.Dispatch(new ServerPacket(ObjectUpdate.Code, [1, 2]));
        bridge.Dispatch(Recorded.First(p => p.Code == ObjectUpdate.Code));

        Assert.Contains("0xA1", Assert.Single(logged));
        Assert.Equal(1, moved);
        Assert.Equal(2, bridge.PacketCount);
    }

    [Fact]
    public void AQuitReplyIsHandedOver()
    {
        var bridge = new NetworkBridge();
        ServerPacket? arrived = null;
        bridge.QuitArrived += p => arrived = p;
        var quit = new ServerPacket(QuitReply.Code, [0x00, 0x01]);
        bridge.Dispatch(quit);
        Assert.Same(quit, arrived);
    }
}
