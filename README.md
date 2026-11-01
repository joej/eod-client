# eod-client

An open-source client for Dark Age of Camelot: a C# protocol library with no engine
dependency, and a Godot 4 (.NET) front end, tested against the
[OpenDAoC](https://github.com/OpenDAoC/OpenDAoC-Core) server emulator.

**Status: early.** Not ready for others to run yet: no releases and no setup instructions.
You can log in, walk your character across a textured DAoC zone, see NPCs and other players move,
and chat. Characters and creatures are still capsules: real animated models are the next milestone
(M7), and the one that reaches the "walk the DAoC world" MVP. See
[`docs/roadmap.md`](docs/roadmap.md) for where this is and where it's going. Progress is logged at
[eveofdarkness.org](https://eveofdarkness.org), named for Ben Falk's Eve of Darkness server (see
Acknowledgements).

## Progress

Tested against our own OpenDAoC server (version pinned in `server/`); about 150 tests across both
libraries.

- **Protocol library** (`src/DaocProtocol`, C#, no engine dependency): packet framing and checksum,
  login, the character screen (list, create, delete), entering the world, keep-alive, movement, the
  in-world object packets (NPC and player create, update, remove), chat, and a clean
  logout — verified byte-for-byte against a recorded 1.127 session and live against OpenDAoC.
- **Asset pipeline** (`src/DaocAssets` + `tools/AssetExtract`): reads the client's own MPK/NPK
  archives and converts its NIF models to glTF (static meshes — buildings, items, creatures in
  bind pose) in metres, resolves textures, places a zone's static objects, and builds real terrain
  from the heightmap. It applies the retail client's rules for hidden helper geometry (collision
  hulls, climb volumes, far LOD levels), so buildings render the way the game draws them. A convert-once asset library caches the results, with an overrides layer for
  author-made content.
- **Godot client** (`godot/`): log in, list characters and enter the world. Given converted assets
  it renders a full textured zone (terrain plus the zone's buildings). You walk, turn and strafe with
  a chase camera, following the terrain, and the server accepts the movement. NPCs and other players
  appear and move in real time (as capsules for now). A chat log shows say, system and channel
  messages, and `/say` and other slash commands go to the server. A development overlay toggles
  layers, switches to wireframe, and inspects any surface's model and material on click. A standalone
  model viewer orbits any converted model.
- **Tests** (`tests/`): framing/checksum against the recorded session, packet builders and parsers,
  a fake server that replays recorded replies, and the asset readers (MPK, NIF, textures, zone,
  terrain) against golden fixtures.
- **Command-line tools** (`tools/`): `DaocCli` runs the login→world→quit flow without a window for
  testing; `AssetExtract` (`mpk`) is the asset converter CLI; an EoD `Launcher` (Windows, built but
  not yet validated) for pointing a real client at a server.

**Not yet:** animated character and creature models, foliage transparency, solid buildings
(collision beyond the terrain), vitals and targeting, combat and inventory. Those are the next milestones in [`docs/roadmap.md`](docs/roadmap.md).

## Scope

- Client protocol version **1.127** against the **OpenDAoC** server (encryption off); see
  [Versions](https://eveofdarkness.org/versions/). Supporting the current official **1.130** client
  is a later goal of the same project (see [`docs/roadmap.md`](docs/roadmap.md)); 1.130 protocol
  research is already underway on the server/analysis side.
- No game assets are included or distributed. Everything that needs game data loads it from your
  own DAoC installation and is converted locally; converted assets never enter the repo or a release.

## Documentation

- [`docs/roadmap.md`](docs/roadmap.md): what's done and the stepwise milestones toward a minimally
  playable client.
- [`docs/protocol.md`](docs/protocol.md): the packets we use, field by field, and how each was
  verified.
- [`docs/open-questions.md`](docs/open-questions.md): oddities and unconfirmed behaviour, with
  sources and next steps.
- `CLAUDE.md`: working notes, with sources for each claim.
- `server/`: a pinned OpenDAoC test server (podman or Docker compose).

## Acknowledgements

- **[Ben Falk](https://github.com/benfalk)**'s [Eve of Darkness](https://github.com/Eve-of-Darkness/eve-of-darkness),
  a DAoC server in Elixir, inspired this effort. Its tested packet definitions are among the clearest
  public descriptions of the login protocol. eveofdarkness.org was its home page; we are not that
  project and are not affiliated with it.
- **[BifrostTitan](https://github.com/BifrostTitan)**'s work on a UE5 DAoC client and asset pipeline,
  and his [DAoC-Client-Lib](https://github.com/BifrostTitan/DAoC-Client-Lib), showed what a modern
  client can look like.
- [OpenDAoC](https://github.com/OpenDAoC/OpenDAoC-Core), [Dawn of Light](https://github.com/Dawn-of-Light/DOLSharp)
  and [RustDAoC](https://github.com/latentcollapse/RustDAoC): the server code and recorded session
  every layout here is checked against.
- Developed with [Claude Code](https://claude.com/claude-code); `CLAUDE.md` holds the shared
  working notes it reads.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
