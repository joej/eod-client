// SPDX-License-Identifier: GPL-3.0-or-later
//
// Headless login check against a DAoC server (client protocol 1.127).
//
//   DAOC_PASSWORD=... dotnet run --project tools/DaocCli -- <host> <account> [--port 10300] [--hex]
//
// The password is read from DAOC_PASSWORD so it stays out of command lines and shell history.
// Exit codes: 0 login granted, 2 login denied, 1 any other failure.

using System.Net.Sockets;
using DaocProtocol.Net;
using DaocProtocol.Packets;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: DAOC_PASSWORD=... DaocCli <host> <account> [--port 10300] [--hex]");
    return 1;
}

string host = args[0];
string account = args[1];
int port = args.SkipWhile(a => a != "--port").Skip(1).Select(int.Parse).DefaultIfEmpty(10300).First();
bool hex = args.Contains("--hex");
string? password = Environment.GetEnvironmentVariable("DAOC_PASSWORD");
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("DAOC_PASSWORD is not set");
    return 1;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var tcp = new TcpClient();
await tcp.ConnectAsync(host, port, timeout.Token);
NetworkStream stream = tcp.GetStream();
Console.WriteLine($"connected to {host}:{port}");

var sequencer = new ClientPacketSequencer();
var inbound = new List<byte>();
var readBuffer = new byte[64 * 1024];

async Task Send(byte code, byte[] payload)
{
    byte[] bytes = sequencer.Next(code, payload).ToBytes();
    // Never log the login packet's bytes: the password is in plain text.
    if (hex && code != LoginRequest.Code)
        Console.WriteLine($"C→S [{code:X2}] {Convert.ToHexString(bytes)}");
    else
        Console.WriteLine($"C→S [{code:X2}] {bytes.Length} bytes");
    await stream.WriteAsync(bytes, timeout.Token);
}

async Task<ServerPacket> Receive()
{
    while (true)
    {
        if (ServerPacket.TryRead(inbound.ToArray(), out ServerPacket? packet, out int consumed) == FrameResult.Complete)
        {
            inbound.RemoveRange(0, consumed);
            Console.WriteLine(hex
                ? $"S→C [{packet!.Code:X2}] {Convert.ToHexString(packet.ToBytes())}"
                : $"S→C [{packet!.Code:X2}] {packet.Payload.Length} bytes");
            return packet;
        }

        int read = await stream.ReadAsync(readBuffer, timeout.Token);
        if (read == 0)
            throw new IOException("server closed the connection");
        inbound.AddRange(readBuffer.AsSpan(0, read).ToArray());
    }
}

async Task<ServerPacket> ReceiveOneOf(params byte[] codes)
{
    while (true)
    {
        ServerPacket packet = await Receive();
        if (codes.Contains(packet.Code))
            return packet;
    }
}

try
{
    await Send(CryptKeyRequest.Code, CryptKeyRequest.Payload(ClientVersion.V1127e));
    CryptKeyReply reply = CryptKeyReply.Parse((await ReceiveOneOf(CryptKeyReply.Code)).Payload);
    Console.WriteLine($"server speaks {reply.Version}, encryption {(reply.KeyLength == 0 ? "off" : "on")}");

    await Send(LoginRequest.Code, LoginRequest.Payload(account, password));
    ServerPacket result = await ReceiveOneOf(LoginGranted.Code, LoginDenied.Code);
    if (result.Code == LoginGranted.Code)
    {
        LoginGranted granted = LoginGranted.Parse(result.Payload);
        Console.WriteLine($"login granted: account {granted.AccountName} on {granted.ServerNameShort} (server type {granted.ServerType})");
        return 0;
    }

    Console.WriteLine($"login denied: {LoginDenied.Parse(result.Payload).Error}");
    return 2;
}
catch (Exception e) when (e is IOException or OperationCanceledException or SocketException)
{
    Console.Error.WriteLine($"failed: {e.Message}");
    return 1;
}
