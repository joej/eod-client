// SPDX-License-Identifier: GPL-3.0-or-later
//
// Headless client against a DAoC server (client protocol 1.127): login, character list, and
// optionally create, delete, or enter the world and stay connected.
//
//   DAOC_PASSWORD=... dotnet run --project tools/DaocCli -- <host> <account>
//       [--port 10300] [--realm albion|midgard|hibernia]
//       [--create <Name> | --delete <slot> | --enter <slot> [--stay <seconds>]] [--log | --hex]
//
// --realm is used when the account has no realm yet (or may use all realms); default albion.
// --create makes a level 1 Briton Armsman (Albion only) in the first free slot; --delete removes
// the character in a slot (0-9) of the account's realm. Both list the characters again afterwards.
// --enter puts the character in a slot into the world and keeps the session alive for --stay
// seconds (default 10), then reports what arrived. --log prints every packet's code and length,
// --hex the whole packet (never the login request).
// The password is read from DAOC_PASSWORD so it stays out of command lines and shell history.
// Exit codes: 0 login granted, 2 login denied, 1 any other failure.

using System.Net.Sockets;
using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: DAOC_PASSWORD=... DaocCli <host> <account> [--port 10300] [--realm albion|midgard|hibernia] [--create <Name> | --delete <slot> | --enter <slot> [--stay <seconds>]] [--log | --hex]");
    return 1;
}

string host = args[0];
string account = args[1];
int port = args.SkipWhile(a => a != "--port").Skip(1).Select(int.Parse).DefaultIfEmpty(10300).First();
bool hex = args.Contains("--hex");
bool log = hex || args.Contains("--log");
Realm realmChoice = args.SkipWhile(a => a != "--realm").Skip(1)
    .Select(r => Enum.Parse<Realm>(r, ignoreCase: true)).DefaultIfEmpty(Realm.Albion).First();
string? createName = args.SkipWhile(a => a != "--create").Skip(1).FirstOrDefault();
int? deleteSlot = args.SkipWhile(a => a != "--delete").Skip(1).Select(int.Parse).Cast<int?>().FirstOrDefault();
int? enterSlot = args.SkipWhile(a => a != "--enter").Skip(1).Select(int.Parse).Cast<int?>().FirstOrDefault();
int staySeconds = args.SkipWhile(a => a != "--stay").Skip(1).Select(int.Parse).DefaultIfEmpty(10).First();
string? password = Environment.GetEnvironmentVariable("DAOC_PASSWORD");
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("DAOC_PASSWORD is not set");
    return 1;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using var connection = new DaocConnection(log ? new PacketLog(Console.WriteLine, hex) : null);
var session = new ClientSession(connection);

try
{
    await connection.ConnectAsync(host, port, timeout.Token);
    Console.WriteLine($"connected to {host}:{port}");

    LoginResult login = await session.LoginAsync(account, password, timeout.Token);
    if (!login.Success)
    {
        Console.WriteLine($"login denied: {login.Error}");
        return 2;
    }
    Console.WriteLine($"login granted: account {login.Granted!.AccountName} on {login.Granted.ServerNameShort} (server type {login.Granted.ServerType})");

    Realm realm = await session.OpenCharacterScreenAsync(timeout.Token);
    Console.WriteLine($"session id {connection.SessionId}, account realm: {realm}");
    if (realm == Realm.None)
        realm = realmChoice;

    CharacterOverview overview = await session.GetCharactersAsync(realm, timeout.Token);
    Print(realm, overview);

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
        Print(realm, await session.CreateCharacterAsync(character, timeout.Token));
    }
    else if (deleteSlot is int slot)
    {
        Print(realm, await session.DeleteCharacterAsync(realm, slot, timeout.Token));
    }
    else if (enterSlot is int enter)
    {
        WorldPosition world = await session.EnterWorldAsync(realm, enter, timeout.Token);
        PositionAndObjectId p = world.Position;
        Console.WriteLine($"in world: object id {p.ObjectId}, region {p.RegionId}, zone {world.Zone.Id} ({world.Zone.Name}), " +
                          $"x {p.X} y {p.Y} z {p.Z}, heading {p.Heading}");
        await StayInWorld(TimeSpan.FromSeconds(staySeconds));
    }

    return 0;
}
catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or SocketException
                              or InvalidDataException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine($"failed: {e.Message}");
    return 1;
}

// Keeps the session alive and drains what the server sends; fails if the connection drops.
async Task StayInWorld(TimeSpan stay)
{
    using var stop = new CancellationTokenSource(stay);
    Task keepAlive = session.KeepAliveAsync(stop.Token);
    var counts = new Dictionary<byte, int>();
    var started = DateTime.UtcNow;
    var nextReport = started.AddSeconds(30);
    try
    {
        while (true)
        {
            ServerPacket packet = await connection.ReceiveAsync(stop.Token);
            counts[packet.Code] = counts.GetValueOrDefault(packet.Code) + 1;
            if (DateTime.UtcNow >= nextReport)
            {
                Console.WriteLine($"  {(DateTime.UtcNow - started).TotalSeconds:0} s: still connected, " +
                                  $"{counts.Values.Sum()} packets, {counts.GetValueOrDefault(PingRequest.ReplyCode)} ping replies");
                nextReport = nextReport.AddSeconds(30);
            }
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
    }

    await keepAlive;
    Console.WriteLine($"stayed {stay.TotalSeconds:0} s: {counts.Values.Sum()} packets received, " +
                      $"{counts.GetValueOrDefault(PingRequest.ReplyCode)} ping replies; by code: " +
                      string.Join(" ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key:X2}×{c.Value}")));
}

static void Print(Realm realm, CharacterOverview overview)
{
    Console.WriteLine($"{realm}: {overview.Characters.Count} character(s), flags 0x{overview.Flags:X}");
    foreach (CharacterSummary c in overview.Characters)
        Console.WriteLine($"  slot {c.Slot}: {c.Name}, level {c.Level} {c.RaceName} {c.ClassName}, {c.Location}");
}
