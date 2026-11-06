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
| f229cfa | The old NIF 10.1 byte-offset fallback scan is gone: a 10.1 file that doesn't decode to the footer is reported as unsupported (only `dr_demonroom2b`, a Darkness Falls room with a particle system; see `docs/nif-coverage.md`). One geometry decoder (`NifGeometry`, 4.x and 10.1) for the converter and the audit. Checked over the whole install: every other NIF reads to identical geometry (hash of vertices, normals, colours, UVs, triangles), the audit report is unchanged, and a full `make check-zones` reconversion left all 5,507 outputs byte-identical. The 28 live fixture tests ran and pass (`DAOC_REQUIRE_FIXTURES=1`). |
| f7a79bf | Quit timer verified live on a player account (`DaocCli --quit-checks`, `eodtest2`): all four cases pass. Moving stands the character up on the server, so the client no longer sends 0xC7 after moving (it drew "You are not sitting!"), and a jump in place no longer cancels the client's quit while the server's goes on. Open question L13 resolved. |
| 69b7cbb | Send rates set to retail 1.127's, from a retail session against our server: 0xA9 every 2 s standing still and every 0.2 s while moving, 0xA3 every 5 s. The keep-alive sends 0xA9 only when nothing else has for 2 s, and pings on their own clock. The Godot client's send-at-once check had fired every other frame while moving (about 30 a second). Verified live with `DaocCli` (2.00 s, 5.00 s, every ping answered); the Godot moving rate is not yet checked live. Open questions R3, R4 resolved. |

Godot changes were checked with `tools/shots.sh` before and after (pixel comparison: the scene is
unchanged; only the live counters in the status and layer panels differ between runs).

## Next

### 1. Work that needs the owner's client install

- **Sit animation** for the quit: the converter exports only idle, walk, back, run and the combat
  idles (`MonsterFigure.ClipColumns`). Find the sit clip's column in `anims.csv`, export it, and
  play it while `WorldView` is quitting.
- **Measure before caching archives in `AssetIndex`**: `Read` reopens the archive for every asset.
  Time a zone conversion; keep archives open (making `AssetIndex` disposable, about nine call sites
  in `AssetExtract`) only if it is slow.

### 2. Lower priority

- Split `tools/AssetExtract/Program.cs` (about 1,500 lines): one file per command, one option
  parser (`--size`/`--offset` parsing is copied), and the zone size passed as a value instead of the
  mutable static `ZoneSize`.
- `DaocConnection`'s inbound channel is unbounded (about 23 packets/s in the world): bound it, or
  document that the game loop must keep draining.
- `ClientSession.MoveToAsync`: the cancellation token comes before the optional `stateFlags`.
- `JumpWithinTheRegionTakesOverTheServersPosition` failed once in about 20 runs: it reads
  `server.Received` right after closing the connection, so the last 0xA9 may not have arrived yet.
- More protocol tests: `ChangeRegionAsync` called directly, `QuitAsync` when the server ends the
  game (`LeavesGame`), `SendCommandAsync`'s `/`, `&` and bare forms.
