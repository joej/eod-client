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
| `summary`, `new_summary_window` | Your health, power, endurance; the target's name and health; level, realm rank, XP bar | 0xAD vitals; 0x91 XP/points; 0x16 player info; target from our own selection | Both built, our style. Default since 2026-11-10: the classic `summary` (128 × 100, from summary.xml): name, Hit/End/Pow labels and bars (word labels as in retail screenshots, where the stock skin draws percentages), Exp bubbles and fine bar, target name and health. Settings → Game → Summary: New = new_summary_window (2026-11-09): name, level, RR, your three bars (power always drawn, empty for a class without power, as retail's; 2026-11-10), target name and health | M9 |
| `float_target_window` | The target's name and health; assist, face, follow, stick, attack, invite | The target's health % rides in 0xA1/0xA9 updates; 0xF6 ChangeTarget | Window built 2026-11-09 (float_target_window layout): name in con colour, health, FACE/FOLLOW/STICK (client-side), INVITE/ASSIST (server commands), ATTACK | M9 |
| Quickbars (built-in) | Spells, styles, abilities, items and macros on keys 1–0, several bars | 0x16 skills (sub 1) and spell lines (sub 2) to fill them; 0xD6 cooldowns; using one sends C→S use-skill/spell | Two bars of ten pages built 2026-11-09 (retail's default.ini places and size): icons from the install's sheets through gamedata's icons.csv, spells.csv and styles.csv, with border frames; keys 1–0 for bar 1; cooldown veil; right-click to choose, drag to swap; saved per character. Bar 3 at retail's left-edge place, and slots take a skill or spell dragged from another window (for the spell and style windows), 2026-11-09. Style marks (wgsm_0, daoc-re RE/68) and retail's number keys (1–0, Alt+ bar 2, Ctrl+ bar 3, Shift+ page) 2026-11-09. Bar 3 hidden by default as in retail's default.ini, `/quickbar <bar> on|off` (1|0 too) shows or hides one per character, page arrows faint until the mouse is over the bar, 2026-11-10. Not yet: /qbind and /qbar, the spell-mark column (wglg_100), importing retail's saved bars (its ini [Quickbar] Hotkey_N lines; ids only match if the server sends the same lists) | M9/M10 |
| `chat_window` (+ `context_window`, `chatrename`, `chat_config_window`, `channel_window`) | Several chat windows (tabs), each with its own filter of message types and default channel, font size and **window/font transparency** | 0xAF messages (type byte) | Built 2026-11-13 as retail's (`WorldView.ChatWindow`, rules in `ChatRules`): one window, 312 × 195 at retail's place, dragged by its top edge; Main (58% opaque) and Combat (38%) tabs with retail's filters and 26-colour palette; lines drawn for 90 s at the bottom, the wheel scrolls back through everything (2500 lines); the sticky channel (`/g `, `/gu `, … followed by a space), `%t`, repeats within 250 ms dropped; 0xC8–0xCB in the middle of the screen. No chat options window (filters and colours fixed at retail's defaults), no per-character save, no second window | M9 |
| `compass` | Heading, on a bar | None (our own heading) | Built 2026-11-10: the stock skin's compass.bmp strip (512 × 32 for 360°, 90° shown, daoc-re RE/74) above quickbar 1 at retail's default.ini place; Settings → Game → Compass (retail starts it hidden) | M9 |
| `menu_bar_window` | Buttons for the main windows; money; XP bar; frame rate and lag meters | 0xFA money; 0x91 XP; 0xA3/0x29 ping round trip | Built 2026-11-10 (skin positions, our style), shown by default along the bottom: Exp bubbles + fine bar, P/G/S/C, page buttons (MAP, MENU and the six stats-panel pages work; JOURNAL and WARMAP dimmed until built), the three meters (frame rate, lag from our ping's round trip, performance = traffic arriving; retail's thresholds) once a second. MENU opens the main menu (IN-GAME OPTIONS, CONFIGURE KEYBOARD, QUIT; HELP and INTERFACE ELEMENTS dimmed) | M9 |
| `performance_window` | Frame rate, lag, performance meters (retail's "packet monitor") | Ping timing (0x29) | Dev overlay shows FPS and pings | M9 |
| `timer_window` | A countdown (release timer, others) | 0xF3 | Decoded; release timer shown in the death panel | M9 |
| `clock_window` | Game time of day | 0x7E Time (u32 BE ms since midnight, u32 BE day increment; at entry and every 15 min) | Built 2026-11-10: "h:mm AM/PM" (retail's format: "0:05 AM" after midnight), advanced locally (daytime at the increment, night 1.25× faster, as OpenDAoC measured 1.127); shown by default, Settings → Game → Clock or its ✕. The sky doesn't follow it yet | M9 |
| `float_level_exp_window`, `float_realm_exp_window`, `float_champ_exp_window` | Floating XP bars | 0x91 | Level XP built 2026-11-10 (our style): ten bubbles = ‰ / 100, the fine blue bar = ‰ mod 100, "Level: n"; also on the classic summary; this floating window hidden by default as in retail (default.ini place, bottom right); Settings → Game → Floating XP window | M9 (level); RR/CL later |

### Menus and settings (M9)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| `main_menu_window` | Quit, options, keys, help, interface elements | None | Settings dialog (ours), Quit | M9 |
| `options_window` | Game options | None (client settings) | Settings dialog; row by row in "Game Options: retail's rows vs ours" below | M9 |
| `key_config` | Key bindings (FPS / MMO / RPG presets) | None | Key bindings panel | M9 |
| `interface_elements_window` | Switches every optional window on or off | None | Built 2026-11-13 (`WorldView.Elements`, our style): retail's 350 × 475 switchboard, its five groups and buttons; lit when on; the windows not built yet greyed. Main menu → INTERFACE ELEMENTS | M9 ✓ |
| `command_window` | Buttons for /afk, /bind, /face, /follow, /pray, /release, /sprint, /stick, sit, attack… | Commands go out as 0xAF `&…` | Built 2026-11-13: 135 × 216, retail's two columns; each button sends the typed command or does the client's own (follow, stick, face, clock, compass, map, quit, keyboard); KEEPS, FRIENDS, HELP, JOURNAL greyed. Hidden until Interface Elements → Command | M9 ✓ |
| `help_window` | Help text | 0xF7 HelpWindow | — OpenDAoC sends 0xF7 only for the starter help and a house's rent reminder; the text is the install's own help topics (index, then the topic's file), not traced | later |

### Talking to the world (M9)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| `interact_window` | An NPC's words with clickable [keywords] | 0xAF (popup types) | Shown (our NPC window), keywords whisper back | M9 ✓ |
| `info_window` | A text window from the server | 0xC4 DetailWindow (custom text) | Shown for any 0xC4 (items' descriptions since 2026-11-11, and the server's own text windows) | M9 ✓ |
| `release_window` | Release to bind / city / house after death | 0xF3, 0xAE | Shown (our death panel: Release) | M8 ✓ |
| `map_window` | Zone and region maps | None | Shown (our map window, M) | M9 ✓ |
| `value_chooser` | Pick a quantity (buy, split a stack) | None | — | M10 |

### Your character (M10)

| Window | Shows / does | Server data | Ours | Milestone |
|---|---|---|---|---|
| Inventory and paper doll (built-in) | Worn gear on a figure, backpack slots, drag and drop, encumbrance | 0x02 InventoryUpdate (equipment, backpack), 0xBD encumbrance; moves go C→S | Built 2026-11-11 as the stats panel's Inventory page: each slot where `pperdoll.bmp` has its frame (our style, word labels), item icons by model (objects.csv → icons.csv), 5 bags of 8 backpack rows, money and encumbrance; hover describes, double-click uses (0x71), drag moves (0xDD), onto the figure wears (slot 100), onto a quickbar uses later, right-click opens `mini_info`. No figure drawn in the middle yet | M10 |
| Character sheet (built-in) / `stats_attributes_window` | Stats, resists, armour factor, level, class, race | 0xFB stats and resists; 0x16 player info | Built 2026-11-11 as the stats panel's Attributes page (skin positions, our style; each stat's and resist's make-up in its tooltip; our own "additional info" lines; only OPTIONS of the six buttons works) | M10 |
| `stats_combat_window` | Weapon skill, damage, speed | 0x16 / 0xFB | — | M10 |
| `stats_spec_abil_window` | Specialisations, abilities, trade skills | 0x16 skills | Built 2026-11-11 as the stats panel's Skills page | M10 |
| `stats_spells_window`, spell and style sheets (built-in) | Spell lines and combat styles, to cast or drag to a quickbar | 0x16 spell lines and skills; 0xD8 delve text | Built 2026-11-11 as the stats panel's Styles and Spells pages: lists with icons (double-click uses, drag to a quickbar, right-click opens `mini_info`). The panel's tab strip (`stats_index`) has word tabs | M10 |
| `concentration_window` | Your concentration buffs | 0x75 | — | M10 |
| `quiver_window` | Arrows in the quiver | 0x02 | — | M10 |
| Effect icons (built-in?) | Active buffs and debuffs with timers | 0x7F UpdateIcons | — | M10 |
| `mini_info`, `info` | An item's details, small (`mini_info`) and full (`info`) | none for `mini_info` (0x02 data); 0xD8 → 0xC4 for `info`; 0xD8 types 100–102 repair, craft, salvage | Built 2026-11-11 for inventory items: right-click selects and opens it (again to close), the skin's layout (con-coloured name, icon, 7 lines), INFO / SALVAGE / REPAIR / CRAFT, a use entry per use spell; INFO fills the `info` window from 0xC4. Spells and styles since 2026-11-11 (right-click on their pages): "Magical Ability" / "Combat Style", the level, a spell's target and concentration, a style's opening; INFO shows the 0xD8 record's text in the client's wording (a style's to-hit and defence words in colour, its fired spell after it). Not yet for quickbar icons or abilities; `info` isn't resizable | M10 |
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
| `stats_group_window` | The stats panel's Group page: up to 8 members (name, class, level; health, endurance and power bars; 6 effect icons), and the group buttons | 0x16 subcode 6 (the list), 0x70 (bars, state, icons); C→S 0x87 invite, 0x9F disband | Built 2026-11-11 at the skin's positions: click a row to target that member; INVITE, DISBAND, ASSIST, FOLLOW, STICK, FACE, MAP work and dim where the server would refuse; MINI, FIND, SET LFG, BENEFITS, WARMAP, CGF, OPTIONS open windows not built yet (dimmed). A member's state (dead, mezzed, link dead, away) shows in orange where its class goes (retail's name-colour rule is unknown) | M10 |
| dialog box | The server's questions and notices: group and guild invites, scripts' yes/no questions | 0x81 Dialog; C→S 0x82 answer | Built 2026-11-11: 320 × 128, centred, YES / NO or OK; a new one replaces one still open | M10 |
| `minigroup_window`, `new_group_window`, `group_buffs` | The floating group window, buffs | 0x70 GroupMemberUpdate | — | later |
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
  (timer), 0xAE/0x89 (death), 0x91 (XP and points), 0xFA (money), 0x16 (who the character is, its skill, spell and crafting lists, weapon stats), 0xFB (stats and resists), 0x02 and 0xBD (inventory, encumbrance) and the
  world's entity packets. Nearly every other window waits on a packet we don't parse yet: 0x7F (effect icons), 0x17 (merchant), 0xD8 (delve). Each is a small, testable piece in
  `DaocProtocol`, verifiable against OpenDAoC's writer and a live session, and worth doing before its window.
- **M9's own windows:** the summary (vitals + target), the floating target window, the compass, the menu
  bar, the command and interface-elements windows, chat tabs and filters, and the quickbars (once the
  skill and spell lists arrive).
- **M10's:** inventory and paper doll, character sheet, spells and styles, merchant, effect icons.
- **Built-in windows need our own layout** on retail's art; the XML loader covers the rest.

## Game Options: retail's rows vs ours

Retail's Game Options window (main menu → IN-GAME OPTIONS) row by row, against our Settings dialog (checked
2026-11-13). Status: ✅ built, 🟡 partly, ❌ not built. **Decision** is the owner's: *build*, *ignore*, or
*done* (checked off). Values in quotes are the ones retail's window shows.

| Retail option | Values | Ours | Status | Waits on | Decision |
|---|---|---|---|---|---|
| Names | "Colored" / White | Settings → Game → Names | ✅ | | |
| Display Names: Monsters/NPCs, Gravestones, Players, Your Name, Groupmates, Guildmates, Enemy Players | on/off each | Settings → Game, all seven; groupmates and guildmates count as players until groups and guilds are shown on names | ✅ | | |
| Sound Effects Volume | "1" … | — | ❌ | audio (M12) | |
| Music Volume | | — | ❌ | audio (M12) | |
| Ambient Sounds Volume | | — | ❌ | audio (M12) | |
| Ambient Music Volume | | — | ❌ | audio (M12) | |
| Spell Effects | "All" … | — | ❌ | spell effects (M12) | |
| Terrain Clip Plane | Near / "Far" | view distance on the F3 key and in settings.cfg, not in the dialog | 🟡 | | |
| Figure Clip Plane | "Near" … | — (figures drawn as far as the server sends them) | ❌ | | |
| Shadows | "Nearest 5" … | sun shadows on/off as a dev layer; figures cast none | 🟡 | figure shadows | |
| Snapback | "Off" / on | — (rules known: off by default, `/snapback on` = 2.5 s) | ❌ | | |
| Show Facial Morphs | "All" … | — (no face morphs) | ❌ | figures with morphs | |
| Tooltip Transparency | "Off" … | — (Godot's default tooltip) | ❌ | | |
| Tooltip Delay | "No Delay" … | — | ❌ | | |
| Quickbars | One / Two (Alt) / Three (Ctrl) | three bars, each shown or hidden on its own; no "how many" choice | 🟡 | | |
| Spell Effect Icons: Width, Height, Oldest First | "10" × "4", on | — | ❌ | the effect icons window (M10) | |

Ours only (not in retail's window): Display tab (window mode and size, frame rate, terrain look, interface size,
field of view), Game tab (compass, clock, summary style, floating XP window), Keyboard tab (retail has a separate
Configure Keyboard window).

### Other retail settings, outside the Game Options window

What retail keeps elsewhere: login-menu options and `user.dat` (per install), the character's ini (per character),
slash commands. Same columns.

| Retail setting | Where retail keeps it | Values | Ours | Status | Waits on | Decision |
|---|---|---|---|---|---|---|
| Mouse | login menu, `user.dat [game]` | On / Off | — | ❌ | | |
| Mouselook Up (invert) | login menu, `user.dat [game]` | Default / Inverted / Off | — (no invert choice) | ❌ | | |
| Mouselook Sensitivity | login menu, `user.dat [game]` | 1–5 = ×0.5 … ×1.5, default 3 | — (fixed) | ❌ | | |
| Mouselook lock | `user.dat [main] mouselooklock` | keep mouselook on, cursor captured | — | ❌ | | |
| Snapback | `user.dat [performance]`, `/snapback on\|off` | off / 4.5 / 2.5 / 1.0 s, default off | — | ❌ | | |
| Water | `user.dat [water]` | Reflective / Classic / Shrouded Isles; reflection quality High/Medium/Low; update Often/Occasional/Seldom | one look of our own (audit B6) | 🟡 | | |
| Atlantis terrain | graphics option | on / off | Settings → Display → Terrain | ✅ | | |
| Screen size, window mode | `user.dat` | | Settings → Display | ✅ | | |
| UI scale | `user.dat`, top-left-corner drag; no options control | 1.0–4.0 | Settings → Display → Interface size, and the drag | ✅ | | |
| Disable the UI-scale drag | `user.dat DisableLmbUiScaling` (1.130) | on / off | — | ❌ | | |
| Spell effects shown | `user.dat [main] EffectsFlag`, also sent to the server (0xDC) | all / mine / none / mine + group | — | ❌ | spell effects (M12) | |
| Classic icons | `user.dat [main] ClassicIcons` | on / off | — | ❌ | | |
| Name colours | character ini `[NameOptions] Coloring`, `/colornames` | con colours / white | Settings → Game → Names | ✅ | | |
| Name visibility | character ini `[NameOptions] Visibility` | the seven Display Names bits | Settings → Game | ✅ | | |
| Face morphs | `/showmorphs all\|self\|none` | | — | ❌ | figures with morphs | |
| Camera home | character ini `[Camera]`, `/setcamera`, `/resetcamera` | distance, height, angle, tilt | per character in our settings | ✅ | | |
| Camera mode, Camera Toggle key | `user.dat [main] cameramode` | first person / third / no camera (inferred) | first person by zoom only, no key | 🟡 | | |
| Window layout | character ini `[Panels]` (place, alpha, font alpha), defaults from `default.ini` by resolution | | places and alpha per install, not per character; no font alpha; defaults ours (audit B12) | 🟡 | | |
| Chat tabs | character ini `[Chat]`, chat options window | channels, filters, colours per tab | retail's two default tabs, fixed | 🟡 | chat options window | |
| Quickbars | character ini `Hotkey_i=` | | ours per character; no import of a retail ini | 🟡 | | |
| Quickbar keys | `/qbind` | a slot to a key | — | ❌ | | |
| Macros | character ini `[Macros]`, `/macro` | 100 macros | — | ❌ | | |
| Target ring | none: retail has no ring, the target's name plate turns white | | our ring, always on | 🟡 | | |

### Keys

Retail's Configure Keyboard (checked 2026-11-13): 90 binding slots, 78 named, 75 rows in 8 sections; # is the slot.
Each slot holds one key plus modifiers (Shift, Alt, Ctrl) or a mouse button; second binds are their own slots
(Forward2, Back2, Run Lock 2). Defaults are the RPG / MMO / FPS presets ("none" = explicitly unbound). Movement keys
are read held and ignore modifiers; the rest need their exact modifiers. Not bindable: Escape, Enter, `/`, `'`, `]`,
the number row (quickbars), Shift as a main key, F12. The client forces NumLock off, so keypad 4/6 are also Left/Right.

Ours: 14 rebindable actions (Settings → Keyboard) with the four presets; other keys are fixed, several of them dev
keys that sit on retail's (F1–F6, Tab, P).

| # | Action | RPG | MMO | FPS | Held/press | What it does | Ours | Status | Decision |
|---|---|---|---|---|---|---|---|---|---|
| **Movement** | | | | | | | | | |
| 0 | Forward | W | W | E | held | Move forward. | W / W / E, rebindable | ✅ | |
| 20 | Forward2 | Left+Right Mouse | Up | Up | held | Second bind for Forward. | — | ❌ | |
| 1 | Back | S | S | D | held | Move back; ends autorun. | S / S / D, rebindable | ✅ | |
| 33 | Back2 | Down | Down | Down | held | Second bind for Back. | — | ❌ | |
| 2 | Left | A | Left | Left | held | Turn left; slides instead while action 6 is held. | A, rebindable; MMO/FPS use keypad 4 only, not the arrows | 🟡 | |
| 3 | Right | D | Right | Right | held | Turn right; slides instead while action 6 is held. | D, rebindable; MMO/FPS use keypad 6 only, not the arrows | 🟡 | |
| 6 | (blank) | none | none | — | held | Blank name in the dialog. While held, Left/Right slide instead of turning (a strafe modifier). | — | ❌ | |
| 8 | Slide Left | Q | A | S | held | Strafe left; ends autorun. | Q / A / S, rebindable | ✅ | |
| 9 | Slide Right | E | D | F | held | Strafe right; ends autorun. | E / D / F, rebindable | ✅ | |
| 4 | Jump/Up | Space | Space | Space | held | Jump, or swim/fly up. Mounted: dismount ("You try to dismount."). On a boat: says to type /disembark. 1 s lockout. | Space, rebindable (no dismount or boat rules) | ✅ | |
| 5 | Down | Z | Q | Z | held | Swim/fly down. | — | ❌ | |
| 57 | Sit | X | N | N | press | Sit / stand. | `/sit`, command window | 🟡 | |
| 7 | Walk | Scroll Lock | Shift+K | Shift+K | held | Walk while held. | — | ❌ | |
| 42 | Runlock | NumLock | T | Y | press | Autorun on/off. | NumLock in every preset (MMO T, FPS Y in retail) | 🟡 | |
| 83 | Run Lock 2 | none | NumLock | NumLock | press | Second bind for Runlock. | — | ❌ | |
| 66 | Sprint | V | F | G | press | Sprint on/off (the /sprint toggle). | V in every preset (MMO F, FPS G in retail) | 🟡 | |
| 62 | Follow | Shift+F | Y | Shift+V | press | Follow the target. | button only | 🟡 | |
| 63 | Stick | Shift+V | E | V | press | Stick to the target (close follow). | button only | 🟡 | |
| 64 | Face | F | R | B | press | Turn to face the target. | button only | 🟡 | |
| **Combat** | | | | | | | | | |
| 68 | Right Hand Weapon | B | Shift+Q | Shift+W | press | Switch to the right-hand weapon. | — | ❌ | |
| 69 | Two Handed Weapon | N | Shift+E | Shift+R | press | Switch to the two-handed weapon. | — | ❌ | |
| 70 | Ranged Weapon | , | Shift+R | Shift+T | press | Switch to the ranged weapon. | — | ❌ | |
| 27 | Combat Mode | backtick | X | C | press | Attack mode on/off. | H in every preset (ours) | 🟡 | |
| **Targeting** | | | | | | | | | |
| 43 | Consider | F6 | none | none | press | Consider (con) the target. Without one: "You must select a target to Consider!" | — (F6 is our travel window) | ❌ | |
| 72 | Nearest Loot | F7 | H | - | press | Target the nearest lootable object ("No lootable object in range."). | — | ❌ | |
| 26 | Target Object | F8 | Shift+H | Shift+- | press | Target the nearest object ("No object target in range."). | — | ❌ | |
| 24 | Target Enemy | Tab | Tab | Tab | press | Target the nearest enemy ("No enemy target in range."). | — (Tab is our free-fly camera) | ❌ | |
| 25 | Target Friend | Ctrl+Tab | Ctrl+Tab | backtick | press | Target the nearest friendly ("No friendly target in range."). | — | ❌ | |
| 35 | Last Attacker | Shift+Tab | Shift+Tab | Shift+Tab | press | Target your last attacker ("Your last attacker is not in range." / "…not visible."). | — | ❌ | |
| 44 | Ground Target | F5 | C | Alt+A | press | Ground-target placement on/off (inferred, not traced). | — | ❌ | |
| 45 | Target Group1 | F1 | none | none | press | Target group member 1. | — (F1 is our dev panels) | ❌ | |
| 46 | Target Group2 | F2 | none | none | press | Target group member 2. | — (F2 is our render mode) | ❌ | |
| 47 | Target Group3 | F3 | none | none | press | Target group member 3. | — (F3 is our view distance) | ❌ | |
| 48 | Target Group4 | F4 | none | none | press | Target group member 4. | — (F4 is our packet log) | ❌ | |
| 49 | Target Group5 | Shift+F1 | none | none | press | Target group member 5. | — | ❌ | |
| 50 | Target Group6 | Shift+F2 | none | none | press | Target group member 6. | — | ❌ | |
| 51 | Target Group7 | Shift+F3 | none | none | press | Target group member 7. | — | ❌ | |
| 52 | Target Group8 | Shift+F4 | none | none | press | Target group member 8. | — | ❌ | |
| **Interaction** | | | | | | | | | |
| 11 | Open | none | Shift+Space | Shift+Space | press | Interact with what is picked at the pointer: a door, object or figure. | — | ❌ | |
| 38 | Destroy | Shift+D | Shift+D | Shift+D | press | Destroy the selected backpack item, after "Do you really wish to destroy this item?". | — | ❌ | |
| 34 | Use Item | U | U | U | press | Use the selected item (primary use). | — | ❌ | |
| 71 | Use Item Secondary | Shift+U | Shift+U | Shift+U | press | Use the selected item (secondary use). | — | ❌ | |
| 65 | Craft | Shift+C | Shift+C | Shift+C | press | Craft with the selected backpack item ("You must select an item in your backpack!"). | — | ❌ | |
| 21 | Get | G | G | = | press | Get: pick up the target or nearby item. | — | ❌ | |
| 29 | Sell | Shift+S | Shift+S | Shift+S | press | Sell the selected backpack item to the open merchant. 1 s lockout ("You must wait one more second to interact again!"). | — | ❌ | |
| **Camera** | | | | | | | | | |
| 14 | Reset Camera | Num 5 | Z | X | held | Reset the camera behind the character. | Home (2001 manual); classic layout D | 🟡 | |
| 61 | Pan Camera | none | none | none | held | Pan: orbit the camera around the character without turning it. | — | ❌ | |
| 86 | Camera Toggle | F11 | F11 | F11 | press | Cycles the camera mode: first person, third-person orbit, mode 2, first person again. Does nothing in the death camera. | — | ❌ | |
| 18 | Mouse | none | none | none | press | Lock or unlock mouselook (same as user.dat mouselooklock; captures the cursor). | — | ❌ | |
| 39 | Mouse Look Toggle | Right Mouse | Right Mouse | Right Mouse | held | Mouselook while held. If "mouselook on by default" is set, holding it turns mouselook off instead. | right-drag turns and re-aligns, fixed | 🟡 | |
| 78 | Mouse Pan | Left Mouse | Left Mouse | Left Mouse | held | Pan the camera while held; also mouselook unless "mouselook on by default" is set. | left-drag pans, fixed | 🟡 | |
| 12 | Look up | Ins | none | none | held | Tilt the camera up. | — | ❌ | |
| 13 | Look Down | Del | none | none | held | Tilt the camera down. | — | ❌ | |
| **Chat** | | | | | | | | | |
| 53 | PageUp | PgUp | PgUp | PgUp | press | Page the chat window up. | — | ❌ | |
| 54 | PageDown | PgDn | PgDn | PgDn | press | Page the chat window down. | — | ❌ | |
| 36 | Reply | R | Alt+R | Alt+R | press | Start a reply: puts "send <last sender> " (or "advice <name> ") on the chat line. Pressing again within 1 s steps back to older senders (3 kept). With none: "You cannot reply until you receive a message." | — | ❌ | |
| 37 | ChatLog | Ctrl+L | Ctrl+L | Ctrl+L | press | Chat logging to file on/off (inferred). | — | ❌ | |
| **Show** | | | | | | | | | |
| 31 | Inventory | I | B | Ctrl+B | press | Character window, Inventory page; press again to close. | menu bar button | 🟡 | |
| 30 | Stats | C | Ctrl+C | Ctrl+C | press | Character window, Stats page; again to close. | menu bar button | 🟡 | |
| 32 | Group | O | Ctrl+G | Ctrl+G | press | Character window, Group page; again to close. | menu bar button | 🟡 | |
| 28 | Show Spells | K | Ctrl+S | Ctrl+S | press | Character window, Spells page; again to close. | menu bar button | 🟡 | |
| 40 | Show Skills | P | Ctrl+D | Ctrl+D | press | Character window, Skills page; again to close. | menu bar button | 🟡 | |
| 41 | Show Combat | L | Ctrl+A | Ctrl+A | press | Character window, Combat page; again to close. | menu bar button | 🟡 | |
| **Misc** | | | | | | | | | |
| 22 | Torch | T | Shift+Y | Shift+Y | press | Torch on/off. | T in every preset (MMO/FPS Shift+Y in retail) | 🟡 | |
| 19 | Toggle Names | Alt+N | Alt+N | Alt+N | press | Overhead names on/off (inferred). | — | ❌ | |
| 10 | Help | Alt+F1 | Alt+F1 | Alt+F1 | press | Open the Help window. | — | ❌ | |
| 58 | Perf. Meter | Alt+P | Alt+P | Alt+P | press | Performance meter: off, mode 1, mode 2, off. | meters always on | 🟡 | |
| 59 | Compass | Alt+C | Alt+C | Alt+C | press | Compass on/off. | Settings / Interface Elements only | 🟡 | |
| 60 | Information/Delve | Shift+I | Shift+I | Shift+I | press | Delve the selected thing. | — | ❌ | |
| 67 | Command Window | ; | Alt+K | Alt+K | press | Command window on/off. | Interface Elements only | 🟡 | |
| 79 | Map Window | M | M | M | press | Map window on/off. | M | ✅ | |
| 81 | Realm War Map | Shift+M | Shift+M | Shift+M | press | Realm war map. | — | ❌ | |
| 82 | Quest Journal | J | J | J | press | Quest journal. | — | ❌ | |
| 80 | Toggle Interface | Alt+Z | Alt+Z | Alt+Z | press | Hide/show the whole interface. | F12 (retail refuses F12 as a bind) | 🟡 | |
| 85 | Take Screenshot | Num - | Num - | Num - | press | Screenshot ("Taking Screenshot!"). | P (ours, saves to private/) | 🟡 | |

Hidden in the dialog: Chat (23), PageUpSys (55, Shift+PgUp), PageDownSys (56, Shift+PgDn), Show Character Info (84).
Retail's preset buttons also set four options (Ctrl and Alt as modifiers only, Forward breaks autorun, modifiers stop
movement): MMO turns Ctrl-as-modifier on, all three turn the others to Alt on, Forward-breaks-autorun on.

## Emotes and postures: retail vs ours

Checked 2026-11-13. Same columns as the settings tables above.

| Retail behaviour | Ours | Status | Waits on | Decision |
|---|---|---|---|---|
| `/wave`, `/bow`, `&<emote>` … sent to the server as commands | typed commands go to the server | ✅ | | |
| The emote's chat line ("You wave…") comes from the server | shown in chat | ✅ | | |
| 0xF9 emote animation: the figure plays its `emotes.csv` clip once (99 emotes, clip per model), then idles | 0xF9 not read; no figure moves | ❌ | | |
| Emote ignored while the figure moves faster than 32 units/s, rides, or is on your ignore list | — | ❌ | 0xF9 | |
| Spell-effect overlay on some emotes (Frenzy, Dragon Roar, Level Up, Pray, Bind, Spellcraft) | — | ❌ | spell effects (M12) | |
| Smile / Angry change the face for 4 s | — | ❌ | figures with morphs | |
| Horse emotes (`&horse halt\|nod\|graze\|…`), whistle sound | — | ❌ | mounts | |
| Siege engines: emotes 200 / 201 play the load and fire frames | — | ❌ | siege | |
| Your own sit: sit down, sitting, stand up clips; moving stands you up | sits on `/sit` and the quit timer; shown to others (0xA9) | ✅ | | |
| Other players' postures from 0xA9: sitting, swimming, dead, riding, climbing | flags read, not shown (audit B3) | ❌ | | |
| Emote picker window (`emoticon_window`) | — | ❌ | | |

## Chat: what to test

M6 counted chat done when a /say went out and replies came back. What retail chat does, to check one by one
(owner, live):

- [ ] /say, /yell (if the server has it), /emote and `:` emotes
- [ ] /send (/tell) out and in; reply to the last tell
- [ ] /group, /guild, /officer, /alliance (needs a group/guild)
- [ ] /broadcast (region), /lfg, /trade, /advice channels; joining and leaving chat channels (/cg, /bg)
- [ ] System, combat, loot, spell, item and merchant messages: each shows in its colour
- [ ] Screen-centre messages (type 0xC8–0xCB): mid-screen since 2026-11-13 (0xCA/0xCB also in chat as Say); check one live
- [ ] Long lines wrap; [keywords] in NPC speech are clickable
- [ ] Enter starts chatting and returns control to the game after sending

Built later: several chat windows with their own filters, default channel, font size and transparency
(retail's `context_window`).

## Screen size and UI scale

See roadmap M9, "Screen and UI scale", which records retail's behaviour (one UI scale from 1.0 to 4.0
over a virtual screen; absolute window positions saved per character; opacity per window; a Toggle
Interface key) and our plan.

**Interface size (2026-11-10):** Settings → Display → Interface size (1.0–4.0, Auto = screen height ÷ 720: 1.5 at 1080p) and retail's top-left-corner drag (daoc-re RE/63) scale every window we draw, live. Window places are kept relative to the nearest screen edge (retail keeps the top-left corner only).
