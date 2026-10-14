// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Packets;

namespace DaocProtocol.Net;

/// <summary>
/// Logs every packet a <see cref="DaocConnection"/> sends or receives. Has no engine
/// dependency: the Godot client sets <see cref="Sink"/> to GD.Print, the CLI to Console.WriteLine.
/// </summary>
public sealed class PacketLog(Action<string> sink, bool hex = false)
{
    public Action<string> Sink { get; } = sink;

    /// <summary>Whole packets in hex; otherwise code and length only.</summary>
    public bool Hex { get; set; } = hex;

    public void Sent(ClientPacket packet) =>
        // Never hex-log the login request: the password is in plain text.
        Write("C→S", packet.Code, packet.ToBytes(), Hex && packet.Code != LoginRequest.Code);

    public void Received(ServerPacket packet) => Write("S→C", packet.Code, packet.ToBytes(), Hex);

    private void Write(string direction, byte code, byte[] bytes, bool hex) =>
        Sink(hex
            ? $"{direction} [{code:X2}] {Convert.ToHexString(bytes)}"
            : $"{direction} [{code:X2}] {bytes.Length} bytes");
}
