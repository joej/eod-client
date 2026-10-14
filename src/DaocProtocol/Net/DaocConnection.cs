// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Sockets;
using System.Threading.Channels;

namespace DaocProtocol.Net;

/// <summary>
/// One TCP connection to a game server. A background loop splits the incoming stream into
/// <see cref="ServerPacket"/>s and queues them. Read them with <see cref="ReceiveAsync"/>, or
/// poll with <see cref="TryReceive"/> (e.g. once per frame from a game loop). Sends are
/// serialised, so a timer may send while another task waits for a reply.
/// </summary>
public sealed class DaocConnection : IAsyncDisposable
{
    // A server packet is at most 3 + 65535 bytes; room for two keeps the loop simple.
    private const int BufferSize = 2 * (ServerPacket.HeaderSize + ushort.MaxValue);

    private readonly TcpClient _tcp = new();
    private readonly Channel<ServerPacket> _inbound =
        Channel.CreateUnbounded<ServerPacket>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly ClientPacketSequencer _sequencer = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly PacketLog? _log;
    private NetworkStream? _stream;
    private Task _receiveLoop = Task.CompletedTask;

    public DaocConnection(PacketLog? log = null) => _log = log;

    /// <summary>Goes into every client packet header; 0 until the server assigns one (0x28).</summary>
    public ushort SessionId
    {
        get => _sequencer.SessionId;
        set => _sequencer.SessionId = value;
    }

    /// <summary>Completes when the connection closes; faults with the reason if it failed.</summary>
    public Task Completion => _inbound.Reader.Completion;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await _tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        _receiveLoop = Task.Run(() => ReceiveLoop(_stream, _stop.Token));
    }

    public async Task SendAsync(byte code, byte[] payload, CancellationToken cancellationToken = default)
    {
        NetworkStream stream = _stream ?? throw new InvalidOperationException("Not connected.");
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClientPacket packet = _sequencer.Next(code, payload);
            _log?.Sent(packet);
            await stream.WriteAsync(packet.ToBytes(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public bool TryReceive(out ServerPacket packet) => _inbound.Reader.TryRead(out packet!);

    /// <summary>
    /// The next packet. Throws <see cref="IOException"/> (or the receive loop's error) once the
    /// connection has closed and the queue is empty.
    /// </summary>
    public async ValueTask<ServerPacket> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException e)
        {
            throw e.InnerException ?? new IOException("The connection is closed.");
        }
    }

    private async Task ReceiveLoop(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        int count = 0;
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("The server closed the connection.");
                count += read;

                int offset = 0;
                while (ServerPacket.TryRead(buffer.AsSpan(offset, count - offset), out ServerPacket? packet, out int consumed) == FrameResult.Complete)
                {
                    _log?.Received(packet!);
                    _inbound.Writer.TryWrite(packet!);
                    offset += consumed;
                }

                buffer.AsSpan(offset, count - offset).CopyTo(buffer);
                count -= offset;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _inbound.Writer.TryComplete();
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            _inbound.Writer.TryComplete(e is IOException ? e : new IOException(e.Message, e));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _tcp.Dispose();
        await _receiveLoop.ConfigureAwait(false);
        _inbound.Writer.TryComplete();
        _stop.Dispose();
        _sendLock.Dispose();
    }
}
