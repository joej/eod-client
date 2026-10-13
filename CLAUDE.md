# DAOC Godot Client — Project Context

## Project Goal

Build a Minimum Viable Client (MVC) in Godot 4 (.NET/C#) that connects to an
OpenDAOC server, authenticates, selects a character, receives a world position,
and renders a primitive (capsule mesh) at that location. This proves the
protocol end-to-end and establishes a community-contribution-ready foundation
for a modern DAOC client.

This is NOT a full game client. It is a protocol proof-of-concept that the
community can build on.

---

## Licensing (decided 2026-10-13)

- Public repo (protocol library, Godot client, tests, protocol docs): **GPL-3.0-or-later**.
  This matches OpenDAoC (GPL-3.0), DOLSharp (GPL-2.0-or-later), RustDAoC and Uthgard (GPL-3.0),
  so their code and fixtures can be reused with attribution.
- Site posts (eveofdarkness.org): CC BY 4.0.

## Stack Decisions

- **Engine**: Godot 4 (.NET variant — must download the .NET build, not Standard)
- **Language**: C# throughout — directly compatible with OpenDAOC/DOLSharp codebase
- **Protocol library**: plain .NET class library (`DaocProtocol`) with **no Godot
  dependency**. It uses `System.Net.Sockets` and holds framing, checksum, packet
  types and the login state machine. Godot is only a front end on top of it.
  This keeps the renderer replaceable and lets unit tests, a headless test
  client or bots reuse the protocol code.
- **Target server**: OpenDAOC (vanilla, pinned version — see below)
- **Packet capture reference**: PacketLogConverter + Wireshark against local server

---

> **Provenance note (2026-10-13):** this file began as notes from an early planning chat.
> The protocol sections below were **corrected
> against DOLSharp source @ 776619f** (`GameServer/packets/Server/IPacketLib.cs`,
> `gameutils/GSPacketIn.cs`, `packets/Server/PacketProcessor.cs`,
> `packets/Client/168/LoginRequestHandler.cs`). The original listed
> pre-2003 opcodes, a made-up rolling-XOR cipher, and an MD5 login hash; none
> of those match the source. Treat anything not yet checked against code or
> captures as unverified.
> Verify against OpenDAOC-Core too: it is a DOLSharp fork, but its code may differ.

## The MVC Milestone Definition

Wire opcodes as used by 1.x clients (DOLSharp `eClientPackets` / `eServerPackets`).
Sequence below is the one OpenDAoC-ClientSimulator (@6993402, client 1.124) drives
successfully against OpenDAoC; confirm it for 1.127 from a capture.

```
TCP Connect (port 10300)
  C→S 0xF4 CryptKeyRequest          (client type + version, see Version detection)
  S→C 0x22 CryptKey                 (SendVersionAndCryptKey)
  C→S 0xA7 LoginRequest             (username + password, see below)
  S→C 0x2A LoginGranted | 0x2C LoginDenied   → start 0xA3 ping timer (simulator: 15 s)
  C→S 0x10 CharacterSelectRequest   (char name empty: the client sends this BEFORE the overview)
  S→C 0x28 SessionID                → session id goes in every C→S header from now on
  C→S 0xFC CharacterOverviewRequest (realm byte + "<account>-S", 24-byte fixed string)
  S→C 0xFE Realm, 0xFD CharacterOverview (0xFC CharacterOverview1126 on 1.126+)
                                    → display character list in Godot UI
  C→S 0x10 CharacterSelectRequest   (now with the chosen name, 28-byte fixed string)
  S→C 0x28 SessionID                → second one: send world init
  C→S 0xD4 WorldInitRequest
  S→C 0x20 PositionAndObjectID      → capsule placed at coordinates
  C→S 0xBF GameOpenRequest          (S→C 0x2D GameOpenReply)
  S→C 0x4E ControlledHorse          → simulator sends 0xE8 PlayerInitRequest on this
  C→S 0xA9 PositionUpdate           (simulator: every 250 ms; see heartbeat note)
  C→S 0xA3 PingRequest  / S→C 0x29 PingReply
```
The simulator never sends 0x9D RegionListRequest. OpenDAoC-ClientSimulator has no license:
read it as a reference, do not copy code.

**1.127e capture (RustDAoC fixture, verified 2026-10-13).** `latentcollapse/RustDAoC`
`crates/caer-world/tests/fixtures/rustdaoc_login_20260714.bin`: two records
`[dir u8][ms u64 BE][len u32 BE][bytes]`, i.e. one whole TCP stream per direction (dir 1 = C→S,
dir 2 = S→C). It splits cleanly into 171 C→S packets (all checksums valid with our algorithm)
and 1886 S→C packets. Whether a retail client or RustDAoC's own client made it is unverified.
Its C→S order differs from the simulator's:
`F4 F4 A7 A3 10 FC 10 FC 9D A3 10 D4 E8 BF …` (S→C: `22 2A 29 28 FE 28 FC B1 29 28 C5 20 …`),
so 1.127 sends 0x9D and 0xE8 *before* 0xBF. Confirm against our own capture.

The source comments show each modern opcode as `old ^ 0xA8` (e.g. 0xA9 = 0x01 ^ 168).
That is why the original plan's 0x01 and 0x40 were wrong: they are the pre-remap values.

Character renders as a capsule mesh at correct world coordinates. No assets
required. No animations. A stable session for 5+ minutes without dropout.

---

## Critical Protocol Details

### Packet Framing (asymmetric)

**Server → client:** 3-byte header
```
[2 bytes: size, big-endian]   payload length, NOT counting the 3 header bytes
                               (verified: 1886 S→C packets in the 1.127e capture frame exactly)
[1 byte:  packet code]
[N bytes: payload]
```

**Client → server:** 10-byte header + 2-byte trailing checksum (`GSPacketIn.HDR_SIZE = 12`)
```
[2: payload size, big-endian]  total on wire = size + 12
[2: sequence]
[2: session ID]
[2: parameter]
[2: packet id]  (code is the low byte)
[N: payload]
[2: checksum]   PacketProcessor.CalculateChecksum over all preceding bytes;
                a mismatch makes the server disconnect immediately
```
Port `CalculateChecksum` exactly. A wrong checksum is the most likely cause of
"server drops me right after connect".

### Encryption

There is no rolling XOR cipher. History (from excalibar): a 12-byte XOR in
2001–02, then RSA + a modified RC4 in later clients. DOLSharp/OpenDAOC run with
encryption **off**: `PacketEncoding168` defaults to `NotEncrypted`, and
`PacketLib1115` forces it off on the 0x22 reply ("to work with Portal").
The real client needs a patched launcher (connect.exe / DAoC-Connect / daochook's
`disable_encryption`). Our own client simply never enables it.

### Version detection (first packet)

The server picks the client version from the raw size of the first TCP read
(`GameClient.CheckVersion`). Fewer than 19 bytes means a pre-1.115 layout. With 19 or more, the
version is `buf[11]*1000 + buf[12]*100 + buf[13]`. A 1.1xx 0xF4 payload is 7 bytes:
`[client type | addons][1][1][patch e.g. 27][revision letter][build u16]`
(simulator sends `0x86, 1, 1, 24, 61, 0, 0`; the 1.127e capture sends `36 01 01 1b 65 7f 05`).
Server accepts up to 1.129; OpenDAoC's docs target `game.dll` 1.127.
The 1.127e capture sends a **second 0xF4** right after: payload u32 LE 256 + 256-byte key
block. With `CLIENT_ENABLE_ENCRYPTION_RC4` off, the server ignores it
(`CryptKeyRequestHandler`: DataSize > 7 → return). Our client can skip it.

### Login

The client sends the password in **plaintext**. The server hashes it only for storage
(`CryptPassword`, MD5-based). Layout by version (`LoginRequestHandler.cs`):
- < 1.115: fixed `ReadString(20)` fields at version-dependent offsets
- 1.115–1.125: type(1) major minor build rev(1) build(2), then username/password as
  Pascal strings: u16 LE length on ≤1.124, u32 LE length on 1.125
- **1.126+: only username then password, each a u32 little-endian length + bytes**.
  In the 1.127e capture the length includes a trailing NUL (`09 00 00 00 "rustdaoc" 00`),
  followed by a trailer the server doesn't read: `2a`, u32 LE 8, 8 bytes (client id?).

Pick one client version and implement that branch only.

### Position Coordinates
On 1.124+ (`PacketLib1124.SendPlayerPositionAndObjectID`, `PlayerPositionUpdateHandler`):
- 0x20 and 0xA9 carry X, Y, Z as **little-endian float32 in region-global coordinates**
  (e.g. 390482, 748466, 370), not 16-bit zone-local values. 0x20 then has u16 object id,
  u16 heading (big-endian), flags.
- Mixed endianness: header fields and most shorts are big-endian; floats and some ints
  are little-endian. Check each field against the server writer/reader.
- Heading: 0–4096 (4096 = 360°)
```csharp
float headingRadians = (heading / 4096f) * 2f * Mathf.Pi;
```
For MVC: scale region-global coords down (e.g. /100) for Godot units. Tune from there.

### Position Heartbeat
Send 0xA9 PositionUpdate regularly even when idle. The original plan's ~30s
disconnect figure is unverified; find the actual timeout in OpenDAOC
(link-death / client timeout settings).
Implement a timer sending this packet even when the character is not moving.
This must be one of the first things implemented after zone entry — mysterious
disconnections shortly after zone entry are most likely this.

---

## Project Structure

```
DaocClient.sln
├── src/DaocProtocol/              # net8.0+ class library — NO Godot references
│   ├── Net/
│   │   ├── DaocConnection.cs      # System.Net.Sockets TCP, background receive loop,
│   │   │                          # raises packets onto a ConcurrentQueue
│   │   ├── PacketFramer.cs        # S→C 3-byte header / C→S 10-byte header + checksum
│   │   ├── Checksum.cs            # Port of PacketProcessor.CalculateChecksum
│   │   ├── PacketReader.cs        # Typed big-endian reads, fixed-length strings
│   │   └── PacketWriter.cs        # Build outbound packets
│   ├── Packets/                   # One type per opcode (CryptKey, LoginGranted, …)
│   ├── Session/
│   │   ├── AuthState.cs           # State machine (see below)
│   │   └── ClientSession.cs       # Login → char select → world; heartbeat timer
│   └── PacketLog.cs               # Hex logger (direction, opcode, bytes)
├── tests/DaocProtocol.Tests/      # xUnit; replays captured bytes (Gate 0 lives here)
├── tools/DaocCli/                 # Optional headless client: login + list chars from terminal
└── godot/                         # Godot 4 .NET project; references DaocProtocol
    ├── scripts/
    │   ├── NetworkBridge.cs       # Node: drains the protocol queue in _Process,
    │   │                          # re-emits as Godot signals
    │   └── GameState.cs           # Autoload singleton: auth state, character, position
    ├── scenes/
    │   ├── UI/LoginScreen.tscn
    │   ├── UI/CharacterSelect.tscn
    │   └── World.tscn             # Plane + capsule + camera
    └── Main.tscn
```

Rule: nothing under `src/` may reference `GodotSharp`. If protocol code needs
something from the engine, it belongs in the Godot project instead.

---

## Auth State Machine

```csharp
public enum AuthState {
    Disconnected,
    Connected,              // TCP up, waiting for 0xF4 session packet
    SessionReceived,        // Sent 0xF4, got 0x22 CryptKey → send 0xA7 login
    LoginSent,              // Awaiting 0x2A login result
    LoggedIn,               // Success, send 0xFC character overview request (realm)
    RealmSelected,          // Sent 0xFC, awaiting 0xFD character overview
    CharacterListReceived,  // Display characters, await user selection
    CharacterSelected,      // Sent 0x10, awaiting 0x28 → world init → 0x20
    InWorld                 // Receiving position updates, heartbeat active
}
```

---

## Network Thread Pattern

Socket I/O runs on a background thread in `DaocProtocol`. Packets cross to the
game loop through a `ConcurrentQueue`. Godot's main thread drains the queue each frame.

```csharp
// src/DaocProtocol/Net/DaocConnection.cs — engine-agnostic
public sealed class DaocConnection : IDisposable {
    private readonly TcpClient _tcp = new();
    private readonly ConcurrentQueue<ServerPacket> _inbound = new();
    private CancellationTokenSource _cts = new();

    public bool TryDequeue(out ServerPacket pkt) => _inbound.TryDequeue(out pkt);

    public async Task ConnectAsync(string host, int port = 10300) {
        await _tcp.ConnectAsync(host, port);
        _ = Task.Run(() => ReceiveLoop(_tcp.GetStream(), _cts.Token));
    }

    private async Task ReceiveLoop(NetworkStream stream, CancellationToken ct) {
        var header = new byte[3];
        while (!ct.IsCancellationRequested) {
            // S→C header: [u16 size, big-endian][u8 code]; size excludes the header
            // (verified against the 1.127e capture).
            await stream.ReadExactlyAsync(header, ct);
            int size  = BinaryPrimitives.ReadUInt16BigEndian(header);
            byte code = header[2];
            var payload = new byte[size];
            await stream.ReadExactlyAsync(payload, ct);   // handles partial reads
            _inbound.Enqueue(new ServerPacket(code, payload));
        }
    }
    // Send: PacketFramer builds the 10-byte C→S header + payload + checksum.
}

// godot/scripts/NetworkBridge.cs — the only place Godot meets the protocol
public partial class NetworkBridge : Node {
    [Signal] public delegate void PacketReceivedEventHandler(byte code, byte[] payload);
    public DaocConnection Connection { get; } = new();

    public override void _Process(double delta) {
        while (Connection.TryDequeue(out var pkt))
            EmitSignal(SignalName.PacketReceived, pkt.Code, pkt.Payload);
    }
}
```

---

## Raw Packet Logger — Add Immediately, Before Any Other Logic

```csharp
// Lives in DaocProtocol (no GD.Print there). The Godot side sets
// PacketLog.Sink = GD.Print; tests and the CLI set it to Console.WriteLine.
public static Action<string> Sink = Console.WriteLine;

public static void LogPacket(byte[] raw, string direction) {
    var hex = BitConverter.ToString(raw).Replace("-", " ");
    // Opcode offset: byte 2 for S→C (3-byte header), byte 9 for C→S (10-byte header)
    byte code = direction == "S→C" ? raw[2] : raw[9];
    Sink($"{direction} [{code:X2}] len={raw.Length}: {hex}");
}
```

Every inbound and outbound packet gets hex-logged before processing. Compare
against PacketLogConverter captures byte-for-byte when debugging. Wrap in a
`[Export] public bool DebugPackets = true;` toggle so it can be disabled.

---

## Minimal World Scene

No DAoC assets needed for MVC:

```
World.tscn
├── WorldEnvironment       # sky color, ambient light
├── DirectionalLight3D     # basic sun
├── StaticBody3D           # ground plane
│   ├── MeshInstance3D (PlaneMesh, 1000x1000 units)
│   └── CollisionShape3D
├── PlayerRepresentation   # MeshInstance3D
│   └── CapsuleMesh        # placed at received X,Y,Z coordinates
└── Camera3D               # follows PlayerRepresentation
```

When 0x20 PositionAndObjectID arrives: parse X/Y/Z → set `PlayerRepresentation.GlobalPosition`.

---

## Development Gates (Do Not Skip)

```
Gate 0 — Before opening Godot (work happens in src/ + tests/):
  ✓ Local OpenDAOC running (Docker, pinned version)
  ✓ Original DAOC client completes login → zone entry
  ✓ PacketLogConverter + Wireshark captures saved and annotated
  ✓ DaocProtocol.Tests: checksum + framer pass against captured bytes
  ✓ (optional) tools/DaocCli logs in and lists characters headlessly

Gate 1 — Scaffolding:
  ✓ TCP connect + 0xF4 handshake
  ✓ Raw packet hex logger active
  ✓ Connects without crashing

Gate 2 — Auth through character list:
  ✓ 0xA7 login layout (for the pinned client version) verified against capture
  ✓ Character names display in Godot UI
  → SHARE WITH COMMUNITY at this point (OpenDAOC Discord)

Gate 3 — MVC complete:
  ✓ Zone entry received
  ✓ Capsule at correct coordinates
  ✓ Session stable 5+ minutes (heartbeat working)

Gate 4 — Publish:
  ✓ Error handling (disconnect, auth failure, timeout)
  ✓ Debug packet toggle
  ✓ Protocol documentation from captures + code
  ✓ Open source on GitHub with setup instructions
```

---

## Key External Resources

### Server
- **OpenDAOC Core**: https://github.com/OpenDAoC/OpenDAoC-Core
- **DOLSharp (original)**: https://github.com/Dawn-of-Light/DOLSharp

### Packet Reference (DOLSharp source — read as spec, do not copy verbatim)
```
GameServer/packets/Server/IPacketLib.cs                   → all opcode enums
GameServer/packets/Client/168/LoginRequestHandler.cs       → 0xA7 layout (per-version branches)
GameServer/packets/Client/168/CharacterSelectRequestHandler.cs → 0x10
GameServer/packets/Client/168/CharacterOverviewRequestHandler.cs → 0xFC
GameServer/packets/Client/168/PlayerPositionUpdateHandler.cs → 0xA9
GameServer/packets/Server/PacketLib*.cs                  → server packet writers
                                                           (SendLoginGranted, SendCharacterOverview, …)
GameServer/gameutils/GSPacketIn.cs                       → client header layout
GameServer/packets/Server/PacketProcessor.cs             → framing + CalculateChecksum
```

### Protocol Tools
- **OpenDAoC-ClientSimulator**: https://github.com/OpenDAoC/OpenDAoC-ClientSimulator —
  the OpenDAoC team's headless C# client for load testing (client 1.124, ~1.6k lines, no
  license → reference only). Drives login → world → position/ping loop. Creates accounts
  and characters by direct SQL; UDP is stubbed out ("Not working"); handlers mostly ignore payloads.
- **RustDAoC** (GPL-3.0, Rust, 1.127e): https://github.com/latentcollapse/RustDAoC —
  replacement client; `caer-protocol` crate covers login → world, checksum test vectors, plus
  the capture above. Single author, created 2026-08. Spec + golden bytes; don't copy (license).
- **PacketLogConverter**: https://github.com/Dawn-of-Light/PacketLogConverter (separate repo,
  not in DOLSharp; dormant since 2020). .NET 3.5 WinForms with ~320 per-opcode decoders up to
  1.126. Reads DOL server logs and DaocLogger; no pcap.
- Other references (reading only): Eve-of-Darkness/eve-of-darkness (Elixir packet DSL + tests,
  no license, dead 2023), dbeattie71/OnlyAModel (C# Span-based protocol lib for 1.125, no license
  file), jpbriend/daoc-packet-logger (Go TCP proxy + hex dump), thekroko/uthgard-opensource
  (GPL-3.0: RC4 encoding for 1.110, says RC4 is needed for client UDP; MPK reader).
- No Wireshark dissector for DAoC exists (searched 2026-10-13).
- **DaocClientLib** (C#, for future asset reading): https://github.com/dol-leodagan/DaocClientLib

### Local Test Server (offline, with bots)
- **catastrophic-impact**: https://github.com/catastrophic-impact/daoc-offline-linux-server-bots
  - Use for later testing (bots add variables — use vanilla OpenDAOC for initial dev)
  - Connects via: `wine connect.exe game.dll 127.0.0.1 <account> <password>`
  - Server-side Linux native, client still needs Wine on Linux/macOS

---

## Version Axes (don't conflate them)

| Axis | Determined by | Ours |
|---|---|---|
| Client / wire protocol | `game.dll` build; announced in 0xF4, echoed in 0x22 | **1.127e** |
| Ruleset / content era ("1.65", "1.58", …) | server code + database content | 1.65 (OpenDAoC's target) |
| Server software | OpenDAoC release tag (date-based, unrelated to DAoC patch numbers) | 2026.09.36 |
| Database | OpenDAoC-Database repo (unversioned; ships inside the server image) | pinned via image digest |

"1.65" (Oct 2003) = the last Shrouded Isles-era patch before Trials of Atlantis (1.66+): classic
+ SI classes and races, housing, Old Frontiers RvR, no Master Levels or artifacts. It is the
freeshard "classic" standard. (User-supplied background, not checked against primary sources.)

One server build serves many protocol versions (one `PacketLib<ver>` per client version, selected
per connection in `GameClient.CheckVersion`, exact match or disconnect). 62 versions: 1.68–1.99 and
1.100–1.129. Each class inherits from the previous one and overrides only what changed
(`PacketLib1129` is empty, so 1.129 = 1.128 on the server side). The revision letter is never used
to select a format. The oldest accepted client (1.68) is newer than the 1.65 rules.
Public write-up: https://eveofdarkness.org/versions/. Freeshards pair a modern client with old rules.
Our client implements exactly one protocol version (1.127); the ruleset only changes what content
the server sends, not packet layouts.

## OpenDAOC Version Pinning

**Pin one OpenDAOC version and do not upgrade during MVC development.**
Packet format differences between versions silently break things.
Pin the DAOC client version that works with your chosen OpenDAOC version.
Verify both match before writing any protocol code.

Default port: TCP 10300, UDP 10400.

**Pinned (2026-10-13):** OpenDAoC-Core `2026.09.36` (@f0aaa1e), image
`ghcr.io/opendaoc/opendaoc-core@sha256:89da3785…`, MariaDB 10.6.28. The DB seed is baked
into the server image, so the digest pins world data too. Setup and start: `server/`
(`REGION_IP=<address clients use> ./setup.sh` once, then `./up.sh`). `REGION_IP` is sent to
clients in the region list, so it must be reachable from the client. MariaDB is published on
localhost only (user `root`, password in `server/.env`, database `opendaoc`).
Auto account creation is on. ~400 ERR lines at startup are data/code content mismatches
(NPC templates, spells), not network-related.

---

## Community Context

### Server Projects
| Project | Language | Status | Notes |
|---|---|---|---|
| OpenDAOC | C# | Active | ECS architecture, game loop, 1.65 focus |
| DOLSharp | C# | Slow | Event-driven, broader patch coverage |
| Eve of Darkness (server) | Elixir | Last commit 2019-09 (repo touched 2023-01) | Packet DSL with tests; homepage was eveofdarkness.org |
| catastrophic-impact | C# (OpenDAOC fork) | Active | Linux-native, AI bots |

### Community Locations
- OpenDAOC Discord (primary active channel)
- DOL SourceForge forums (historical depth)
- Reddit r/daoc
- GitHub Issues/PRs on OpenDAOC-Core (most technically honest discussions)

---

## Protocol Knowledge State

### Well Documented (TCP)
Login/auth, character management, world state, combat, inventory, chat,
RvR/keep packets (0x69, 0x6C, 0x6D range), NPC movement.

### Gap: UDP Movement
- UDP port 10400 for high-frequency position updates
- Basic framing known, exact update rate and triggering logic approximated
- Client-side prediction reconciliation algorithm undocumented
- Eve of Darkness explicitly noted "UDP not supported by portal launcher,
  not documented" — this remains the clearest gap in protocol documentation
- Analyzing UDP traffic with Wireshark against a live OpenDAOC session is
  a valuable contribution (traffic analysis, not just code)

### DOL vs OpenDAOC Gaps (potential contribution area)
OpenDAOC targeted 1.65 accuracy and ECS architecture. Things in DOLSharp
not yet ported to OpenDAOC:
- Higher patch level (1.108, 1.124–1.127) protocol support
- Housing system (always partial in DOLSharp, not ported)
- Some crafting edge cases, guild management features
- Uthgard freeshard contributions (validated mechanics, some open-sourced)

Documenting this gap as a structured comparison table would be a valued
first community contribution.

---

## Coordinate System Reference

```
1.124+ wire format: X, Y, Z float32 LE, region-global (zone offsets already applied).
Older clients used 16-bit zone-local values — irrelevant for our pinned version.
Heading: 0–4096 (4096 = 360°)

float headingRadians = (heading / 4096f) * 2f * Mathf.Pi;

Zone offsets matter only for mapping region-global coords onto zone terrain later.

Initial Godot scale: divide by 100. Tune from captures.
```

---

## What This Project Is NOT

- Not a replacement for other community client efforts (e.g. UE5-based ones); different engine, different goals
- Not a full game client (no assets, no animations, no sound, no combat UI)
- Not competing with OpenDAOC (server stays as-is, this is a new client)
- Not production ready (MVC is a foundation, not a release)

The value is: open source, C#, Godot, documented, community-friendly starting
point that any developer familiar with the existing ecosystem can contribute to.

---

## Prior Research (Claude Code sessions, 2026-10-09 / 10-13)

- **excalibar** (CapnBry, @ad88819): source for the encryption history and the
  0xA8 opcode remap.
- **daochook** (atom0s): client hook with a binary core and Lua scripts;
  `patch.func.disable_encryption`; supports clients 1.113 and 1.127e.
- **OfflineDAoC** (shadowofze): OpenDAoC fork with server-side bots
  (`GameBot : GameNPC, IGamePlayer` + a no-op `BotDummyPacketLib`).
  Separate from catastrophic-impact's bot fork.
- **OpenDAoC-Core** (@f0aaa1e): its own database layer (MySQL + SQLite). A
  Postgres back end is possible; the main risk is case-insensitive collation.
- **Renderer track** (parallel to the Godot client): NIF 4.1/4.2 → glTF
  converter is step 1. Open question: does the target `game.dll` import d3d8 or d3d9?
  dgVoodoo2 already works. RTX Remix / ReShade are the low-effort options.

---

## Private notes

Notes about people, local infrastructure and unverified planning material live in the
git-ignored `CLAUDE.local.md` (and raw planning exports in `private/`). Never commit them.
