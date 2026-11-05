// SPDX-License-Identifier: GPL-3.0-or-later
//
// Headless client against a DAoC server (client protocol 1.127): login, character list, and
// optionally create, delete, or enter the world and stay connected.
//
//   DAOC_PASSWORD=... dotnet run --project tools/DaocCli -- <host> <account>
//       [--port 10300] [--realm albion|midgard|hibernia]
//       [--create <Name> | --delete <slot> | --enter <slot> [--goto <zone>] [--stay <seconds>]]
//       [--idle <seconds> [--no-keepalive]] [--log | --hex] [--quit-checks]
//
// --realm is used when the account has no realm yet (or may use all realms); default albion.
// --create makes a level 1 Briton Armsman (Albion only) in the first free slot; --delete removes
// the character in a slot (0-9) of the account's realm. Both list the characters again afterwards.
// --enter puts the character in a slot into the world and keeps the session alive for --stay
// seconds (default 10), reports what arrived, then logs out with /quit (about 20 s). --goto then
// moves the character to the middle of a zone (src/DaocProtocol/World/zones.tsv) with the GM
// command /jump, so the account must be a GM. --log prints every packet's code and length,
// --hex the whole packet (never the login request). --idle waits at the character screen first;
// with --no-keepalive it sends no pings there (OpenDAoC disconnects after 150 s without one).
// --quit-checks (with --enter, on a player account: GMs have no quit timer) runs the Godot client's
// quit path four ways: quit and wait; quit and move (the quit is called off); quit again;
// quit while running. It prints the server's chat lines and sitting state as they arrive.
// The password is read from DAOC_PASSWORD so it stays out of command lines and shell history.
// Exit codes: 0 login granted, 2 login denied, 1 any other failure.

using System.Net.Sockets;
using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

if (args.Length < 2)
{
    Console.Error.WriteLine(Usage);
    return 1;
}

string host = args[0];
string account = args[1];
// A bad or missing option value prints the usage line and exits 1, as a missing argument does.
string? Value(string name)
{
    int i = Array.IndexOf(args, name);
    if (i < 0)
        return null;
    if (i + 1 < args.Length)
        return args[i + 1];
    Console.Error.WriteLine($"{name} needs a value.\n{Usage}");
    Environment.Exit(1);
    return null;
}
int? Int(string name)
{
    if (Value(name) is not string v)
        return null;
    if (int.TryParse(v, out int n))
        return n;
    Console.Error.WriteLine($"{name} needs a number, not '{v}'.\n{Usage}");
    Environment.Exit(1);
    return null;
}

int port = Int("--port") ?? 10300;
bool hex = args.Contains("--hex");
bool quitChecks = args.Contains("--quit-checks");
bool log = hex || args.Contains("--log");
var clock = System.Diagnostics.Stopwatch.StartNew();
Realm realmChoice = Realm.Albion;
if (Value("--realm") is string realmName && !Enum.TryParse(realmName, ignoreCase: true, out realmChoice))
{
    Console.Error.WriteLine($"--realm must be albion, midgard or hibernia, not '{realmName}'.\n{Usage}");
    return 1;
}
string? createName = Value("--create");
int? deleteSlot = Int("--delete");
int? enterSlot = Int("--enter");
int? gotoZone = Int("--goto");
int staySeconds = Int("--stay") ?? 10;
int idleSeconds = Int("--idle") ?? 0;
bool keepAliveOn = !args.Contains("--no-keepalive");
string? password = Environment.GetEnvironmentVariable("DAOC_PASSWORD");
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("DAOC_PASSWORD is not set");
    return 1;
}

// Each session step times out on its own (ClientSession.ReplyTimeout); only connecting needs a deadline here.
using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
await using var connection = new DaocConnection(
    quitChecks ? new PacketLog(QuitWatch, hex: true) : log ? new PacketLog(Console.WriteLine, hex) : null);
var session = new ClientSession(connection);
using var stopKeepAlive = new CancellationTokenSource();
Task keepAlive = Task.CompletedTask;

try
{
    await connection.ConnectAsync(host, port, connectTimeout.Token);
    Console.WriteLine($"connected to {host}:{port}");

    LoginResult login = await session.LoginAsync(account, password);
    if (!login.Success)
    {
        Console.WriteLine($"login denied: {login.Error}");
        return 2;
    }
    Console.WriteLine($"login granted: account {login.Granted!.AccountName} on {login.Granted.ServerNameShort} (server type {login.Granted.ServerType})");

    Realm realm = await session.OpenCharacterScreenAsync();
    Console.WriteLine($"session id {connection.SessionId}, account realm: {realm}");
    if (keepAliveOn)
        keepAlive = session.KeepAliveAsync(stopKeepAlive.Token);
    if (idleSeconds > 0)
        await Idle(TimeSpan.FromSeconds(idleSeconds));
    if (realm == Realm.None)
        realm = realmChoice;

    CharacterOverview overview = await session.GetCharactersAsync(realm);
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
        Print(realm, await session.CreateCharacterAsync(character));
    }
    else if (deleteSlot is int slot)
    {
        Print(realm, await session.DeleteCharacterAsync(realm, slot));
    }
    else if (enterSlot is int enter)
    {
        WorldPosition world = await session.EnterWorldAsync(realm, enter);
        PositionAndObjectId p = world.Position;
        Console.WriteLine($"in world: object id {p.ObjectId}, region {p.RegionId}, zone {world.Zone.Id} ({world.Zone.Name}), " +
                          $"x {p.X} y {p.Y} z {p.Z}, heading {p.Heading}");
        if (gotoZone is int zoneId)
        {
            DaocProtocol.World.Zone target = DaocProtocol.World.Zones.All.FirstOrDefault(z => z.Id == zoneId)
                ?? throw new ArgumentException($"No zone {zoneId} in zones.tsv.");
            (float cx, float cy) = target.Centre;
            WorldPosition moved = await session.JumpAsync(target.RegionId, cx, cy, p.Z);
            PositionAndObjectId m = moved.Position;
            Console.WriteLine($"jumped: region {m.RegionId}, zone {moved.Zone.Id} ({moved.Zone.Name}), x {m.X} y {m.Y} z {m.Z}");
        }
        if (quitChecks)
            await QuitChecks(realm, enter);
        else
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
finally
{
    stopKeepAlive.Cancel();
    // A keep-alive that failed (connection gone) has nothing more to report.
    await keepAlive.ContinueWith(_ => { }, TaskScheduler.Default);
}

// Waits at the character screen without reading; fails if the server closes the connection.
async Task Idle(TimeSpan idle)
{
    Console.WriteLine($"idling {idle.TotalSeconds:0} s at the character screen, {(keepAliveOn ? "with" : "without")} pings…");
    var started = DateTime.UtcNow;
    Task finished = await Task.WhenAny(connection.Completion, Task.Delay(idle));
    if (finished == connection.Completion)
        throw new IOException($"disconnected after {(DateTime.UtcNow - started).TotalSeconds:0} s at the character screen");
    Console.WriteLine("  still connected");
}

// Drains what the server sends while the keep-alive runs, then quits cleanly; fails if the
// connection drops.
async Task StayInWorld(TimeSpan stay)
{
    using var stop = new CancellationTokenSource(stay);
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

    Console.WriteLine($"stayed {stay.TotalSeconds:0} s: {counts.Values.Sum()} packets received, " +
                      $"{counts.GetValueOrDefault(PingRequest.ReplyCode)} ping replies; by code: " +
                      string.Join(" ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key:X2}×{c.Value}")));

    // The heartbeat keeps running through the server's quit timer.
    var quitStarted = DateTime.UtcNow;
    Console.WriteLine("quitting (/quit)…");
    CharacterOverview? back = await session.QuitAsync();
    Console.WriteLine($"quit after {(DateTime.UtcNow - quitStarted).TotalSeconds:0} s; " +
                      (back is null ? "the server ended the game" : $"back at the character screen ({back.Characters.Count} character(s))"));
}

// --quit-checks: the four quit cases from docs/code-review-2026-11.md, driven the way the Godot
// client's LeaveWorld does it (QuitAsync with a cancel token; on movement, stop waiting: the server
// stands the character up and calls the quit off when its X/Y changes).
async Task QuitChecks(Realm realm, int slot)
{
    Step("1. /quit and stand still");
    await TimedQuit();

    WorldPosition w = await Reenter();
    Step("2. /quit, then move after 5 s");
    using (var interrupt = new CancellationTokenSource())
    {
        Task<CharacterOverview?> quit = session.QuitAsync(interrupt.Token);
        await Task.Delay(TimeSpan.FromSeconds(5));
        await Walk(w, seconds: 1);
        interrupt.Cancel();
        await Swallow(quit);
    }
    await StillInWorld(TimeSpan.FromSeconds(30));

    Step("3. /quit again after the cancelled one");
    await TimedQuit();

    w = await Reenter();
    Step("4. /quit while running");
    using (var interrupt = new CancellationTokenSource())
    {
        Task running = Walk(w, seconds: 3);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Task<CharacterOverview?> quit = session.QuitAsync(interrupt.Token);
        await running;
        interrupt.Cancel();
        await Swallow(quit);
    }
    await StillInWorld(TimeSpan.FromSeconds(30));

    Step("leaving: /quit standing still");
    await TimedQuit();

    async Task TimedQuit()
    {
        long started = clock.ElapsedMilliseconds;
        CharacterOverview? back = await session.QuitAsync();
        Note($"0xA4 after {(clock.ElapsedMilliseconds - started) / 1000.0:0.0} s; " +
             (back is null ? "the server ended the game" : "back at the character screen"));
    }

    async Task<WorldPosition> Reenter()
    {
        WorldPosition entered = await session.EnterWorldAsync(realm, slot);
        Note($"back in the world at x {entered.Position.X} y {entered.Position.Y}");
        await Task.Delay(TimeSpan.FromSeconds(3));   // let the entry burst settle
        return entered;
    }

    // Runs east at run speed (191 units/s), an update every 0.2 s as retail does, then stops.
    async Task Walk(WorldPosition from, double seconds)
    {
        PositionAndObjectId p = from.Position;
        Note($"running {seconds:0} s");
        for (double t = 0.2; t <= seconds + 0.01; t += 0.2)
        {
            await session.MoveToAsync(p.X + (float)(191 * t), p.Y, p.Z, 1024, speed: 191);
            await Task.Delay(200);
        }
        await session.MoveToAsync(p.X + (float)(191 * seconds), p.Y, p.Z, 1024, speed: 0);
        Note("stopped");
    }

    // Reads what arrives for a while (the log prints chat); a 0xA4 here means the quit went ahead.
    async Task StillInWorld(TimeSpan wait)
    {
        using var stop = new CancellationTokenSource(wait);
        try
        {
            while (true)
                if ((await connection.ReceiveAsync(stop.Token)).Code == QuitReply.Code)
                    throw new IOException("0xA4 arrived: the quit was not called off");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        Note($"no 0xA4 in {wait.TotalSeconds:0} s: still in the world");
    }

    static async Task Swallow(Task t)
    {
        try { await t; }
        catch (OperationCanceledException) { }
    }
}

void Step(string what) => Console.WriteLine($"\n[{clock.Elapsed.TotalSeconds,6:0.0}] === {what}");
void Note(string what) => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,6:0.0}]     {what}");

// Packet-log sink for --quit-checks: chat in and out (0xAF), stand/sit requests (0xC7) and the
// sitting byte of 0xAD (byte 2 of the payload), nothing else.
void QuitWatch(string line)
{
    // "S→C [AF] <hex>"
    if (line.Length < 9)
        return;
    string dir = line[..3], code = line[5..7];
    if (code is not ("AF" or "AD" or "A4" or "C7"))
        return;   // includes 0xA7, which the log never writes as hex
    byte[] bytes = Convert.FromHexString(line[9..]);
    if (dir == "S→C" && code == "AF")
    {
        ChatMessage m = ChatMessage.Parse(bytes.AsSpan(3));
        Note($"S→C chat {m.Type} (0x{(byte)m.Type:X2}): {m.Text}");
    }
    else if (dir == "S→C" && code == "AD" && bytes.Length > 5)
        Note($"S→C 0xAD status: sitting {bytes[5]}");
    else if (dir == "S→C" && code == "A4")
        Note("S→C 0xA4 quit");
    else if (dir == "C→S" && code is "AF" or "C7")
        Note($"C→S 0x{code} {Convert.ToHexString(bytes.AsSpan(10, bytes.Length - 12))}");
}

static void Print(Realm realm, CharacterOverview overview)
{
    Console.WriteLine($"{realm}: {overview.Characters.Count} character(s), flags 0x{overview.Flags:X}");
    foreach (CharacterSummary c in overview.Characters)
        Console.WriteLine($"  slot {c.Slot}: {c.Name}, level {c.Level} {c.RaceName} {c.ClassName}, {c.Location}");
}

partial class Program
{
    private const string Usage = "usage: DAOC_PASSWORD=... DaocCli <host> <account> [--port 10300] [--realm albion|midgard|hibernia] [--create <Name> | --delete <slot> | --enter <slot> [--goto <zone>] [--stay <seconds>]] [--idle <seconds> [--no-keepalive]] [--log | --hex] [--quit-checks]";
}
