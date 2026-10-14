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
In that capture 0x10 CharacterSelectRequest and 0xFC CharacterOverviewRequest carry a
**single byte** (0x10: `00`; 0xFC: `00`, then `01`), not ClientSimulator's 1.124 fixed strings
(28-byte name, 24-byte "<account>-S"). Check the server handlers before implementing them.

**Login verified live (2026-10-13)** against our OpenDAoC 2026.09.36 with `tools/DaocCli`:
0xF4 → 0x22 ("1.127", key length 0) → 0xA7 → 0x2A (new account auto-created) or 0x2C
IncorrectPassword; the server log showed version 1127 detected. Our 0xF4 client-type byte 0x36
(copied from the capture) is logged as type LabyrinthOfTheMinotaur plus add-on bits.

**Character screen (1.127, verified live 2026-10-13; parser in `Packets/CharacterPackets.cs`):**
- 0x10 payload `00`. OpenDAoC ignores it and answers 0x2A LoginGranted (again) + 0x28 SessionID.
- 0x28 payload: session id as u16 **little-endian** (the C→S header field is big-endian).
- 0xFC request payload: one realm byte. 0 → server answers 0xFE (realm byte + 12 zero bytes;
  None = not chosen yet or all realms allowed). 1/2/3 (Alb/Mid/Hib) → 0xFC overview.
- 0xFC overview (`PacketLib1126.SendCharacterOverview`): 4 × u32 LE header (u32[0] bit 0 =
  realm switcher), then 10 slots; empty slot = `00`, occupied slot starts with level. An account
  with no characters gets the 16-byte header only, no slots. Strings are u32 LE length (counting
  a trailing NUL) + bytes + NUL; empty string = length 0.
- Recorded overview's race/gender byte is 0x13: bit 0x10 isn't produced by OpenDAoC's encoding
  `((race & 0x10) << 2) + (race & 0x0F) | gender << 7` (the recording's server may differ).

**Character create/delete (1.127, verified live 2026-10-14; `CharacterCreateRequest` in
`Packets/CharacterPackets.cs`):** C→S 0xFF, one character per packet on 1.125+
(`CharacterCreateRequestHandler.CreationCharacterData`). No recording has one; tests decode our
payloads with a port of the server's reader.
- Slot byte is numbered across realms (Alb 0–9, Mid 10–19, Hib 20–29); DB `AccountSlot` = realm × 100 + slot.
- Operation byte: 1 create, 2 customise, 3 delete. Race/gender byte: `race & 0x1F | female << 7`.
- After the 8 stat bytes: 43 skipped bytes, "new constitution", trailing 0. RustDAoC instead
  writes a region byte at +40 and new-con at +44; OpenDAoC doesn't read region on 1.126+.
- Delete: send realm 0. The server converts the slot with the account's saved realm, and a
  non-zero realm byte makes it subtract the realm offset twice: on Mid/Hib the slot then matches
  no character, so nothing is deleted and nothing is answered.
- The server answers an accepted change with 0x2A LoginGranted; the client re-requests 0xFC.
  An invalid create also gets 0x2A (silently ignored); deleting an empty slot gets nothing.
- A bad name (< 3 chars, or not `^[A-Z][a-zA-Z]`) disconnects a player and, with BAN_HACKERS,
  bans the account. Our builder refuses such names.
- Stats must be race bases + exactly 30 points (`CharacterStatValidator`: +1 per point, +2
  above +10, +3 above +15). Briton Armsman requested → stored as Fighter (class 14): OpenDAoC's
  StartAsBaseClass script, consistent with 1.65 rules. Start location: Prydwen Keep.
- 0xFC with a realm (1.124+ path) sets the account's realm to that page, all-realms or not; delete
  converts its slot with it, and later 0xFE replies return it (when all realms aren't allowed).
- Login 0x2A server type: 1 when `allow_all_realms` is on (or GM/admin), otherwise 7 (1.127's
  replacement for 0; see open question C9). Overview flag bit 0 = all realms allowed.

**World entry (1.127, verified live 2026-10-14; `Packets/WorldPackets.cs`, `ClientSession.EnterWorldAsync`):**
- 0x10 never carries a name on 1.127. The character is chosen by slot (cross-realm numbering, as
  for create) in 0x9D RegionListRequest (→ 0xB1; server reads only the slot byte) and 0xD4
  WorldInitRequest. 0xD4 converts the slot with the account's current realm, so list that realm last.
- Our order: 9D → B1, 10 → 28, D4 → burst containing 0x20 PositionAndObjectID and ending with
  0x4E ControlledHorse, then E8 PlayerInit and BF GameOpen → 0x2D. (The recorded client sent E8
  and BF right after D4; OpenDAoC's 0xD4 handler is async, so we wait for the burst.)
- 0x20 (1.124+): x, y, z f32 LE, object id u16 BE, heading u16 BE, dungeon offsets 2 × u16,
  region (skin) u16, flags u8 (0x80 diving), then 5 bytes. Live: Eodarms at (574797, 530161, 2896),
  region 1, heading 3116 = DB Direction 7212 & 0x0FFF.
- 0xA9 (1.127, 40 bytes): x, y, z, speed, vertical speed f32 LE; session, object id, zone u16 BE;
  state flags, unknown; falling damage, heading u16 BE; action flags; 2 unknown; health %; 4 zero.
  All 92 recorded 0xA9 rebuild byte for byte. The zone must exist or the server kicks to the
  character screen; we look it up in `World/zones.tsv` (OpenDAoC-Database, GPL).
- 0xA3 ping: 4 skipped bytes, u32 BE timestamp, 4 zero. 0x29 reply echoes it, then sequence + 1.
- Timeouts (OpenDAoC source): link death after 5 s without 0xA9, disconnect after 150 s without
  0xA3. `KeepAliveAsync` sends 0xA9 every 1 s and 0xA3 every 10 s (our choice; retail rates unknown).
  Live 2026-10-14 (`DaocCli --enter 0 --stay 330`): 330 s in world, 31/31 pings answered, ~23
  packets/s received (mostly 0xA1 NPC updates), no link death until we closed the socket.
- Leaving: closing the socket makes the character link-dead for 60 s, and logging in meanwhile
  is refused with AccountIsInLogoutProcedure. A clean logout is the `/quit` command (0xAF), which
  starts a server-side quit timer that ends with S→C 0xA4. Not implemented yet (Gate 4).

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
Header values in the 1.127e capture (all 171 client packets): sequence starts at 0 and goes up
by exactly 1; session id is 0 until 0x28 SessionID, then the assigned id; parameter is mostly
0xFFFF, sometimes 0xFFFE or 0 (ClientSimulator always sends 0); the packet id's high byte is
usually 0 but not always (e.g. the first 0xF4 is `01 F4`). The server ignores the high byte;
`ClientPacket` keeps it so real packets rebuild byte for byte.
Ported in `src/DaocProtocol/Net/Checksum.cs` (byte-wrapping accumulators, int arithmetic
before the final ushort truncation). The capture tests catch a port without byte wrap; the two
RustDAoC hand vectors alone do not.

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
Implemented (`ClientSession.KeepAliveAsync`): 0xA9 every 1 s even when idle, 0xA3 every 10 s.
OpenDAoC marks a player link-dead after 5 s without 0xA9 and disconnects after 150 s without
0xA3 (see World entry above).

---

## Project Structure

.NET: Microsoft's SDK 10 in `/usr/local/share/dotnet` (Homebrew cask `dotnet-sdk`, pulled in by
`godot-mono`). Godot and .NET app launchers find it there without `DOTNET_ROOT`.
Godot: 4.7.2 .NET build (`brew install --cask godot-mono`, needs a real terminal for sudo).
Its editor tooling targets net8.0 but runs on the .NET 10 runtime (verified 2026-10-14).

```
eod-client.slnx                    # .NET 10 SDK; Directory.Build.props: warnings as errors
├── src/DaocProtocol/              # net8.0 class library (Godot-compatible) — NO Godot references
│   ├── Net/
│   │   ├── Checksum.cs            # ✓ port of PacketProcessor.CalculateChecksum
│   │   ├── ClientPacket.cs        # ✓ C→S 10-byte header + checksum: TryRead / ToBytes
│   │   ├── ServerPacket.cs        # ✓ S→C 3-byte header: TryRead / ToBytes
│   │   ├── FrameResult.cs         # ✓ Complete / Incomplete / BadChecksum
│   │   ├── PayloadReader.cs       # ✓ endian-explicit reads, int-length strings; throws on short data
│   │   ├── PayloadWriter.cs       # ✓ the matching writer
│   │   ├── DaocConnection.cs      # ✓ TCP + background receive loop → Channel; ReceiveAsync or
│   │   │                          #   TryReceive (per-frame polling); serialised sends
│   │   └── PacketLog.cs           # ✓ per-connection logger, Sink + Hex; never hex-logs 0xA7
│   ├── Packets/                   # ✓ ClientVersion, LoginPackets, CharacterPackets (0x10, 0x28,
│   │                              #   0xFC/0xFE overview, 0xFF create/delete), WorldPackets
│   │                              #   (0x9D, 0xD4, 0xE8, 0xBF, 0x20, 0xA9, 0xA3/0x29)
│   ├── World/                     # ✓ Zones.cs + zones.tsv (embedded; OpenDAoC-Database, GPL)
│   └── Session/
│       └── ClientSession.cs       # ✓ login → character screen → list/create/delete → world;
│                                  #   KeepAliveAsync; reply timeout
├── tests/DaocProtocol.Tests/      # net10.0 xUnit; Fixtures/ holds the RustDAoC 1.127e capture
│                                  # (GPL, provenance in Fixtures/README.md). FakeServer: localhost
│                                  # server replaying recorded replies, for ClientSession tests
├── tools/DaocCli/                 # ✓ thin ClientSession front end: DAOC_PASSWORD=… dotnet run --project
│                                  #   tools/DaocCli -- <host> <account> [--port N] [--realm R]
│                                  #   [--create Name | --delete slot] [--hex]
│                                  #   (exit 0 granted, 2 denied, 1 error; never hex-logs 0xA7)
└── godot/                         # ✓ Godot 4.7.2 .NET project (EodClient.csproj, net8.0, in the
    │                              #   solution); references DaocProtocol
    ├── project.godot              # main scene: scenes/Main.tscn
    ├── scenes/Main.tscn           # ✓ root Control with CharacterScreen.cs
    └── scripts/
        ├── CharacterScreen.cs     # ✓ UI built in code: login form, realm, character list, Enter
        │                          #   world, hex-log toggle. Headless check: DAOC_PASSWORD=… Godot
        │                          #   --headless --path godot -- --autologin <host> <account> [port]
        │                          #   [--enter <slot> [--stay <seconds>]]
        └── WorldView.cs           # ✓ Gate 3: ground, light, camera, capsule at (X, Z, Y)/100 with a
                                   #   heading nose; drains packets per frame; status overlay
```

Rule: nothing under `src/` may reference `GodotSharp`. If protocol code needs
something from the engine, it belongs in the Godot project instead.

---

## Session and threading

`ClientSession.State` (`SessionState`) has the states that exist so far: Connected → LoggedIn →
CharacterScreen. World states (character selected, in world, heartbeat) come with Gate 3. Each
session method sends a request and awaits its reply, skipping unrelated packets (e.g. 0x29),
and throws `TimeoutException` after `ReplyTimeout` (10 s): some server paths never answer.

`DaocConnection` reads the socket on a background task and queues whole packets in a
`System.Threading.Channels` channel. In Godot, awaiting the session from the main thread is
fine (continuations return to it). Once in the world, unprompted packets (positions, chat) will
be drained per frame with `TryReceive` and re-emitted as signals by `godot/scripts/NetworkBridge.cs`,
the only place Godot meets the protocol. Sends are serialised, so a heartbeat timer may send
while a session method waits.

Logging: `new DaocConnection(new PacketLog(sink, hex))` logs every packet both ways. Godot
passes `GD.Print`, the CLI `Console.WriteLine`; expose `Hex` as the debug-packet toggle.

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
    (done 2026-10-13 against the RustDAoC 1.127e capture; repeat with our own capture)
  ✓ (optional) tools/DaocCli logs in and lists characters headlessly

Gate 1 — Scaffolding:                                      (done 2026-10-14)
  ✓ TCP connect + 0xF4 handshake
  ✓ Raw packet hex logger active
  ✓ Connects without crashing

Gate 2 — Auth through character list:                      (done 2026-10-14)
  ✓ 0xA7 login layout (for the pinned client version) verified against capture
  ✓ Character names display in Godot UI (seen by the owner in the editor's Run window)
  → SHARE WITH COMMUNITY at this point (OpenDAOC Discord), once it's worth showing

Gate 3 — MVC complete:                                     (done 2026-10-14)
  ✓ Zone entry received (0x20; Eodarms in Camelot Hills, region 1, zone 0)
  ✓ Capsule at correct coordinates (seen by the owner in the Godot window)
  ✓ Session stable 5+ minutes (330 s via DaocCli, 31/31 pings; heartbeat 0xA9 1 s, 0xA3 10 s)

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

### Packet Reference (DOLSharp / OpenDAoC source: GPL, so port with attribution in the file header)
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

## Open questions

`docs/open-questions.md` lists oddities and unconfirmed behaviour (OpenDAoC vs retail, conflicting
references). Add an entry whenever something surprises us; close it with the evidence.

## Private notes

Notes about people, local infrastructure and unverified planning material live in the
git-ignored `CLAUDE.local.md` (and raw planning exports in `private/`). Never commit them.
