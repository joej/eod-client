# Controls: the original client's key bindings

Reference for the controls review (roadmap M8, open question W41). The client's keys are configurable
(Options → Configure Keyboard, or `/keyboard`), with presets the player picks from (the original manual names
RPG and FPS; darkageofcamelot.com's current page names RPG, FPS and "MMO Default").

## Sources

- The original US game manual (2001), "The Commands", pages 42-45: the default layout, with a second
  layout's key in parentheses. Facts only are listed here, not the manual's text.
- darkageofcamelot.com, "Control and Movement" (current): W/S forward and back, A/D turn, Q/E strafe,
  Space jump, NumLock auto-run, Z dive, V sprint; mouse: left-drag pans the camera, right button looks
  where you run. That is the layout most players use today.

## Retail's shipped presets (keyboard.dat, read by daoc-re 2026-11-06)

The player's own bindings (user.dat) come first; keyboard.dat's preset only fills actions left unbound.

| Action | RPG | MMO | FPS |
|---|---|---|---|
| Forward / back | W / S | W / S | E / D |
| Turn left / right | A / D | Num4 / Num6 | Num4 / Num6 |
| Strafe left / right | Q / E | A / D | S / F |
| Jump / up | Space | Space | Space |
| Down (dive) | Z | Q | Z |
| Walk (toggle) | Scroll Lock | K + a modifier (likely Shift) | K + a modifier |

Speeds (daoc-re's capture, 2026-11-05): run 191 units/s; backward, strafe and swim 85; backward is sent as a
negative speed with no flag. Walking's speed is still to be captured.

## Today's layout (owner's reference, 2026-11-06)

| Action | Key |
|---|---|
| Move forward / backward | W / S |
| Turn left / right | A / D (or strafe, by layout choice) |
| Strafe left / right | Q / E |
| Jump | Space |
| Dive | Z |
| Sprint | V |
| Auto-run | NumLock |
| Follow target | Shift+F |
| Stick to target (melee) | Shift+V |
| Face target | F |
| Pan camera | left-drag |
| Align camera / mouse-look | right-drag |
| Save the current camera as this character's home | `/setcamera` |
| Reset camera to home | Home (classic layout: D); `/resetcamera` sets home to 240 units, height 10, level |
| Spells, champion and master-level skills | K |
| Melee combat styles | L |
| Realm abilities, crafting, other skills | P |
| Options / configuration | Esc |

Quickbars: up to three visible, each with 10 pages of 10 buttons, drag and drop; `/qbind` binds a slot to a
key. Not in this list: a walk toggle (the 2001 manual's F is now "face target"), sit, attack mode, consider.

## Default layout, 2001 (manual; alternative in parentheses)

| Action | Key |
|---|---|
| Move forward / backward | Up / Down arrow; right / left mouse button |
| Turn left / right | Left / Right arrow |
| Strafe left / right | Q (Z) / W (X); a strafe toggle is unset |
| Jump / up | A (Space) |
| Down (swimming) | Z (D) |
| Walk (toggle walk and run) | F (W) |
| Run lock (auto-run) | NumLock |
| Sprint | V |
| Sit / stand | C (S) |
| Look up / down | S (PgUp) / X (PgDown) |
| Reset camera | D (Home) |
| Pan camera | Tilde |
| Mouselook | hold Ctrl; toggle M |
| Attack mode | F6 (A) |
| Consider | N (C) |
| Nearest enemy / object / friend | F8 / F7 / F9 |
| Nearest loot | H |
| Last attacker | F10 |
| Target group member 1-8 | Shift+F1 ... Shift+F8 (F1 ... F6, Shift+F1, Shift+F2) |
| Ground target | F5 (Shift+T) |
| Select / open | Space (U) |
| Get item | G |
| Use item / use item 2 | F4 (E) / Shift+U |
| Sell / destroy item | Shift+S / Shift+D |
| Torch | T |
| Toggle names | Shift+N (N) |
| Follow / stick / face | Shift+F / Shift+G / Shift+H (Shift+E) |
| Inventory | I |
| Status / group / spells / skills / combat | P (O) / O (P) / U (Shift+I) / [ (Shift+O) / Y (Shift+P) |
| Reply | R |
| Chat log page up / down | PgUp / PgDown |
| System log page up / down | Shift+PgUp (Shift+Insert) / Shift+PgDown (Shift+Delete) |
| Command window | Colon (Y) |
| Chat logging | L |
| Help | F1 (Shift+H) |
| Performance meter | Shift+P (Shift+X) |
| Compass | Shift+C |
| Information / delve | Shift+I (Shift+N) |
| Craft / salvage | Shift+R |
| Right-hand / two-handed / ranged weapon | , / (not legible in the text) / B |

Quick binds: any free key can be bound to a quickbar slot (`/qbind bar slot`, `/qunbind`, `/qbind` lists
them), saved per character.

## Our client today

Presets RPG (default), MMO, FPS and classic (the 2001 layout), chosen in Settings → Keyboard (login screen or F10 in game), with your own keys kept over the layout as retail keeps user.dat's (click a key, press the new one; Backspace: no key; a key taken from another action leaves it).
Backing up and strafing at 85 of 191 (scaled with the server's speed), backward sent negative; V sprint
(`/sprint`); NumLock auto-run (forward or back stops it); left-drag pans the camera round the character, a click
inspects, right-drag turns and re-aligns; mouse wheel zoom; a camera home per character, as retail's: `/setcamera` saves the
distance, height, angle and tilt, `/resetcamera` sets retail's 240 / 10 / 0 / 0, Home (classic: D) returns to it, and
entering the world starts there (a character without one starts at 500 units, height 10, level);
H attack mode (retail: F6 or A by layout, W34); Tab free-fly camera (ours); Enter or `/` to chat. No walk
toggle, auto-run, consider or targeting keys yet.
