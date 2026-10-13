# eod-client

An open-source client for Dark Age of Camelot: a C# protocol library with no engine
dependency, and a Godot 4 (.NET) front end, tested against the
[OpenDAoC](https://github.com/OpenDAoC/OpenDAoC-Core) server emulator.

**Status: early.** Nothing to run yet. The first milestone is a client that logs in, lists
characters, and stands in the world at the right coordinates. Progress is logged at
[eveofdarkness.org](https://eveofdarkness.org).

## Scope

- Client protocol version **1.127** only (see [Versions](https://eveofdarkness.org/versions/)).
- No game assets are included or distributed. Anything that needs game data will load it from
  your own DAoC installation.

## Layout

- `server/`: a pinned OpenDAoC test server (podman or Docker compose). Run
  `REGION_IP=<address your client uses> ./setup.sh` once, then `./up.sh`.
- `CLAUDE.md`: working notes on the protocol, with sources for each claim.

The protocol library, tests, and Godot project are coming next.

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
