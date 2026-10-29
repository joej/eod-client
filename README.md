# eod-client

An open-source client for Dark Age of Camelot: a C# protocol library with no engine
dependency, and a Godot 4 (.NET) front end, tested against the
[OpenDAoC](https://github.com/OpenDAoC/OpenDAoC-Core) server emulator.

**Status: early.** Not ready for others to run yet: no releases and no setup instructions.
The protocol flow works end to end and a textured world renders, but the character does not yet
move under its own control and the world is not yet live (no other players, NPCs or chat). See
[`docs/roadmap.md`](docs/roadmap.md) for where this is and where it's going. Progress is logged at
[eveofdarkness.org](https://eveofdarkness.org), named for Ben Falk's Eve of Darkness server (see
Acknowledgements).

## Progress

Tested against our own OpenDAoC server (version pinned in `server/`); ~110 tests across both
libraries.

- **Protocol library** (`src/DaocProtocol`, C#, no engine dependency): packet framing and checksum,
  login, the character screen (list, create, delete), entering the world, keep-alive, and a clean
  logout — verified byte-for-byte against a recorded 1.127 session and live against OpenDAoC.
- **Asset pipeline** (`src/DaocAssets` + `tools/AssetExtract`): reads the client's own MPK/NPK
  archives and converts its NIF models to glTF (static meshes — buildings, items, creatures in
  bind pose) in metres, resolves textures, places a zone's static objects, and builds real terrain
  from the heightmap. A convert-once asset library caches the results, with an overrides layer for
  author-made content.
- **Godot client** (`godot/`): log in, list characters, enter the world, and show the character at
  the server's position. Given converted assets it renders a full textured zone — terrain plus the
  zone's buildings. Sessions hold 5+ minutes and quit cleanly. A standalone model viewer orbits any
  converted model.
- **Tests** (`tests/`): framing/checksum against the recorded session, packet builders and parsers,
  a fake server that replays recorded replies, and the asset readers (MPK, NIF, textures, zone,
  terrain) against golden fixtures.
- **Command-line tools** (`tools/`): `DaocCli` runs the login→world→quit flow without a window for
  testing; `AssetExtract` (`mpk`) is the asset converter CLI; an EoD `Launcher` (Windows, built but
  not yet validated) for pointing a real client at a server.

**Not yet:** self-driven movement, other players/NPCs, chat, combat, inventory, and animated
character models. Those are the next milestones in [`docs/roadmap.md`](docs/roadmap.md).

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
