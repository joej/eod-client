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

**Size limit.** OpenDAoC reads client bytes into a 1024-byte buffer (`BaseClient.TCP_RECEIVE_BUFFER_SIZE`)
and disconnects a client whose packet doesn't fit, so one client packet is at most 1024 bytes on the
wire (payload ≤ 1012). `ClientPacket.ToBytes` refuses a longer one. The largest recorded packet, the
second 0xF4, is 272 bytes.

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
C→S A9 PositionUpdate every 2 s (0.2 s moving), A3 PingRequest every 5 s (S→C 29 PingReply)
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
6    appearance: eye size, lip size, eye colour, hair colour, face, hair style (see below)
2    armour extensions (boots << 4 | gloves; torso << 4 | hood up)
u8   customisation step (1 generated, 2 finished by the player, 3 may customise)   u8 mood
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

**Appearance bytes.** Stored by the server as the client sent them at creation and sent back here and
in 0x4B PlayerCreate (whose order is eye size, lip size, **mood**, eye colour, hair colour, face, hair
style). As the retail client reads them:

| Byte | High half | Low half |
|---|---|---|
| eye size | face slider 0 (Briton: nose) | slider 1 (eyes) |
| lip size | slider 2 (lips) | slider 3 (jaw) |
| mood | mood slider: 0 = neutral (position 4), 1–4 = positions 0–3, 5–8 as they are | 0 |
| eye colour | eye colour | skin tone |
| hair colour | the whole byte: hair colour | |
| face | face | decal (tattoo) |
| hair style | hair style, **from 1** (0 = no hair) | |

Every other index counts from 0; sliders are positions 0–8 with 4 neutral. *Verified:* a Briton made
in the retail 1.130 creator with known choices (face 4, nose and eyes at the ends, lips 2, jaw 6, mood
far left, eye colour 2, darkest skin, hair 3, hair colour 6) stores `08 26 10 17 05 30 30`.

**Model.** A player's model field (here and in 0x4B) holds the model id in its low 11 bits and the body
size in bits 11–12 (1 short, 2 average, 3 tall; OpenDAoC `GamePlayer.Size`). The retail creator sets the
size: its Briton male is 45528 = model 472, average; its kobold male 53777 = model 529, average. A
character made without a size (ours) stores the bare id (32).

**Dyes and emblems.** Each worn item carries one number (the overview's colours; 0x15's colour field,
announced by model bit 0x8000 for a u16 or 0x4000 for a u8): the guild emblem when the item has one,
else its dye (`SendLivingEquipmentUpdate`, `SendCharacterOverview`). A dye is a `tints.csv` row (0–143);
an emblem is `logo << 9 | pattern << 7 | primary << 3 | secondary` (`EmblemDialogReponseHandler`; logo
1–255, pattern 0–3, primary 0–15, secondary 0–7), so any emblem with a logo is 512 or more. A logo above 127
needs a 17th bit: 0x15 sets the slot byte's 0x80 for it (cloak and left hand only); the overview's u16
drops it.

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
  requested class into its base class (Armsman → Fighter). A class id outside `eCharacterClass`
  (0-62) bans with `ban_hackers` on; our builder accepts only 1-62.
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
u16 BE heading          0–4095 = a full turn; 0 faces +Y (south), 1024 west (OpenDAoC Point2D)
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
| 0xA3 PingRequest | disconnect after **150 s** without one, in every state including the character screen (`ClientService.HARD_TIMEOUT`) | every 5 s from the character screen on |
| 0xA9 PositionUpdate | link death after **5 s** without one while playing (`PlayerMovementComponent.SOFT_LINK_DEATH_THRESHOLD`) | every 2 s in the world standing still, every 0.2 s while moving or turning |
| Idle kick | to the character screen after 60 min, only if `kick_idle_player_status` is on (off by default) | — |

These are the retail 1.127 client's rates, measured against our server: 0xA9 every 2.07–2.09 s
standing still and every 0.20–0.22 s while moving, turning, jumping or swimming (plus one on each
change of state); 0xA3 every 5.000 s. The keep-alive sends an 0xA9 only when none has gone out for
2 s, so it adds nothing while movement updates flow.

*Verified live:* 2026-11-05, 30 s standing in the world: 0xA9 every 2.00 s, 0xA3 every 5.00 s, every
ping answered, then a clean quit. Earlier, at the old rates (1 s, 10 s): 330 s in the world with 31/31 pings answered; 180 s at the character screen
without pings → disconnected at exactly 150 s; with pings → still connected.

**0xA3 PingRequest** (C→S): 4 bytes the server skips, u32 **BE** timestamp, 4 zero bytes.
**0x29 PingReply** (S→C): the timestamp, 4 zero bytes, the ping's sequence + 1 (u16 BE), 6 zero.

**0xA9 PositionUpdate** (C→S, `PlayerPositionUpdateHandler`, 1.124+ branch; 40 bytes on 1.127):

```
f32 LE x, y, z, speed, vertical speed
u16 BE session id        skipped by the server
u16 BE object id         1.127+, skipped; the recorded client sends 0
u16 BE zone id           must exist or the player is kicked to the character screen
u8     state flags       (mode << 2) | strafe: mode 1 swimming 0x04, 2 jumping 0x08, 4 sitting 0x10,
                         5 dead 0x14 (dead outranks sitting, sitting the movement mode);
                         strafe 0x20 any, 0x40 left, 0x80 right
u8     unknown
u16 BE falling damage     bit 15 = landed (see below); the recorded client sends 0
u16 BE heading           0-4095, plus 0x1000 while on the ground (L7)
u8     action flags      0x80 "torch" in the server's enum (the recorded client sends it); 0x02 underwater
2      unknown
u8     health %          skipped by the server; 0x80 = attack mode
4      zero
```

**State flag pairs the server bans for.** OpenDAoC (`PlayerPositionUpdateHandler.ProcessStateFlags`)
reads two of sitting, jumping and swimming together as a forged state: 0x18 riding (while not mounted)
and 0x0C flying (without permission) ban the account and its IP when `ban_hackers` is on, with no GM
exemption; 0x14 (playing dead while alive) is stripped. All three (0x1C) is climbing and allowed. With
`ban_hackers` off (the default) the bits are stripped instead. `ClientSession.MoveToAsync` refuses
these pairs, so a jump while swimming or sitting can't send both bits.

**Speed.** The server sends the player's allowed run speed in 0xB6 MaxSpeed (`PacketLib168.SendUpdateMaxSpeed`):

```
u16 BE  speed %           of the base run, 191 region units/s (GamePlayer.PLAYER_BASE_SPEED)
u8      turning disabled  1 while stunned or mezzed
u8      water speed       the WaterSpeed property
```

It comes on world entry and whenever buffs, encumbrance or a mount change it (the recording: 125 at
entry, 170 later). The server never trusts 0xA9's speed field; it measures x/y distance between
updates (`PlayerMovementMonitor`) against 1.15 × this speed (the old speed for 750 ms after a
decrease). Six violations within 1.5 s move the player back to where they began; the sixth such move
in a row disconnects (no ban). GMs and players allowed to fly are exempt; Z isn't checked. Our client
runs at the 0xB6 speed; its Shift sprint (×4) is a dev toggle, off by default, for GM accounts.

**Sprint** is the server's: `/sprint` (retail's V key) toggles ×1.3 speed while endurance lasts, and every
change, including the server ending it when endurance runs out, arrives as a new 0xB6.

**Untargetable NPCs.** 0xDA's second flags byte (`PacketLib1124.SendNPCCreate`) has 0x01 can't target, 0x02
name hidden, 0x04 stealthed, 0x08/0x10 quest available/finishable, 0x80 pet. A GM account gets none of 0x01
and 0x02: the server appends "-DOR" and "-NON" to the name instead (OpenDAoC's teleporter indicator, model
1923, reaches a GM as "[]-DOR-NON"). Model 1923's client figure is "Corpse Light Node"; the recording has two
of these indicators and a swarm of flies flagged 0x03 among its 111 NPCs.

**Quest glyphs.** The knot over a quest giver's head comes from 0xDA (`PacketLib1124.SendNPCCreate`): flags2
0x08 available, 0x10 finish; flags3 (the byte after flags2) 0x01 lesson, 0x02 lore, 0x20 pending. It changes
later through **0x4C VisualEffect** (`PacketLib173.SendNPCsQuestEffect`): object id u16 BE, kind u8 = 7, then
the indicator u8 (OpenDAoC's eQuestIndicator: 0x01 available, 0x02 finish, 0x04 lesson, 0x08 lore, 0x10 pending,
0 none) and a u32 0. The recorded session's 0x4C packets are other kinds (5, 9). The client's glyphs are effect
files: moneffects.csv's "Quest Completed/Pending Indicator", "Lore/Lesson Quest Indicator" rows, and
`effects/quest_knot.nif` (gold) for a quest to take; retail shows a gold knot for one to take and a cyan one to
hand in.

**Hidden NPCs.** OpenDAoC hides a creature without removing it (`HideableNpc`: night spawns by day, day
spawns by night) by sending its 0xDA again as **model 1**, can't-target, name hidden and peaceful; it sends
the real model when the creature shows. Model 1 is the client table's "Demon", so a client that draws it
puts demons where nothing should be (Camelot Hills' zombie boars by day). Our client draws nothing for model 1
sent untargetable. What retail 1.127 draws for it is open question W44.

The zone id is the zone containing the position. The real client knows it from its map data; we
look it up in OpenDAoC's zone table (`src/DaocProtocol/World/zones.tsv`, from OpenDAoC-Database,
GPL): zone offsets and sizes × 8192, half-open rectangle, first match. Walking over a zone border
needs no packet from the server: the next 0xA9 simply names the new zone, and OpenDAoC answers the
change with "You have entered …" and the zone's areas (`PlayerPositionUpdateHandler`). In a gap
between zones (no rectangle contains the position) we keep reporting the last zone, which the
server knows; an unknown zone id kicks the player.

*Verified:* all 92 recorded 0xA9 packets rebuild byte for byte; the recorded position falls in the
zone the recorded client reported.

**Falling.** While in the air the client reports its vertical speed (region units per second, falling
negative); the recorded client sends small negative values walking downhill (−17 to −50) and 0 standing.
On the one update that lands from a fall it sets bit 15 of the falling-damage field, and OpenDAoC then
charges fall damage from that update's vertical speed: none up to 500 units/s, otherwise
`min(99, (speed − 501) / 15)` percent, with Safe Fall raising the threshold by 100 per level
(`PlayerPositionUpdateHandler`). The recorded client never sets the bit while standing. GMs take none.

## Being moved by the server

The server moves a player with `GamePlayer.MoveTo` (the GM command `/jump`, teleports, recall).
The client must take the new position over at once: the server believes every 0xA9, so a client
that goes on reporting its old position moves itself straight back. Our client holds 0xA9 back
(pings go on) from sending a `/jump` until the new position is known.

**Within the region: 0x04 CharacterJump** (S→C, `PacketLib168.SendPlayerJump`, all big-endian):

```
u32 BE x, y              region-global, integers (0x20 uses floats)
u16 BE object id
u16 BE z
u16 BE heading
u16 BE house number      0 outside houses
```

x, y and z are all 0 when only the heading changed.

**To another region: 0xB1 + 0xB7 RegionChanged** (S→C, `PacketLib174.SendRegionChanged`). The
server has already taken the player out of the world. 0xB7: u16 BE region skin, u16 BE zone skin,
then `00 00 00 01 0C 00 FF BF`. The client enters the world again from 0xD4: **0xD4** → burst with
**0x20** … **0x4E**, then **0xE8** and **0xBF** → **0x2D**, as at login. The 0xD4 handler accepts
this while "playing" because the player object is inactive; it sets the connection to "world
enter", and only 0xBF sets it back (clearing the server's cache of what we've been sent, so the
new region's objects arrive). No realm check applies to the move (`IsAllowedToZone` is `true` in
every rule set): an Albion character can stand in Hibernia.

**`/jump to X Y Z REGION`** (GM and above; `GMCommands/jump.cs`): X, Y, Z integers; the region's
expansion must not exceed the client's.

*Verified live (2026-11-03):* Camelot Hills → Salisbury Plains (same region) answered by 0x04;
Salisbury Plains → Cursed Forest (region 200) by B1 B7, then our D4 → … 4E, E8, BF → 2D, with the
new region's NPCs arriving; Cursed Forest → Camelot Hills through the Godot client, which loaded each
zone's converted assets by the zone the server reported.

## Jump points

A zone's doorways (city gates, dungeon entrances, portals) are lines in the client's own zone data
(`zonejump.csv`: x1, y1, x2, y2 zone-local, floor and top, and a zone point id). The client notices
when the player walks through one and names its zone point; the server moves the player:

```
C→S 0x90 PlayerRegionChangeRequest   u16 LE zone point id
```

OpenDAoC looks the id up in its zonepoint table (for a player: their realm's or a realm-less row) and
does not check that the player is near the doorway. The move then comes as any other (0x04 within the
region, 0xB1 + 0xB7 to another). *Verified live 2026-11-07 (owner):* Camelot Hills' gate (zone point 6) into
Camelot City. Which way a one-way doorway fires is open question W47.

## Target and interact

```
C→S 0xB0 PlayerTarget        u16 BE object id, u16 BE flags (retail: e0 00 = examine | in view); clear: 00 00 00 00
C→S 0x7A ObjectInteract      u32 BE player x, u32 BE player y (whole units), u16 BE session, u16 BE object id
```

The server answers a target with chat type 0x1E "You target [X]." and "You examine X. It is friendly
towards you." An interact (a right-click; retail targets first) needs the player within 192 units
(OpenDAoC `INTERACT_DISTANCE`), else "X is too far away!". An NPC's words to the player come as chat
type 0x12 with "##" before the text (the pop-up window); a [keyword] in them is answered by whispering
it: C→S 0xAF `00 "&whis keyword"`. Retail also puts the target in every 0xA9 (offset 22) and sets
action flags 0x80, plus 0x30 while a target is in view. *Verified live 2026-11-07* with Master Visur at
Prydwen Keep (eodtest2): target, examine, his destinations, and [towns] answered with the town list.

## Swimming

The client sends 0xA9 with state flag 0x04 while it swims; other clients see it in the relayed position.
OpenDAoC 2026.09.36 stores the flags for 1.127 clients and doesn't otherwise act on them: its swimming
property, which skips fall damage, is set only by the pre-1.124 handler. So a client that falls into deep
water must not set the landing bit, or the server charges the fall. 0xB6's third byte is the player's
water speed bonus. *Verified live 2026-11-07* (eodtest2: swam in the Camelot Hills river, no complaint from
the server).

## Death and release

```
S→C 0xAE PlayerDeath         u16 BE victim, u16 BE killer (0: none, e.g. a fall), 4 zero bytes
S→C 0xF3 TimerWindow         u16 BE seconds, u8 title length, u8 1, title ("Release Timer", 900 s);
                             close: 00 00 00 00
S→C 0xAD CharacterStatus     health %, mana %, sitting (2), endurance %, concentration %, 0, then u16 BE
                             max mana, max endurance, max concentration, max health, health, endurance,
                             mana, concentration
C→S 0xAF "&release"          (as any command: 00 + text + 00)
S→C 0x89 PlayerRevive        u16 BE object id, 2 zero bytes
```

On death the server sends the kill messages (chat 0x16 "X was just killed!", 0x15 "You have died. Type
/release …"), 0xF3, 0xAE and 0xAD with health 0. While dead the client sends each 0xA9 with state flags
0x14 (sitting + swimming: OpenDAoC's DEAD, accepted only at 0 health), speed 0 and health 0, and doesn't
move; retail also clears its target (0xB0 with 0). After "&release" the server counts down a few seconds,
then closes the timer, says "Your surroundings suddenly change!", moves the player to the bind point (0x04,
or 0xB1 + 0xB7 for another region) and sends 0x89 and an 0xAD at full health. OpenDAoC charges fall
damage of at most 99% of maximum health per landing, so a second big fall kills. *Verified live
2026-11-07* (eodtest2/Quitcheck at Prydwen Keep: two 140 m drops, died, released to the bind point,
revived).

## The character's points, money and identity

```
S→C 0x91 CharacterPoints     u32 BE realm points, u16 BE level progress ‰, u16 BE spec points,
                             u32 BE bounty points, u16 BE realm spec points, u16 BE champion progress ‰,
                             then u64 LITTLE-endian: experience, experience for the next level,
                             champion experience, its next level (OpenDAoC: 0, 0). 48 bytes.
S→C 0xFA MoneyUpdate         u8 copper, u8 silver, u16 BE gold, u16 BE mithril, u16 BE platinum
                             (100 c = 1 s, 100 s = 1 g, 1000 g = 1 p, 1000 p = 1 m)
S→C 0x16 VariousUpdate, 3    head 03 0F 00 00, then fifteen pairs of a byte and a u8-length string:
                             level + name; max health high byte + class; max health low byte + profession;
                             0 + level title; realm level + realm title; realm spec points + base class;
                             house number high byte + guild; house low byte + last name; master level + 1
                             + race; 0 + guild rank; 0 + crafting skill; 0 + crafting title; 0 + master
                             level title; 0 + custom title; champion level + 1 (0 if none) + champion title
```

Realm level 40 is rank 5L0 (rank = level / 10 + 1, the remainder after the L). OpenDAoC writes "None" for a
string the client should hide.

The other 0x16 subcodes at world entry (all four head bytes: subcode, entry count, subtype, a fourth):

```
0x16, 1  usable skills, paged    head 01, count, subtype (99: more pages follow, 3: last), first index;
                                 each: u8 level, u16 BE id, u8 page (0 spec, 1 ability, 2 style, 3 spell,
                                 4 song, 5 ability-spell, 6 realm ability), u16 BE special, u8 bonus,
                                 u16 BE icon, u8-length name. A page starting at index 0 restarts the list.
0x16, 2  list-caster spell lines head 02, entries + 1, 2, line number; the line (u16 LE 0, u16 BE id, u16 BE 0,
                                 name), then each entry: u16 LITTLE-endian level word (low byte the level,
                                 high byte 1 an ability, 2 a style, 0 a spell), u16 BE id, u16 BE icon, name.
                                 Footer: 02 00 63 00 (subtype 99) ends the lines.
0x16, 5  weapon and armour       05 06 00 00, then weapon damage × 100, weapon skill and armour factor, each
                                 as high byte, 0, low byte, 0 (1.126+)
0x16, 8  crafting skills         08, count, 03, 00; each: u16 BE points, u8 icon, u32 BE 1, name
```

"Special" is a spell's specialisation (its index in this list; 0xFF a song, 0xFE none) or a style's opening
requirement; "bonus" a specialisation's added levels or a style's specialisation. The server sends the
skill list again whenever a skill changes. *Checked against the RustDAoC 1.127e capture (a level 50 Paladin: every field
decodes and rebuilds byte for byte) and live 2026-11-09 (eodtest2/Quitcheck at world entry: one 0x16/3,
one 0xFA, three 0x91, XP 0 of 50 at level 1; the skill list twice: 3 specialisations and 7 abilities,
no spell lines, 14 crafting skills). A list caster's spell lines are so far checked only against bytes built
in OpenDAoC's layout.*

Stats and resists share one packet, told apart by the byte at offset 72:

```
S→C 0xFB StatsUpdate (78)    five columns of nine places: base (u16 BE), buffs and debuffs (signed u16 BE),
                             item bonuses (u16 BE), item cap (u8), realm-ability bonus (u8); then u8 kind
                             (00 stats, FF resists), u8 constitution lost to deaths, u16 BE max health
                             (stats only), u16 0.
  stats order                strength, dexterity, constitution, quickness, intelligence, piety, empathy,
                             charisma (ninth place 0)
  resists order              crush, slash, thrust, heat, cold, matter, body, spirit, energy (base = racial)
```

The character has base + buffs + min(items, cap) + realm abilities: the server writes the buff column as what is
left once those are taken off its own total. *Checked against the RustDAoC 1.127e capture (both kinds decode and
rebuild byte for byte; a Briton's racial resists) and live 2026-11-11 (eodtest2/Quitcheck and Spellcheck).*

## Inventory

```
S→C 0x02 InventoryUpdate     count, 0, visibility (0x01 cloak hidden, 0x02 helm hidden), hood (bit 0) | active
                             quiver (bits 4–7), visible weapon slots, window (0 update, 1 all equipment, 2 the rest,
                             3+ a vault, merchant or other window), then count × (u8 slot, item)
  item (1.124+)              u16 0, level, value 1, value 2, hand << 6, damage type << 6 | object type, 0,
                             u16 BE weight (tenths of a pound), condition %, durability %, quality %, bonus %,
                             bonus level, u16 BE model (objects.csv row), extension, u16 BE colour or emblem,
                             flags (0x02 salvage, 0x04 craft, 0x08 / 0x10: a spell follows, u16 BE icon + u8-length
                             name), u16 BE effect, u8-length name. An empty slot: the same 24 bytes, all 0.
  values by object type      weapon: DPS × 10, speed × 10; armour: AF, absorption %; shield: size, DPS × 10;
                             arrows, bolts, poison: count, …; generic items: count (low, high byte)
S→C 0xBD Encumbrance         u16 BE can carry, u16 BE carried (pounds)
C→S 0xDD PlayerMoveItem      u16 0, u16 BE to-slot, u16 BE from-slot, u16 BE count (0: the whole stack)
```

Slots: 10–13 right hand, left hand, two-handed, ranged; 21–37 the armour and jewellery (21 helm, 22 hands, 23 feet,
24 jewellery, 25 torso, 26 cloak, 27 legs, 28 arms, 29 neck, 32 waist, 33/34 wrists, 35/36 rings, 37 mythical);
40–79 the backpack. A move to slot 100 lets the server choose where the item is worn. The server answers a move
with 0x02 for both slots (or a chat line saying why not); moving an item out of a weapon slot sheathes it, into one
wields it. *Checked against the RustDAoC 1.127e capture (both 0x02 packets, a cloak with two spells, and 0xBD decode
and rebuild byte for byte) and live 2026-11-11 (Quitcheck: sword 10 → 41 → 10, helm 21 → 42 → 100 → back on).*

### An item's details

The client's small item window (selected with a right-click) is filled from the 0x02 item data it already holds and
sends nothing. Its INFO, REPAIR, CRAFT and SALVAGE buttons, and its use entries, do:

```
C→S 0xD8 DetailRequest       u16 BE type, 2 bytes (not read), u32 BE id, u32 BE extra (12 bytes). Types: 1 an
                             inventory item (id = slot), 100 repair, 101 self-craft, 102 salvage (id = slot).
                             OpenDAoC reads u16 type, u32, u16 id: the id's low 16 bits.
S→C 0xC4 DetailWindow        u8 mode (0 a description, 1 statistics and titles), u8 more (not 0: the next 0xC4
                             continues this window), u8-length caption, then (u8 line number, u8-length text)
                             pairs, then 0. The lines are shown in order in a scrolling window titled with the caption.
C→S 0x71 UseSlot type 1 / 2  use the item's first or second use spell (type 0: a plain use)
```

Salvage and self-craft only apply to backpack items (40–79); the client refuses others itself. *The request's layout
matches the one 0xD8 in the RustDAoC 1.127e capture (a spell delve, type 24); live 2026-11-11, INFO on Quitcheck's
practice sword (slot 10) brought a 13-line 0xC4 titled "practice sword".*

## Using skills and spells

```
C→S 0xBB UseSkill   x, y, z, speed (f32 LE), u16 BE heading (no on-ground bit), u16 BE flags,
                    u8 index, u8 type, 2 zero bytes. Type 0: a specialisation, index into the whole
                    usable-skill list; otherwise the index counts from the first entry that isn't a
                    specialisation (we send the entry's page as the type; the server only tests > 0).
C→S 0x7D UseSpell   the same, with u8 spell level, u8 spell-line number (list casters' lines, 0x16/2);
                    the server picks the line's first entry of that level.
C→S 0x71 UseSlot    the same, with u8 slot, u8 type (retail: 0).
S→C 0xD6 Disable    u16 BE seconds, u8 count, u8 kind. Kind 1 (skills): count × (u16 BE index from the
                    first non-specialisation, u16 BE seconds). Kind 2 (list spells, one packet per
                    duration): count × (u8 line, u8 entry index). 0 seconds: usable again.
```

Flags as OpenDAoC reads them: 0x4000 strafing, 0x2000 or 0x8000 a target in view, 0x1000 a ground target in
view. Retail 1.127's UseSlot is 24 bytes (the two trailing zeros included) with flags 0 when it has no target;
what it sets with a target is open (C12). We send 0x2000 whenever a target is selected. *Verified live
2026-11-09 (eodtest2/Spellcheck, a level 20 Wizard): 0x7D cast Cobalt Shield (Enhanced) ("You begin casting
…", "A crystal shield covers you"), 0xBB used Sprint (index 0, type 1: "You prepare to sprint!"); at world
entry the server sends 0xD6 with 0 seconds for every skill and list spell, both kinds.*

## Attack mode

**C→S 0x74** (`PlayerAttackRequestHandler`): `u8 start` (1 attack, 0 stop), `u8 asked` (0 when the
player pressed the key, 1 when the client itself stops). The server starts only an attack the player
asked for; with a ranged weapon readied it refuses melee mode.

**S→C 0x74 AttackMode** (`PacketLib168.SendAttackMode`): `u8 on`, then 3 zero bytes. Sent when the
server's attack state changes: in answer to the request, and when it ends attack mode itself. No target
is needed ("You enter combat mode but have no target!"). The 1.127e recording has two, both off.

**Other players** carry it in their position and heading updates: bit 0x80 of the health byte (the low
seven bits are the health percent). Both are "UDP" packets that OpenDAoC sends over TCP to a client
that hasn't confirmed UDP (our 0xBF sends 0).

S→C **0xA9 PlayerPosition** (1.127, `PlayerPositionUpdateHandler.CreateOutPak1127`), 40 bytes:

```
f32 LE x, y, z           region-global
f32 LE speed, fall speed
u16 BE session id, object id, zone id
u8 state flags, u8 0, u16 BE steed seat
u16 BE heading           low 12 bits
u8 action flags, u8 roleplay flag, u8 0
u8 health                0x80 = attack mode
u8 mana %, u8 endurance %, u16 0
```

S→C **0xBA PlayerHeading** (1.127, `PlayerHeadingUpdateHandler.CreateOutPak1127`), keyed by session id
(the player's 0x4B gives both ids): `u16 BE session`, `u16 BE target` (0), `u16 BE heading`,
`u8 steed seat`, `u8 action flags`, `u8 0`, `u8 state flags`, `u8 health`, `u8 mana %`,
`u8 endurance %`, `u8 0`.

*Verified live (2026-11-05):* our own 0x74 on and off. Other players' bit is from the server's writer,
not yet seen with a second player.

## Logging out

Closing the socket while "playing" makes the character link-dead: it stays in the world for 60 s
and logins in the meantime are refused (0x2C, 0x11 AccountIsInLogoutProcedure). A clean logout:

1. **0xAF CommandRequest** (C→S): `00`, the command text with `&` for `/`, `00`. Here `&quit`.
   (The 1.127 server skips one byte, then reads the text.)
2. Keep sending 0xA9 and 0xA3: the server's quit timer runs **20 s** (up to 60 s after combat;
   immediate for GM accounts or with `disable_quit_timer`, so a GM test account never sees the
   timer or the sitting below; 0xA4 follows the `&quit` at once). It sits the character down
   (0xAD status, sitting byte `02`; chat "You will quit after sitting for 20 seconds", then a
   countdown every 5 s) and also sends the `/stats` summary as a chat line. It refuses to start
   while the last 0xA9 had a speed ("You must be standing still to quit."), and stops on death,
   link death, crafting, or when the character stands up. **Moving stands the character up:** the
   first 0xA9 with a new X/Y (`PlayerMovementComponent.UpdatePosition` → `OnPlayerMove`) gets
   "You stand up." and "You are no longer waiting to quit.", so a client only has to stop waiting.
   A turn or a jump in place (Z only) doesn't. `/stand` or **0xC7 PlayerSitRequest** `00` also
   stands up; sent after moving it only earns "You are not sitting!".
3. **0xA4 Quit** (S→C, `PacketLib168.SendPlayerQuit`): `00` = back to the character screen (`01` =
   leave the game), then the character's level. The character is saved and removed.
4. **Request 0xFC for the realm.** Until then the server still treats the connection as "playing",
   and disconnecting right after 0xA4 still triggers link death (L9). After the listing the
   connection is at the character screen and closing it is a normal logout.

*Verified live* (2026-11-05, player account, `DaocCli --enter 0 --quit-checks`): quit after 20.1 s
and back at the character screen; quit then moving after 5 s calls it off (no 0xA4 in the next 30 s);
a second quit after that works; a quit sent while running is refused at once. An immediate new login
after a quit is accepted.

## Code map

| Topic | Code | Tests |
|---|---|---|
| Framing, checksum | `Net/ClientPacket.cs`, `Net/ServerPacket.cs`, `Net/Checksum.cs` | `CaptureFramingTests`, `ChecksumTests` |
| Login | `Packets/LoginPackets.cs` | `LoginPacketTests` |
| Character screen | `Packets/CharacterPackets.cs` | `CharacterPacketTests`, `CharacterCreateTests` |
| World, keep-alive, server moves, logout | `Packets/WorldPackets.cs`, `World/Zones.cs` | `WorldPacketTests` |
| Attack mode, other players' moves | `Packets/CombatPackets.cs`, `Packets/WorldEntityPackets.cs` | `CombatPacketTests` |
| Session flow | `Session/ClientSession.cs`, `Net/DaocConnection.cs` | `ClientSessionTests` (against a fake server replaying recorded replies) |
