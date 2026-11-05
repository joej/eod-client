// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>The connection's own failure paths, against <see cref="FakeServer"/>.</summary>
public class DaocConnectionTests
{
    private static readonly ServerPacket CryptKey = CaptureFile.Login1127e().ServerPackets()[0];

    [Fact]
    public async Task AnUnexpectedReceiveErrorReachesTheReader()
    {
        // A log sink that throws on a received packet ends the receive loop; the reader must get that
        // error straight away rather than wait for a reply that can no longer arrive.
        await using var server = new FakeServer(_ => [CryptKey]);
        var log = new PacketLog(line =>
        {
            if (line.StartsWith("S→C", StringComparison.Ordinal))
                throw new InvalidOperationException("sink failed");
        });
        await using var connection = new DaocConnection(log);
        await connection.ConnectAsync("127.0.0.1", server.Port);
        await connection.SendAsync(CryptKeyRequest.Code, new byte[7]);

        Task<ServerPacket> receive = connection.ReceiveAsync().AsTask();
        Assert.Same(receive, await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(5))));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => receive);
        Assert.Equal("sink failed", e.Message);
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        await using var server = new FakeServer(_ => []);
        var connection = new DaocConnection();
        await connection.ConnectAsync("127.0.0.1", server.Port);
        await connection.DisposeAsync();
        await connection.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(async () => await connection.ReceiveAsync());
    }
}
