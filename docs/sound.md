# Sound: what plays when

How the retail 1.1xx client chooses and plays its sounds from the install's tables, as a reference for
roadmap M12. Source: daoc-re, 2026-11-08, from the client's behaviour; **not yet confirmed by ear** (one
combat and one spellcast with audio will settle it; open question W52). Column numbers are 0-based,
counted after the row's name column.

## The tables

The client uses the first table set whose `<prefix>_sounds.csv` exists, trying `ToA_`, `CC_` and `Imp_`
in that order. The OpenDAoC install has only `CC_`.

| File | Holds |
|---|---|
| `sounds/cc_sounds.csv` | the master list (4,439 rows): id, sound name, file, and how to play it (below) |
| `cc_groups.csv` | `g_` names: a group, one member picked at random |
| `cc_queues.csv` | `q_` names: a sequence with delays |
| `cc_loops.csv` | `l_` names: loops |
| `cc_mp3s.csv`, `cc_music.csv` | `mp3_` names and the music rows the server selects |
| `cc_creatures.csv` | per creature sound set: attack vocal, pain light/medium/heavy, death vocal, … |
| `cc_weapons.csv` | per weapon sound set: use, style use, hits light/medium, style hit, parried, blocked |
| `cc_spells.csv` | per spell sound set: gather, shape, cast, travel, hit, miss, resisted, fizzle |
| `cc_doors.csv` | open, close |
| `CC_events.csv` | per realm: raise level, die, money, inventory, experience, friend enters/exits |
| `cc_interface.csv` | clicks and rollovers |
| `gamedata.mpk animsound.csv` | (anim set, anim number) → sound id 1–25 |
| `zones/zoneNNN/sounds.dat` | the zone's ambient sound regions (389 zones) |
| `zones.mpk zones.dat` | each region's entry music |

### cc_sounds.csv columns

- id, sound name, file name;
- rate: a range; the pitch is chosen at random within it;
- vol: a range;
- pan: a left-to-right range, e.g. `.9L-.9R`;
- offset: a range in ms;
- r1, r2: floats, where `*` means −1 (defaults 0 and 1000). Presumably the 3D minimum and maximum
  distance in world units (a wall collapse uses 4500);
- columns 10, 11, 13 and 14 are not understood yet;
- polygroup: the row's voice group.
- `*` or an empty field takes the value from the `#,0,s_default` row.
- `POLYGROUPS` rows give name, limit and cutoff. The limit is presumably the most voices that play at
  once (`TutorialVO` = 1); the cutoff is not understood yet.
- The row doesn't say 2D or 3D: whatever triggers the sound decides.

## Which row

| Thing | Row |
|---|---|
| Creature | its model → `monsters.csv` column 15 "Sound Set" → `cc_creatures` row |
| Weapon | the weapon model (0xBC's weapon field) → `objects.csv` column 21 "Sound Set" → `cc_weapons` row. Unarmed: the attacker's creature row, column 14 "unarmed set" (19 in every row). `objects.csv` column 19 "Combat Stance" values 5, 6 and 8 have no attack sound |
| Spell | the spell id in 0x72 and the client effect id in 0x1B → `spells.csv` column 14 "Sound Set" → `cc_spells` row (shields: set 9, "Cleric Shields") |
| Event | the player's realm (1 Albion, 2 Midgard, 3 Hibernia); level-up takes its row from the packet |
| Door | 0x99 byte 5, the door's flag (OpenDAoC sends 1, door1, for doors without a flag) |
| Music | 0xEF type 2's value byte |

## Triggers

**0xBC combat.** The result byte decides:
- **At the attacker, when the attack starts:** without a weapon, creatures column 0 "attack vocal"; with
  one, weapons column 5 "use", or column 6 "style use" for result 0x0B (a style).
- **At the defender, on a defence:**
  - result 1 (parry): weapons column 16 "parried";
  - result 2 (block): weapons column 17 "blocked";
  - result 3 (evade): no combat sound; only the evade animation's own sound (animsound).
- **At the defender, on a hit** (result 10 or more):
  - weapons column 9 "hits body light" when the damage is under 10%, column 10 "medium" otherwise, and
    column 11 "style hits body" for 0x0B;
  - plus the defender's creature pain: column 1, 2 or 3 (light, medium, or heavy for a style hit).
  - The damage is the defender's previous health % minus 0xBC's health byte.
- **Timing:** an impact waits for the attack animation's hit time (from `animnifs.csv`; 400 ms when
  unknown). Parry and block subtract the defender's reaction lead.

**0x72 SpellCastAnimation** (at the caster):
- With no cast time, column 2 "cast" plays at once.
- Otherwise, column 0 "gather" plays at once.
- Column 1 "shape" plays 1000 ms later, if the effect has a second animation and the cast is longer
  than 1500 ms.
- Column 2 "cast" plays 500 ms before the cast ends, if the effect has a third animation and the cast
  is longer than 500 ms.

**0x1B SpellEffectAnimation:**
- An effect with a bolt plays column 2 "cast" at the caster.
- So does a spell with Remove letters (it holds its last cast animation and "cast" sound until it lands).
- Unless byte 8 (no sound) is set: column 4 "hit" for results 1, 2 and 3, column 5 "miss" for 0 and 4,
  at the target at once (not after the bolt's flight).
- 0x73 (interrupt) stops the caster's "shape" and "cast" sounds, playing or still to come.
- Built in `WorldView.SpellSounds`.
- Columns 3 (travel), 6 (resisted) and 7 (fizzle) are never played.

**Other packets:**

| Packet | Sound |
|---|---|
| 0xEF RegionSound (u16 player id, u8 type, u8 value) | type 1: events column 0 "Raise Level", row = value (the realm); type 2: `cc_music` row = value, the music for the region or area the player has entered (the server sends it on entry) |
| 0x91 CharacterPointsUpdate | events column 4 "Get Experience" |
| 0xC6 friend online/offline | events columns 5 and 6 (both empty in the shipped file) |
| 0xAE PlayerDeath, 0xA2 RemoveObject (type 0) | creatures column 4 "death vocal"; for your own death 2D, and combat music stops |
| 0x4E ControlledHorse | creatures row 41 (horse): column 23 on mounting, 24 on dismounting |
| 0x99 DoorState | doors column 0 "open" (state 1) or column 1 "close" (state 0), at the door |
| 0xC9 SoundEffect (id, zone, x, y, z, radius) | a `cc_sounds` id: 3D at the zone-local position when radius ≠ 0 (the value is otherwise unused), 2D when 0 |
| 0xD3 PlaySound (u16 type, u16 id) | type 1, crafting: id 1 → sound 924 (fumble), 2 → 923 (failure), 3 → 926 (success), 4 → 925 (masterpiece); type 2: a `cc_sounds` id, 2D; type 3: 3D with zone and position (field order unknown) |
| 0x5A (OpenDAoC never sends it) | a u16 `cc_sounds` id, 2D, not restarted while it is still playing |
| Emotes | the emote's animation sound (animsound) |

**animsound:**
- When a figure starts an animation, its (anim set, anim number) gives a sound id 1–25. The sound is
  column (id − 1) of the figure's creature row.
- There is no frame offset: the sound starts with the animation.
- A rider uses the mount's animation.

**Water** (your own character):

| When | Sound (`cc_sounds` id) |
|---|---|
| entering water | 19 `s_wstep` |
| leaving it | 221 `s_outofwater` |
| surfacing from underwater | creatures column 8 "move in water" |
| wading steps | random 994–997 |
| swim strokes | random 1002–1005; 985–988 underwater |

## Music

- **Title and character select:** `mp3_Title`.
- **Entering the world:** the region's `entry_music` in `zones.dat` (default `mp3_Enter_Albion`).
  - `proxy_region` uses another region's entry music.
  - `dr_entry_music` overrides it when Darkness Rising is installed.
- **Area music:** 0xEF type 2 (above).
- **Combat music:**
  - 0x74 AttackMode on plays `mp3_Combat1` or `mp3_Combat2` at random. It doesn't start in region 27
    (the tutorial) or when combat music is already playing.
  - Attack mode off, or your own death, fades it out.
- No timer stops entry music from repeating; `cc_music.csv`'s comment only wishes for one.

## Zone ambience: sounds.dat

Each zone's file is reloaded on every zone change. It comes from `zones/` (or `tutorial/`,
`frontiers/`, `phousing/`) `zoneNNN/sounds.dat`. The regions that play are chosen by the listener's
position and the game time.

**Region keys** (defaults):

| Key | Default |
|---|---|
| `random_sound_spacing` | 5 |
| `fade_time` | 3 |
| `fade_on_mp3` | 0 |
| `fade_on_mp3_time` | 1 |
| `instant_start` | 0 |
| `fade_on_weather_time` | 1 |
| `fade_on_underwater_time` | 1 |
| `zone_wide_sounds` | 0 |
| `ignore_random_offset` | 0 |
| `sound_type` | 1 |
| `version` | 1 |

Optional reverb keys: `enable_reverb`, `reverb_time`, `reverb_pre_delay`, `reverb_damping`,
`global_sound_wet_level`, `global_sound_dry_level`.

**Each `SoundNN` (up to 128) gives** (the file's own header: `Type, Sound, Period, Chance, Volume, Start Time,
End Time, Weather, Environment Type`):
- kind: 1 a one-shot or group, 2 a looping bed;
- the sound, by name or number (a cc_sounds id);
- period: seconds, a single value or a min-max range;
- chance, in % (not always 100: zone 0's wind lines use 60–90);
- volume, 0–100;
- start and end time, HHMM (0,0 = always; a range may wrap past midnight);
- a condition: 0 always, 1 clear, 2 rain, 3 fog (presumably the weather);
- with `version=2`, one more int: 1 normal, 2 underwater.

**Shapes**, in zone-local units:
- `ShapeNN`: `0,x1,y1,x2,y2` is a rectangle, `1,x,y,r` a circle.
- `xShapeNN` has the same format and presumably excludes an area.

## The tutorial's voice

- The 67 voice lines are ordinary `cc_sounds` rows, ids 2321–2406. Lines 001–004, 034, 035 and 053
  also have a version per realm.
- They are all in polygroup `TutorialVO` (limit 1), so one line plays at a time.
- The server plays each line by id (0xD3 type 2, or 0xC9) and picks the realm version; the client
  doesn't start them by area or by NPC.
- The files are found through the `tutorial/sounds/` fallback, and a `zones.dat` `tutorial` key makes
  those zones load from `tutorial/zones/`.
- How `Music_Tutorial{Albion,Hibernia,Midgard}.wav` start is unknown (presumably a sounds.dat loop or
  the server).

## Newtowns

- If `newtowns/zones/nifs` exists, every zone object model is looked for there first, by the same file
  name, then in the normal place.
- The seven `.npk` models are drop-in replacements: border keeps, the Camelot wall, a teleporter and a
  demon portal. All but `camwall_p00` also exist in `zones/Nifs/`.
- The client tells the server it has them: bit 0x10 of the configuration block in its first 0x9D
  RegionListRequest. OpenDAoC ignores that bit.
- For us: the converter should prefer `Newtowns/zones/Nifs` over `zones/Nifs` for zone objects when the
  folder exists, as the client does.

## In our client (2026-11-21)

`make sounds` (`mpk sounds`) copies `sounds/`, `Tutorial/sounds`, `frontiers/sounds`, every zone's `sounds.dat` and
`zones.dat` into `library/sounds`, then checks them (`soundcheck.md` there). The client decodes the wavs itself
(`DaocAssets.Sound.WavFile`: PCM and the 593 IMA ADPCM files) and plays the mp3s as they are. `DaocAssets.Sound` reads
the tables (`SoundTables`), resolves a name to the files it plays (`SoundPlanner`), reads `sounds.dat` (`ZoneSounds`)
and runs a zone's ambience (`Ambience`); `godot/scripts/GameAudio.cs` plays them and `WorldView.Sound.cs` connects the
packets. What is built and what is ours is in open question W52.

How retail's client does it (daoc-re, 2026-11-21, from its code and data; nothing heard yet), and where we follow:
- **Footsteps:** only your own figure (and your mount) makes them, in 2D, from the clip: one as the walk or run clip
  passes its middle and one as it wraps. Wading plays a water step on both; swimming a stroke on the wrap only. Ours
  takes the clip's time instead of its frame count, and a figure with no clips makes none.
- **Distance:** full volume within r1, linear to silence at r2, nothing beyond. r1 and r2 belong to the file: the first
  `cc_sounds` row naming it sets them (ours: the first in file order). A 3D sound pans by its angle from the listener's
  heading; ours uses Godot's panning.
- **Polygroups:** only cutoff 2 limits: a sound that takes its group past the limit (at most 10) stops the oldest.
  Cutoff-1 groups never limit.
- **sounds.dat:** volume is 0–127; weather 3 is snow (rain and snow come from the zone's temperature, not 0x92's fog);
  environment 0 plays above and under water; start time 0 means always. A random line takes a turn each period; a
  turn is skipped while its last sound still plays or within the region's spacing of any of its random sounds, then
  the chance is rolled. The first turn is due at once with `instant_start=1`, else after 1 to period seconds. Beds start
  at 1/128 and fade in and out over the region's fade time. A line whose sound doesn't resolve stays as a silent slot:
  it still takes turns and still resets the spacing.
- **Entry music:** only `[regionNNN]` entry_music (through proxy_region; default `mp3_Enter_Albion`); the `[zoneNNN]`
  values are never played.
- **Volumes:** four, as retail's options: music, sound, ambient music, ambient sound (sounds.dat `sound_type` 2 and 1).
  A channel at 0 starts nothing. Retail's defaults aren't known; ours are 70 %, 80 %, 70 %, 80 %.
- **Queues:** the interval runs from start to start; −1 waits for the end; `s_silenceN` waits N ms in place of it.
- **Names:** exact, case-sensitive, and only in the name tables (never by file name). Member lists have their whitespace
  removed; `*NN` replaces the last len(NN) characters of the previous member; a member that doesn't resolve is left out.
  `s_null` is built in.

## Classic and current zone sounds (2026-11-21)

Two installs write the zones' `sounds.dat` for different tables:
- **Classic.** An early client (before the `cc_*` tables; here a 2004 install) numbers its sounds in
  `sounds/sndfiles.csv` (id, file, two distances). The OpenDAoC install's `sounds.dat` files are of this kind. Today's
  `cc_sounds.csv` dropped or renumbered some of those numbers: 526–533 distant horses, 574–579 the capitals' distant
  music, 120 and 176 brook and shore. So with today's tables alone those lines are silent, in retail 1.127 too.
- **Current.** Retail 1.130's `sounds.dat` files are rewritten for today's numbers (49 zones differ; daoc-re's
  comparison), but still use a few classic ones.

`make sounds` with `CLASSIC_CLIENT` (an install with `sounds/sndfiles.csv`) and `CURRENT_CLIENT` (a retail install),
for example in `private/play.conf`, builds both sets into the library (`mpk sounds … --classic DIR --current DIR`).
Settings → Sound → Zone sounds picks one (Classic by default); without them the client plays the install's own files
as retail does.
- A number: the set's own table first (classic: `sndfiles.csv`; current: `cc_sounds.csv`), then the other. A classic
  sound plays its file plainly (rate 1, full volume, centred), heard fully to its first distance and fading to its
  second.
- A name: the tables, then a small alias list of the install's near misses of real groups (`ZoneSoundSets.Aliases`;
  ours).
- A line that still resolves to nothing is left out, so it doesn't hold back its region's random sounds. Today only 506
  (an insect loop the 2004 table itself marks removed). A zone whose classic file plays nothing takes its current one.
- `soundcheck.md` reports each set: lines that play, those left out, and that every classic file decodes.

