# In-game windows: what a playable client needs, and what it takes

What the retail interface consists of, what each window needs from the server, and how far our client is.
This keeps the UI work (roadmap M9 onward) in step with the protocol work it depends on: a window is only
as useful as the data behind it. Built 2026-11-09 from the owner's install (`ui/uimain.xml` and the
`ui/atlantis` skin), OpenDAoC's packet writers (`opendaoc-fork`, `GameServer/packets/Server`) and our code.

## How to read it

Each window needs three things, in this order:

1. **Data from the server:** the packets that carry what it shows. OpenDAoC must send them (a 1.65 server
   doesn't send everything retail does).
2. **Ours to decode and keep:** `DaocProtocol` parses the packet, `NetworkBridge` raises it, and the client
   keeps the state (your vitals, the target, the inventory…).
3. **The window:** drawn from the user's installed skin (the XML loader, M9), or by us.

"Ours" below says how far each is: **decoded** (the packet is parsed), **shown** (something on screen
shows it, even if not yet the retail window), **—** (nothing yet).

## Two kinds of retail window

- **XML windows.** `ui/uimain.xml` includes 76 files (75 windows and the shared styles), each defining a
  window's layout and binding its controls to game values ("adapters") and actions ("events"). The skin
  loader planned in M9 draws these, and a community skin replaces them.
- **Built-in windows.** Some core windows have **no XML file**: the inventory and paper doll, the
  character sheet, the spell and combat-style sheets, and the quickbars. The menu bar's
  `ToggleStatsInventory`, `ToggleStatsSkills` and `ToggleStatsStyles` open them. Their art is the old
  bitmap set in `data/` (`pperdoll.bmp`, `charsheet.bmp`, `Magicsheet.bmp`, `combstylesheet.bmp`,
  `buttons.bmp`…; a skin overrides it from `ui/custom/oldstyle/`), and their layout is in the client
  itself. **The XML loader alone won't give us these:** we lay them out ourselves, on retail's art,
  matching retail screenshots.
- **Which ones (daoc-re RE/63, 2026-11-09):**
  - **The stats panel** (right-hand, 192 × 448) is a mix. Its tab index, Attributes, Group and
    Specs/Abilities pages are XML. Inventory (paper doll), combat styles and magic use the old art
    (`pperdoll.bmp`, `combstylesheet.bmp`, `Magicsheet.bmp`, each 192 × 448).
  - **Also without an XML file:**
    - quickbars 1–3;
    - the trade window (`trade.bmp`, 384 × 256) and trade timer;
    - the bank (`vault.bmp`, 224 × 400);
    - the lag meter and time display;
    - character info;
    - NPC talk (384 × 320) and dialogs (320 × 128);
    - the quest log (`questbg.bmp`, 512 × 256);
    - the spell-effects bar;
    - the tradeskill window;
    - consignment results and the money selector;
    - six housing windows, siege and the war map.
  - **Possibly XML after all:** some of these may be an XML window under another name (bank and
    `vault_window`, NPC talk and `interact_window`, quest log and `quest_window`). Check before building
    one ourselves.
  - **Unknown:** their inner layout is in the client and hasn't been read.

## The windows

### Always on screen (M9)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| `summary`, `new_summary_window` | Your health, power, endurance; the target's name and health; level, realm rank, XP bar | 0xAD vitals; 0x91 XP/points; 0x16 player info; target from our own selection | Window built 2026-11-09 (retail's new_summary_window layout, our style): name, level, RR, your three bars, target name and health | M9 |
| `float_target_window` | The target's name and health; assist, face, follow, stick, attack, invite | The target's health % rides in 0xA1/0xA9 updates; 0xF6 ChangeTarget | Window built 2026-11-09 (float_target_window layout): name in con colour, health, FACE/FOLLOW/STICK (client-side), INVITE/ASSIST (server commands), ATTACK | M9 |
| Quickbars (built-in) | Spells, styles, abilities, items and macros on keys 1–0, several bars | 0x16 skills (sub 1) and spell lines (sub 2) to fill them; 0xD6 cooldowns; using one sends C→S use-skill/spell | Lists, cooldowns (0xD6) and the use requests (0xBB, 0x7D, 0x71) done and checked live (2026-11-09; `/useskill <name>` until the bars exist); the bars — | M9/M10 |
| `chat_window` (+ `context_window`, `chatrename`, `chat_config_window`, `channel_window`) | Several chat windows (tabs), each with its own filter of message types and default channel, font size and **window/font transparency** | 0xAF messages (type byte) | One log, all types, coloured per type; slash commands sent. No tabs, filters or transparency | M9 |
| `compass` | Heading, on a bar | None (our own heading) | — | M9 |
| `menu_bar_window` | Buttons for the main windows; money; XP bar; frame rate and lag meters | 0xFA money; 0x91 XP | Money and XP decoded (2026-11-09); no window | M9 |
| `performance_window` | Frame rate, lag, performance meters (retail's "packet monitor") | Ping timing (0x29) | Dev overlay shows FPS and pings | M9 |
| `timer_window` | A countdown (release timer, others) | 0xF3 | Decoded; release timer shown in the death panel | M9 |
| `clock_window` | Game time of day | 0x7E Time | — | M9 |
| `float_level_exp_window`, `float_realm_exp_window`, `float_champ_exp_window` | Floating XP bars | 0x91 | Decoded (2026-11-09); no window | M9 (level); RR/CL later |

### Menus and settings (M9)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| `main_menu_window` | Quit, options, keys, help, interface elements | None | Settings dialog (ours), Quit | M9 |
| `options_window` | Game options | None (client settings) | Settings dialog: display, game names, sprint | M9 |
| `key_config` | Key bindings (FPS / MMO / RPG presets) | None | Key bindings panel | M9 |
| `interface_elements_window` | Switches every optional window on or off | None | — | M9 |
| `command_window` | Buttons for /afk, /bind, /face, /follow, /pray, /release, /sprint, /stick, sit, attack… | Commands go out as 0xAF `&…` | The commands work typed; no window | M9 |
| `help_window` | Help text | 0xF7 HelpWindow | — | M9 |

### Talking to the world (M9)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| `interact_window` | An NPC's words with clickable [keywords] | 0xAF (popup types) | Shown (our NPC window), keywords whisper back | M9 ✓ |
| `info_window` | A text window from the server | 0xC4 DetailWindow (custom text) | — | M9 |
| `release_window` | Release to bind / city / house after death | 0xF3, 0xAE | Shown (our death panel: Release) | M8 ✓ |
| `map_window` | Zone and region maps | None | Shown (our map window, M) | M9 ✓ |
| `value_chooser` | Pick a quantity (buy, split a stack) | None | — | M10 |

### Your character (M10)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| Inventory and paper doll (built-in) | Worn gear on a figure, backpack slots, drag and drop, encumbrance | 0x02 InventoryUpdate (equipment, backpack), 0xBD encumbrance; moves go C→S | — (others' visible gear 0x15 is decoded and drawn) | M10 |
| Character sheet (built-in) / `stats_attributes_window` | Stats, resists, armour factor, level, class, race | 0xFB stats and resists; 0x16 player info | — | M10 |
| `stats_combat_window` | Weapon skill, damage, speed | 0x16 / 0xFB | — | M10 |
| `stats_spec_abil_window` | Specialisations, abilities, trade skills | 0x16 skills | — | M10 |
| `stats_spells_window`, spell and style sheets (built-in) | Spell lines and combat styles, to cast or drag to a quickbar | 0x16 spell lines and skills; 0xD8 delve text | — | M10 |
| `concentration_window` | Your concentration buffs | 0x75 | — | M10 |
| `quiver_window` | Arrows in the quiver | 0x02 | — | M10 |
| Effect icons (built-in?) | Active buffs and debuffs with timers | 0x7F UpdateIcons | — | M10 |
| `mini_info` | Delve: an item's or spell's details | 0xD8 DelveInfo | — | M10 |
| `title_window` | Titles to choose | 0xC4 (titles) | — | later |

### Trade and money (M10)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| `merchant_window` | A merchant's pages of items; buy/sell | 0x17 MerchantWindow; 0xFA money | — (talking to merchants works; their window is the open M9 item) | M10 |
| Trade window (built-in?) | Player-to-player trade | 0xEA TradeWindow | — | later |
| `vault_window` | Bank vault | 0x02 (vault) | — | later |
| `minicraft_window` | Craft, repair, salvage progress | 0x16 crafting; timer | — | later |
| `bazaar_query` | Market search (housing consignment) | 0x1F MarketExplorer | — | later |

### Group, guild, friends (after M10)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| `minigroup_window`, `new_group_window`, `group_buffs`, `stats_group_window` | Group members' health, power, endurance, class, level, buffs | 0x70 GroupMemberUpdate | — | later |
| `lfg_window`, `group_results_window`, `casual_group_finder_window`, `group_options_window` | Looking for group, finder | 0x86, 0xE7, 0xB4 | — | later |
| `community_window`, `minifriends_window` | Guild, alliance and friends lists, MOTD, guild commands | 0xC5/0xC6 friends; guild and alliance lists (packets to identify in OpenDAoC) | — | later |
| `emblem_picker` | Guild emblem | 0xE2 | — | later |

### Quests and training (after M10)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| `quest_window`, `new_quest_journal_window` | Quest journal | 0x83 QuestEntry | — (NPC quest glyphs 0x4C shown) | later |
| `new_quest_window`, `new_quest_finish_window` | Offer and reward dialogs | Quest offer/reward packets (to identify in OpenDAoC) | — | later |
| `training_window`, `train`, `daoc_subclass_window` | Train specialisations and realm abilities | 0x7B TrainerWindow; 0x91 points | — | later |

### Pets, mounts, realm war (later)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| `minipet_window` | Pet health, stance, commands | 0x88 PetWindow | — | later |
| `mount_window` | Horse, saddlebags | 0x4E, 0xC8, 0x02 (horse bags) | — | later |
| `realmwar_alb/mid/hib/agramon/key`, `realm_bonuses` | War map: keeps, relics, realm bonuses | 0x49, 0x4A, 0x66, 0x45/0x46 | — | later (frontiers) |
| `keep_status_window`, `keep_upgrade_window`, `hookpoint_merchant_window`, `siege_window`, `siegetower_status_window`, `mino_relic_window` | Keeps, siege, relics | 0x61–0x6E, 0xF5, 0xE3 | — | later (frontiers) |
| `master_level_window`, `float_champ_exp_window` | Master levels, champion levels | 0x13 | — (not in the 1.65 rules our server runs) | low |
| `emoticon_window` | Emote picker | 0xF9 EmoteAnimation | — | later |

### Not windows

- **Torch:** a key toggles it; the server tracks it (players and NPCs, OpenDAoC `IsTorchLighted`). It's an
  action and a light, not a window: M10 (key) and M12 (the light).
- **Minimap:** retail 1.127 has no minimap window among these (the map window is full-size). A minimap
  would be ours, from the same map pictures: optional, after the map window.

## What this means for the order of work

- **The data layer is the bigger gap, not the windows.** We decode 0xAD (vitals), 0xAF (chat), 0xF3
  (timer), 0xAE/0x89 (death), 0x91 (XP and points), 0xFA (money), 0x16 (who the character is, its skill, spell and crafting lists, weapon stats) and the
  world's entity packets. Nearly every other window waits on a packet we don't parse yet: 0x02 (inventory), 0xFB (stats), 0x7F (effect icons), 0x17 (merchant), 0xD8 (delve). Each is a small, testable piece in
  `DaocProtocol`, verifiable against OpenDAoC's writer and a live session, and worth doing before its window.
- **M9's own windows:** the summary (vitals + target), the floating target window, the compass, the menu
  bar, the command and interface-elements windows, chat tabs and filters, and the quickbars (once the
  skill and spell lists arrive).
- **M10's:** inventory and paper doll, character sheet, spells and styles, merchant, effect icons.
- **Built-in windows need our own layout** on retail's art; the XML loader covers the rest.

## Chat: what to test

M6 counted chat done when a /say went out and replies came back. What retail chat does, to check one by one
(owner, live):

- [ ] /say, /yell (if the server has it), /emote and `:` emotes
- [ ] /send (/tell) out and in; reply to the last tell
- [ ] /group, /guild, /officer, /alliance (needs a group/guild)
- [ ] /broadcast (region), /lfg, /trade, /advice channels; joining and leaving chat channels (/cg, /bg)
- [ ] System, combat, loot, spell, item and merchant messages: each shows in its colour
- [ ] Screen-centre messages (type 0xC8–0xCB): retail shows them mid-screen; we drop them today
- [ ] Long lines wrap; [keywords] in NPC speech are clickable
- [ ] Enter starts chatting and returns control to the game after sending

Built later: several chat windows with their own filters, default channel, font size and transparency
(retail's `context_window`).

## Screen size and UI scale

See roadmap M9, "Screen and UI scale", which records retail's behaviour (one UI scale from 1.0 to 4.0
over a virtual screen; absolute window positions saved per character; opacity per window; a Toggle
Interface key) and our plan.
