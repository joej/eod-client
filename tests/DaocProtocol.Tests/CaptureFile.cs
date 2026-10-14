// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using DaocProtocol.Net;

namespace DaocProtocol.Tests;

/// <summary>
/// Reads the RustDAoC capture format: records of <c>[u8 direction][u64 BE ms][u32 BE length][bytes]</c>.
/// Records are concatenated per direction to rebuild each TCP stream.
/// </summary>
internal sealed class CaptureFile
{
    public const byte ClientToServer = 1;
    public const byte ServerToClient = 2;

    public byte[] ClientStream { get; }
    public byte[] ServerStream { get; }

    private CaptureFile(byte[] clientStream, byte[] serverStream)
    {
        ClientStream = clientStream;
        ServerStream = serverStream;
    }

    public static CaptureFile Load(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        var client = new MemoryStream();
        var server = new MemoryStream();
        int pos = 0;
        while (pos < data.Length)
        {
            byte direction = data[pos];
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 9)));
            var bytes = data.AsSpan(pos + 13, length);
            (direction switch
            {
                ClientToServer => client,
                ServerToClient => server,
                _ => throw new InvalidDataException($"unknown direction {direction} at offset {pos}"),
            }).Write(bytes);
            pos += 13 + length;
        }

        return new CaptureFile(client.ToArray(), server.ToArray());
    }

    public static CaptureFile Login1127e() => Load("rustdaoc_login_20260714.bin");

    public List<ClientPacket> ClientPackets()
    {
        var packets = new List<ClientPacket>();
        for (int pos = 0; pos < ClientStream.Length;)
        {
            if (ClientPacket.TryRead(ClientStream.AsSpan(pos), out ClientPacket? p, out int n) != FrameResult.Complete)
                throw new InvalidDataException($"client stream doesn't frame at offset {pos}");
            packets.Add(p!);
            pos += n;
        }

        return packets;
    }

    public List<ServerPacket> ServerPackets()
    {
        var packets = new List<ServerPacket>();
        for (int pos = 0; pos < ServerStream.Length;)
        {
            if (ServerPacket.TryRead(ServerStream.AsSpan(pos), out ServerPacket? p, out int n) != FrameResult.Complete)
                throw new InvalidDataException($"server stream doesn't frame at offset {pos}");
            packets.Add(p!);
            pos += n;
        }

        return packets;
    }
}
