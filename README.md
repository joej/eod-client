# eod-client

An open-source client for Dark Age of Camelot: a C# protocol library with no engine
dependency, and a Godot 4 (.NET) front end, tested against the
[OpenDAoC](https://github.com/OpenDAoC/OpenDAoC-Core) server emulator.

**Status: early.** Not ready for others to run yet: no releases and no setup instructions.
Progress is logged at [eveofdarkness.org](https://eveofdarkness.org), named for Ben Falk's Eve
of Darkness server (see Acknowledgements).

## Progress

Tested against our own OpenDAoC server (version pinned in `server/`):

- **Protocol library** (`src/DaocProtocol`, C#, no engine dependency): packet framing and checksum,
  login, the character screen (list, create, delete), entering the world, keep-alive, and a clean
  logout.
- **Godot client** (`godot/`): log in, list characters, enter the world, and show the character
  as a capsule at the position the server sends. It stays connected (sessions of 5+ minutes) and
  quits back to the character screen cleanly.
- **Tests** (`tests/`): checksum and framing checked against a recorded 1.127 session, packet
  builders and parsers, and a fake server that replays recorded replies.
- **Command-line tool** (`tools/DaocCli`): the same steps without a window, for testing.

No terrain, models, other players or movement yet.

## Scope

- Client protocol version **1.127** only (see [Versions](https://eveofdarkness.org/versions/)).
- No game assets are included or distributed. Anything that needs game data will load it from
  your own DAoC installation.

## Documentation

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
