# Code review, November 2026

A quality review of the whole repository (protocol library, asset pipeline, Godot client, tools),
started 2026-11-05: what was found, what is done, and what is left. Pick the work up from
"Next" below; mark items done here as they land, and delete this file once nothing is open.

## Done (2026-11-05, all pushed, CI green)

| Commit | What |
|---|---|
| 7bcc1e6 | Tests over the owner's client files are reported as skipped when the files are missing (`[LiveFact]`/`[LiveTheory]`, `tests/DaocAssets.Tests/LiveFixture.cs`); `DAOC_REQUIRE_FIXTURES=1` makes them fail instead. CI (`.github/workflows/ci.yml`): build and test on every push, under a German locale. |
| 477a487 | Protocol robustness: receive-loop errors reach the reader, a started send can't be cancelled halfway, `DisposeAsync` is idempotent, a failed jump returns to `InWorld`, `ClientSession.AwaitingReply`, the login replies parse through `PayloadReader`. |
| f463c61 | Godot: `zone.csv` parsed with the invariant culture, model templates freed on zone change, fire-and-forget tasks go through `Fire()`, status text 4 Hz. |
| be64f2d | MPK and PCX readers reject sizes the file can't hold; `TextureResolver` indexes archive members once. |
| e6edb11 | Quit: the character sits out the timer, moving stands it up (0xC7 `00`) and calls the quit off; a typed `/quit` runs the client's own quit; a 0xA4 the client wasn't waiting for finishes the quit. See open question L13. |
| f66c67c | First CI run caught a Linux-only bug: texture copies now take the real file name; install files are matched in any case. |
| 82d585e | The client reads `zone.csv`, `water.csv` and the figure tables with the code that writes them (`ZoneCheck.ReadManifest`, `ZoneWater.ReadCsv`, `FigureTable`); the Godot project references DaocAssets. Fixed: a water body whose name has a comma lost its texture and flow. |
| c3d416d | One glTF loader (`ModelTexturing.LoadGlb`) instead of seven copies; shots refresh the status text per pose. |
| d7ce38e | `WorldView` split into partial-class files by subject (`.Zone`, `.Player`, `.Entities`, `.Chat`, `.Inspect`, `.Shots`). Moved code only. |
| 3c5d984 | Small fixes: DaocCli and command-line argument errors, an unlisted realm, a FakeServer race, `reconvert-zone.sh` no longer hides errors, Keychain exposure documented. |
| 2bc30e5 | `NetworkBridge` moved into `DaocProtocol.Session` (no Godot), with tests over the recorded session. |
| d8f7d0a | `CharacterScreen` split into partial-class files (`.Ui`, `.Profiles`, `.World`, `.CommandLine`). Moved code only. |

Godot changes were checked with `tools/shots.sh` before and after (pixel comparison: the scene is
unchanged; only the live counters in the status and layer panels differ between runs).

## Next

### 1. Verify the quit timer live (needs a player account)

The usual test account is a GM, and OpenDAoC skips the quit timer for GMs, so the sit-and-cancel
path (e6edb11) has not been seen live. On a **player** account (PrivLevel 1: a newly created
account, or `UPDATE account SET PrivLevel = 1 WHERE Name = '<account>';` on the server's database
and back to 2 or 3 afterwards, since Travel needs GM rights):

1. Type `/quit` and stand still: the character screen after about 20 s.
2. Type `/quit`, press W within the 20 s: "Quit cancelled: you moved." in the chat; play on.
3. Click Quit afterwards: the character screen again.
4. Press Quit while running: the quit is called off at once (the server refuses to start one while
   moving).

Record the result in `docs/protocol.md` ("Logging out", now marked "from source, not yet seen
live") and open question L13.

### 2. Work that needs the owner's client install

- **Remove the old NIF 10.1 fallback scan** (`NifFile.cs`, `ReadGamebryo1010` and its
  `TryRead*1010` helpers): it runs only when the tree reader fails (one file in the install, per
  `docs/nif-coverage.md`), repeats `Gamebryo1010`'s geometry reading, and tries every byte offset
  on a bad file. Report that file as unsupported instead.
- **One geometry/strip decoder** instead of three (`NifFile` 4.x, `Gamebryo1010`, `NifSurvey`).
- Both are NIF reader changes: rerun `mpk audit <client-dir> --out private/audit/nif-audit.md`
  afterwards and keep its integrity section clean (CLAUDE.md rule), then `make check-zones` and
  `tools/shots.sh`.
- **Sit animation** for the quit: the converter exports only idle, walk, back, run and the combat
  idles (`MonsterFigure.ClipColumns`). Find the sit clip's column in `anims.csv`, export it, and
  play it while `WorldView` is quitting.
- **Measure before caching archives in `AssetIndex`**: `Read` reopens the archive for every asset.
  Time a zone conversion; keep archives open (making `AssetIndex` disposable, about nine call sites
  in `AssetExtract`) only if it is slow.
- Run the fixture tests with the private fixtures present (`DAOC_REQUIRE_FIXTURES=1 dotnet test`):
  the 28 live tests have not run since they were changed to report skips.

### 3. Lower priority

- Split `tools/AssetExtract/Program.cs` (about 1,500 lines): one file per command, one option
  parser (`--size`/`--offset` parsing is copied), and the zone size passed as a value instead of the
  mutable static `ZoneSize`.
- `DaocConnection`'s inbound channel is unbounded (about 23 packets/s in the world): bound it, or
  document that the game loop must keep draining.
- `ClientSession.MoveToAsync`: the cancellation token comes before the optional `stateFlags`.
- More protocol tests: `ChangeRegionAsync` called directly, `QuitAsync` when the server ends the
  game (`LeavesGame`), `SendCommandAsync`'s `/`, `&` and bare forms.
