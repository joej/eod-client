# DAoC client protocol 1.127 (as spoken by OpenDAoC)

What a client needs to log in to an [OpenDAoC](https://github.com/OpenDAoC/OpenDAoC-Core)
server, manage characters, enter the world, stay connected and log out, using the TCP protocol of
DAoC client **1.127**. Everything here is implemented in `src/DaocProtocol` and was checked
against two sources:

- **Server source:** OpenDAoC-Core 2026.09.36 (commit f0aaa1e). Handler and writer names below
  are its class and method names.
- **Traffic:** a recorded 1.127e session from [RustDAoC](https://github.com/latentcollapse/RustDAoC)
  (see `tests/DaocProtocol.Tests/Fixtures/README.md`), and our own client against a live server.

Each section says how it was verified. Unresolved details are tracked in
[open-questions.md](open-questions.md) (referenced as C1, L5, …). Retail (Mythic, EA, Broadsword)
servers may differ; we have not compared against them yet.

Not covered: encryption beyond the short note under Login (OpenDAoC runs without it), UDP (port 10400; we use TCP only), anything
after entering the world besides keep-alive and logout.

## Conventions

- **Byte order is mixed.** Header fields and most 16-bit values are big-endian; floats, most 32-bit
  values and string lengths are little-endian. Every layout below names the order; check each field
  against the server's reader or writer before trusting a guess.
- **Strings:**
  - *int-length string*: u32 LE length that **counts a trailing zero byte**, the Latin-1 bytes,
    then the zero. An empty string from the server is length 0; our client writes length 1 + `00`.
  - *byte-length string*: u8 length, then the bytes (no zero).
- **Opcodes:** modern client opcodes are the old ones XOR 0xA8 (the server source notes e.g.
  `0xA9 = 0x01 ^ 168`).
- **Character slots** are numbered across realms: Albion 0–9, Midgard 10–19, Hibernia 20–29.
  The database stores `realm × 100 + slot` (101–109, …). The server converts a slot using the
  account's *current* realm, which is the realm of the last character list requested (C3, C11).

## Framing

TCP port 10300. No encryption: OpenDAoC answers 0xF4 with key length 0 (`PacketLib1115` forces it
off), and a client that never enables encryption needs nothing else.

**Server → client:** 3-byte header, no checksum.

```
u16 BE  payload size (not counting these 3 bytes)
u8      code
...     payload
```

**Client → server:** 10-byte header, payload, 2-byte checksum (`GSPacketIn.HDR_SIZE` = 12).

```
u16 BE  payload size (total on the wire = size + 12)
u16 BE  sequence      starts at 0, +1 per packet
u16 BE  session id    0 until 0x28 SessionID arrives, then the assigned id
u16 BE  parameter     the recorded client mostly sends 0xFFFF (sometimes 0xFFFE or 0); we send 0 and the server accepts it
u16 BE  packet id     low byte = code; the server ignores the high byte
...     payload
u16 BE  checksum      over everything before it, header included
```

**Checksum** (`PacketProcessor.CalculateChecksum`); a mismatch disconnects immediately:

```
val1 = val2 = 0x7E                  (both bytes: every addition wraps at 256)
for each byte b:  val1 += b;  val2 += val1
checksum = (val2 − ((val1 + val2) << 8)) mod 65536     (computed as int, then truncated)
```

Examples: empty input → `0x047E`; `[0x01]` → `0x84FD`. Those two never wrap, so they don't catch a
port that forgets the byte arithmetic; real traffic does.

*Verified:* all 171 recorded client packets have valid checksums; all 1,886 recorded server packets
split exactly with the 3-byte header; every packet rebuilds byte for byte (`CaptureFramingTests`).

## Session overview

```
C→S F4 CryptKeyRequest        S→C 22 CryptKey
C→S A7 LoginRequest           S→C 2A LoginGranted | 2C LoginDenied
C→S 10 CharacterSelectRequest S→C 2A LoginGranted (again), 28 SessionID
C→S FC overview, realm 0      S→C FE Realm                       ← character screen
C→S FC overview, realm 1–3    S→C FC CharacterOverview
   (FF create/delete          S→C 2A, then the client lists again)
C→S 9D RegionListRequest      S→C B1 region list
C→S 10 CharacterSelectRequest S→C 2A, 28
C→S D4 WorldInitRequest       S→C burst: … 20 PositionAndObjectID … ends with 4E
C→S E8 PlayerInitRequest
C→S BF GameOpenRequest        S→C 2D GameOpenReply                ← in the world
C→S A9 PositionUpdate every 1 s, A3 PingRequest every 10 s (S→C 29 PingReply)
C→S AF "&quit"                S→C … A4 Quit after the quit timer
C→S FC overview, realm 1–3    S→C FC CharacterOverview           ← back at the character screen
```

The recorded client sends E8 and BF straight after D4; we wait for the burst first because
OpenDAoC's D4 handler loads the character asynchronously. The recorded client also sends a second
0xF4 (a 256-byte key block, ignored by the server) and pings between steps.

## Login

**0xF4 CryptKeyRequest** (C→S), 7 bytes. The server picks the protocol version from the first TCP
read (`GameClient.CheckVersion`: `buf[11]*1000 + buf[12]*100 + buf[13]`, exact match required).

```
u8  client type | add-ons     recorded 1.127e: 0x36 (logged by OpenDAoC as LabyrinthOfTheMinotaur + add-ons; L1)
u8  major (1)  u8 minor (1)  u8 patch (27)
u8  revision letter ('e')
u8  build high  u8 build low  recorded: 7F 05
```

**0x22 CryptKey** (S→C, `PacketLib1126.SendVersionAndCryptKey`): u32 LE key length (0 = no
encryption), 5-byte version text `"1.127"`, revision byte, build high, build low.

**Second 0xF4** (C→S, recorded 1.127e): u32 LE 256 + a 256-byte block, the client's RC4 session key.
On retail it is RSA-encrypted with the public key from 0x22; OpenDAoC ignores it unless
`client_enable_encryption_rc4` is on, then uses it in the clear (`CryptKeyRequestHandler`). The
cipher is a modified RC4 (`PacketEncoding1110`): standard key schedule, a fresh S-box copy per
packet, second half of the packet before the first, and each plaintext byte added into `j`.
We have not tested encryption.

**0xA7 LoginRequest** (C→S, `LoginRequestHandler`, 1.126+ branch): account, then password, each an
int-length string. **The password is plain text** on the wire; the server only hashes it for
storage. The recorded client appends 13 bytes the server doesn't read (L3). Auto account creation
(on by default) means an unknown account name with any password creates a new account.

**0x2A LoginGranted** (S→C, `PacketLib1127.SendLoginGranted`): byte-length account name,
byte-length server name, server id, server type, trial flag. Server type: 1 when `allow_all_realms`
is on (or for GM accounts), otherwise the server rules' value, where 0 (a normal server) is sent as
7 on 1.127 (C9).

**0x2C LoginDenied** (S→C, `PacketLib168.SendLoginDenied`): reason (OpenDAoC's `eLoginError`,
e.g. 0x01 incorrect password, 0x11 account is in logout procedure), 0x01, two version bytes, 0x00.

*Verified:* live login granted, wrong password denied, recorded 0xF4 and 0xA7 rebuilt byte for byte.

## Character screen

**0x10 CharacterSelectRequest** (C→S): payload `00`. OpenDAoC ignores the content and answers
0x2A again plus **0x28 SessionID** (S→C): u16 **little-endian** session id (the header field it
goes into is big-endian). On 1.127 this packet never carries a character name (L8).

**0xFC CharacterOverviewRequest** (C→S, `CharacterOverviewRequestHandler._HandlePacket1124`): one
realm byte.
- 0 → **0xFE Realm** (S→C): realm byte, 12 zero bytes. 0 = not chosen yet, or all realms allowed.
- 1/2/3 → **0xFC CharacterOverview** (S→C). The server also makes this the account's current
  realm, which later deletes and world entry use.

**0xFC CharacterOverview** (S→C, `PacketLib1126.SendCharacterOverview`): 4 × u32 LE header
(bit 0 of the first = all realms allowed), then 10 slots. An account with no characters gets the
header only. An empty slot is a single `00`; an occupied slot:

```
u8   level (non-zero)
str  name                            int-length string
u32  0x18, u8 1
10   face and hair, armour extensions, hood, customisation step, mood
13   zero
str  location, class name, race name int-length strings
u16 LE model   u8 region   u8 expansion zone flag
16   armour models   16 armour colours   8 weapon models
8    stats: str dex con qui int pie emp cha
u8   class id   u8 realm
u8   race/gender: race = (b & 0x0F) | ((b & 0x40) >> 2); female = b & 0x80
2    active weapon slots   3 SI flag, constitution, unknown
```

*Verified:* the recorded overview decodes to one level 50 Highlander Paladin; live accounts with
0, 1 and 2 characters.

**0xFF CharacterCreateRequest** (C→S, `CharacterCreateRequestHandler.CreationCharacterData`,
1.125+ branch): one character per packet.

```
u8   slot (cross-realm numbering)
str  name                       int-length string
u32  0x18
u8   custom mode (0 = no face customisation; the next six bytes are then ignored)
6    eye size, lip size, eye colour, hair colour, face type, hair style
3    skipped   u8 mood   9 skipped
u8   operation: 1 create, 2 customise, 3 delete
u8   customise type   2 skipped
str  location, class name, race name   int-length strings, skipped (we send empty)
u8   class id   u8 realm
u8   race & 0x1F | female << 7
u16 LE creation model
8    stats: str dex con qui int pie emp cha
43   skipped
u8   "new constitution" (customisation only)   u8 0
```

- **Create:** the name must be at least 3 characters and match `^[A-Z][a-zA-Z]`, or the player is
  disconnected (and banned with `ban_hackers` on; C6). Stats must be the race's base values plus
  exactly 30 points (+1 each, +2 above +10, +3 above +15). A rejected create gets the same 0x2A as
  a successful one (C1); only the next listing tells. OpenDAoC's StartAsBaseClass script turns the
  requested class into its base class (Armsman → Fighter).
- **Delete:** operation 3, empty name, realm byte **0** (a non-zero realm makes the server apply
  the realm offset twice; C3). Deleting an empty slot gets no answer at all (C2).
- RustDAoC's builder puts a region byte 40 bytes after the stats and new constitution at +44;
  OpenDAoC reads neither the region nor that layout on 1.126+ (C7).

*Verified:* our payloads decode correctly with a port of the server's reader; live create and
delete, confirmed in the database.

## Entering the world

**0x9D RegionListRequest** (C→S): the slot byte. The server reads only that byte (the recorded
client sends 28 more; L6) and answers **0xB1** with the region list.

**0xD4 WorldInitRequest** (C→S): the slot byte. The server loads the character and sends a burst
of state packets that includes **0x20** and ends with **0x4E** ControlledHorse.

**0x20 PositionAndObjectID** (S→C, `PacketLib1124.SendPlayerPositionAndObjectID`):

```
f32 LE x, y, z          region-global coordinates
u16 BE object id        the player's object id (not the session id)
u16 BE heading          0–4095 = a full turn (direction of 0 unchecked; W2)
u16 BE, u16 BE          dungeon zone offsets / 8192 (0 outside dungeons)
u16 BE region           the region's "skin": its id, except for instances
u8     flags            0x80 diving enabled, 0x01 underwater
5      unknown / housing server name
```

**0xE8 PlayerInitRequest** (C→S): payload `01` (ignored). **0xBF GameOpenRequest** (C→S): `00`
(1 would confirm a working UDP channel). The server answers **0x2D GameOpenReply** and the
connection is now "playing".

*Verified:* recorded D4, E8, BF match ours byte for byte; recorded 0x20 parses to the position the
recorded client then reported; live entry placed our character at its database position.

## Staying connected

| What | OpenDAoC limit (source) | Our client |
|---|---|---|
| 0xA3 PingRequest | disconnect after **150 s** without one, in every state including the character screen (`ClientService.HARD_TIMEOUT`) | every 10 s from the character screen on |
| 0xA9 PositionUpdate | link death after **5 s** without one while playing (`PlayerMovementComponent.SOFT_LINK_DEATH_THRESHOLD`) | every 1 s in the world, even when idle |
| Idle kick | to the character screen after 60 min, only if `kick_idle_player_status` is on (off by default) | — |

*Verified live:* 330 s in the world with 31/31 pings answered; 180 s at the character screen
without pings → disconnected at exactly 150 s; with pings → still connected.

**0xA3 PingRequest** (C→S): 4 bytes the server skips, u32 **BE** timestamp, 4 zero bytes.
**0x29 PingReply** (S→C): the timestamp, 4 zero bytes, the ping's sequence + 1 (u16 BE), 6 zero.

**0xA9 PositionUpdate** (C→S, `PlayerPositionUpdateHandler`, 1.124+ branch; 40 bytes on 1.127):

```
f32 LE x, y, z, speed, vertical speed
u16 BE session id        skipped by the server
u16 BE object id         1.127+, skipped; the recorded client sends 0
u16 BE zone id           must exist or the player is kicked to the character screen
u8     state flags       sitting 0x10, jumping 0x08, swimming 0x04, strafing 0x20/0x40/0x80
u8     unknown
u16 BE falling damage
u16 BE heading           the recorded client sometimes sets 0x1000 (L7)
u8     action flags      0x80 "torch" in the server's enum; the recorded client sends 0x80
2      unknown
u8     health %          skipped
4      zero
```

The zone id is the zone containing the position. The real client knows it from its map data; we
look it up in OpenDAoC's zone table (`src/DaocProtocol/World/zones.tsv`, from OpenDAoC-Database,
GPL): zone offsets and sizes × 8192, half-open rectangle, first match.

*Verified:* all 92 recorded 0xA9 packets rebuild byte for byte; the recorded position falls in the
zone the recorded client reported.

## Logging out

Closing the socket while "playing" makes the character link-dead: it stays in the world for 60 s
and logins in the meantime are refused (0x2C, 0x11 AccountIsInLogoutProcedure). A clean logout:

1. **0xAF CommandRequest** (C→S): `00`, the command text with `&` for `/`, `00`. Here `&quit`.
   (The 1.127 server skips one byte, then reads the text.)
2. Keep sending 0xA9 and 0xA3: the server's quit timer runs **20 s** (up to 60 s after combat;
   immediate for GM accounts or with `disable_quit_timer`). It sits the character down and stops
   only on link death or death.
3. **0xA4 Quit** (S→C, `PacketLib168.SendPlayerQuit`): `00` = back to the character screen (`01` =
   leave the game), then the character's level. The character is saved and removed.
4. **Request 0xFC for the realm.** Until then the server still treats the connection as "playing",
   and disconnecting right after 0xA4 still triggers link death (L9). After the listing the
   connection is at the character screen and closing it is a normal logout.

*Verified live:* quit after 20 s, back at the character screen, and an immediate new login accepted.

## Code map

| Topic | Code | Tests |
|---|---|---|
| Framing, checksum | `Net/ClientPacket.cs`, `Net/ServerPacket.cs`, `Net/Checksum.cs` | `CaptureFramingTests`, `ChecksumTests` |
| Login | `Packets/LoginPackets.cs` | `LoginPacketTests` |
| Character screen | `Packets/CharacterPackets.cs` | `CharacterPacketTests`, `CharacterCreateTests` |
| World, keep-alive, logout | `Packets/WorldPackets.cs`, `World/Zones.cs` | `WorldPacketTests` |
| Session flow | `Session/ClientSession.cs`, `Net/DaocConnection.cs` | `ClientSessionTests` (against a fake server replaying recorded replies) |
