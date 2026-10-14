// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using DaocProtocol.Net;

namespace DaocProtocol.Tests;

/// <summary>
/// A one-client game server on localhost for session tests. Every client packet must frame and
/// checksum correctly; <c>respond</c> returns the packets to answer with (sent in one write, or
/// one byte per write when <c>dribble</c> is set), an empty array to stay silent, or null to
/// close the connection.
/// </summary>
internal sealed class FakeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _run;

    public FakeServer(Func<ClientPacket, ServerPacket[]?> respond, bool dribble = false)
    {
        _listener.Start();
        _run = Task.Run(() => Serve(respond, dribble));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public List<ClientPacket> Received { get; } = [];

    /// <summary>Whatever went wrong on the server side (bad frame, bad checksum), or null.</summary>
    public Exception? Error { get; private set; }

    private async Task Serve(Func<ClientPacket, ServerPacket[]?> respond, bool dribble)
    {
        try
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            var buffer = new byte[64 * 1024];
            int count = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count));
                if (read == 0)
                    return;
                count += read;

                int offset = 0;
                while (true)
                {
                    FrameResult result = ClientPacket.TryRead(buffer.AsSpan(offset, count - offset), out ClientPacket? packet, out int consumed);
                    if (result == FrameResult.Incomplete)
                        break;
                    if (result == FrameResult.BadChecksum)
                        throw new InvalidDataException("Client packet with a bad checksum.");

                    Received.Add(packet!);
                    offset += consumed;
                    ServerPacket[]? replies = respond(packet!);
                    if (replies is null)
                        return; // closes the connection
                    byte[] bytes = replies.SelectMany(r => r.ToBytes()).ToArray();
                    if (dribble)
                        foreach (byte b in bytes)
                            await stream.WriteAsync(new[] { b });
                    else
                        await stream.WriteAsync(bytes);
                }

                buffer.AsSpan(offset, count - offset).CopyTo(buffer);
                count -= offset;
            }
        }
        catch (Exception e) when (e is not ObjectDisposedException)
        {
            Error = e;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        await _run;
    }
}
