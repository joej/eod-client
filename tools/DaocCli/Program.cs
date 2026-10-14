// SPDX-License-Identifier: GPL-3.0-or-later
//
// Headless login + character list against a DAoC server (client protocol 1.127).
//
//   DAOC_PASSWORD=... dotnet run --project tools/DaocCli -- <host> <account>
//       [--port 10300] [--realm albion|midgard|hibernia] [--create <Name> | --delete <slot>] [--hex]
//
// --realm is used when the account has no realm yet (or may use all realms); default albion.
// --create makes a level 1 Briton Armsman (Albion only) in the first free slot; --delete removes
// the character in a slot (0-9) of the account's realm. Both list the characters again afterwards.
// The password is read from DAOC_PASSWORD so it stays out of command lines and shell history.
// Exit codes: 0 login granted, 2 login denied, 1 any other failure.

using System.Net.Sockets;
using DaocProtocol.Net;
using DaocProtocol.Packets;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: DAOC_PASSWORD=... DaocCli <host> <account> [--port 10300] [--realm albion|midgard|hibernia] [--create <Name> | --delete <slot>] [--hex]");
    return 1;
}

string host = args[0];
string account = args[1];
int port = args.SkipWhile(a => a != "--port").Skip(1).Select(int.Parse).DefaultIfEmpty(10300).First();
bool hex = args.Contains("--hex");
Realm realmChoice = args.SkipWhile(a => a != "--realm").Skip(1)
    .Select(r => Enum.Parse<Realm>(r, ignoreCase: true)).DefaultIfEmpty(Realm.Albion).First();
string? createName = args.SkipWhile(a => a != "--create").Skip(1).FirstOrDefault();
int? deleteSlot = args.SkipWhile(a => a != "--delete").Skip(1).Select(int.Parse).Cast<int?>().FirstOrDefault();
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
        await ListCharacters();
        return 0;
    }

    Console.WriteLine($"login denied: {LoginDenied.Parse(result.Payload).Error}");
    return 2;
}
catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine($"failed: {e.Message}");
    return 1;
}

async Task ListCharacters()
{
    await Send(CharacterSelectRequest.Code, CharacterSelectRequest.Payload());
    sequencer.SessionId = SessionId.Parse((await ReceiveOneOf(SessionId.Code)).Payload);
    Console.WriteLine($"session id {sequencer.SessionId}");

    await Send(CharacterOverviewRequest.Code, CharacterOverviewRequest.Payload(Realm.None));
    Realm realm = RealmReply.Parse((await ReceiveOneOf(RealmReply.Code)).Payload);
    Console.WriteLine($"account realm: {realm}");
    if (realm == Realm.None)
        realm = realmChoice;

    CharacterOverview overview = await Overview(realm);
    if (createName is null && deleteSlot is null)
        return;

    if (createName is not null)
    {
        if (realm != Realm.Albion)
            throw new ArgumentException("--create only knows an Albion character (Briton Armsman).");
        int slot = Enumerable.Range(0, CharacterOverview.SlotsPerRealm)
            .Except(overview.Characters.Select(c => c.Slot))
            .DefaultIfEmpty(-1).First();
        if (slot < 0)
            throw new ArgumentException("No free slot in Albion.");
        // Briton bases are 60 each; +10 STR, DEX and CON spend exactly the 30 bonus points.
        var character = new NewCharacter(createName, Realm.Albion, slot, ClassId: 2, Race: 1, Female: false, Model: 32,
            new CharacterStats(70, 70, 70, 60, 60, 60, 60, 60));
        await Send(CharacterCreateRequest.Code, CharacterCreateRequest.CreatePayload(character));
    }
    else
    {
        // The server stays silent for an empty slot, so don't ask.
        if (!overview.Characters.Any(c => c.Slot == deleteSlot))
            throw new ArgumentException($"Slot {deleteSlot} in {realm} is empty.");
        await Send(CharacterCreateRequest.Code, CharacterCreateRequest.DeletePayload(realm, deleteSlot!.Value));
    }

    // The server answers any accepted change with 0x2A; a request it rejects gets the same answer
    // (or nothing), so the new overview is the only real confirmation.
    await ReceiveOneOf(LoginGranted.Code);
    await Overview(realm);
}

async Task<CharacterOverview> Overview(Realm realm)
{
    await Send(CharacterOverviewRequest.Code, CharacterOverviewRequest.Payload(realm));
    CharacterOverview overview = CharacterOverview.Parse((await ReceiveOneOf(CharacterOverview.Code)).Payload);
    Console.WriteLine($"{realm}: {overview.Characters.Count} character(s), flags 0x{overview.Flags:X}");
    foreach (CharacterSummary c in overview.Characters)
        Console.WriteLine($"  slot {c.Slot}: {c.Name}, level {c.Level} {c.RaceName} {c.ClassName}, {c.Location}");
    return overview;
}
