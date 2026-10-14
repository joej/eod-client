# Open questions and oddities

Things we noticed that look odd, are unconfirmed, or may differ between OpenDAoC and the retail
(Mythic / EA / Broadsword) servers. Resolving them adds to what the community knows about the
protocol. Add an entry whenever something surprises us; close it with the evidence that settled it.

**Sources:** `OpenDAoC` = OpenDAoC-Core 2026.09.36 (f0aaa1e) source; `live` = our tests against
that server; `RustDAoC capture` = the recorded 1.127e session in `tests/DaocProtocol.Tests/Fixtures`.

**Ways to resolve:**
- **Retail client capture**: run a real 1.127 client against our OpenDAoC and record it.
  This settles what the *client* sends.
- **Retail server comparison**: compare against the live servers Broadsword still runs. This is
  the only way to settle what a *retail server* replies. It needs their encryption handled and
  a check of their terms of service first, so it's a deliberate decision, not a default step.
- **Historical logs**: PacketLogConverter's decoders and old DaocLogger logs came from retail traffic.
- **Community**: OpenDAoC Discord, DOL forums.

## Character screen

| # | Oddity | Source | Status / next step |
|---|---|---|---|
| C1 | A rejected create (bad stats, class/race, disabled class) gets the same 0x2A reply as a successful one; the reason is only in the server log. There is no "create failed" packet in the server's opcode list. Did retail send an explanation (dialog, message)? | OpenDAoC, live | Open. Retail behaviour unknown. |
| C2 | Deleting an empty slot gets no reply at all; a client waiting for one hangs. | OpenDAoC | Open. Confirm live; what did retail do? |
| C3 | Delete with a non-zero realm byte: the server subtracts the realm's slot offset twice. For Midgard/Hibernia the result (190–199, 280–289) matches no character, so nothing is deleted and, per C2, nothing is answered. Albion's offset is 0, so Albion is unaffected. A server comment says the real client sends no realm on delete, which would avoid it. | OpenDAoC | Open. Confirm what a retail client sends; if it sends a realm, it's an OpenDAoC bug worth reporting. |
| C4 | The create handler doesn't check the invalid-names list (`invalidnames.txt`); only the separate name-check requests (0xCB, 0xC3) do. A client that skips the check can create a reserved name. | OpenDAoC | Open. Verify live with a harmless entry from the list; likely worth an OpenDAoC issue. |
| C5 | Duplicate names across accounts: the create handler only checks the creating account; the database column is unique. What does the client get when the insert fails? | OpenDAoC | Open. Test live. |
| C6 | A name failing `^[A-Z][a-zA-Z]` or shorter than 3 gets a player kicked, or banned with `ban_hackers` on (off by default). The pattern only checks the first two characters. Retail rules and penalty unknown. | OpenDAoC (inherited from DOLSharp) | Open. |
| C7 | Create packet tail: OpenDAoC reads "new constitution" 43 bytes after the stats and no region byte; RustDAoC writes region at +40 and new constitution at +44. | OpenDAoC vs RustDAoC | Open. Settle with a retail client capture of a create. |
| C8 | Race/gender byte 0x13 in the recorded overview: bit 0x10 isn't produced by OpenDAoC's encoding. | RustDAoC capture | Open. The recording's server may not be stock OpenDAoC. |
| C9 | Login reports server type 7 ("all realms" in the server's comments), but the overview's all-realms flag is 0 and choosing a realm was saved on the account. | live | Open. Check which setting drives each. |
| C10 | 0x10 CharacterSelectRequest with payload `00` is ignored; the server answers with 0x2A LoginGranted again plus 0x28. | OpenDAoC, live | Open. Why does the 1.127 client send it before the overview? |

## Login and session

| # | Oddity | Source | Status / next step |
|---|---|---|---|
| L1 | Our 0xF4 client-type byte 0x36 (copied from the capture) is logged as "LabyrinthOfTheMinotaur" plus add-on bits. What does a retail 1.127e install send? | live | Open. Retail client capture. |
| L2 | The 1.127e client sends a second 0xF4 with a 256-byte key block; OpenDAoC ignores it. | RustDAoC capture | Open. |
| L3 | 0xA7 has a 13-byte trailer (`2a`, u32 8, 8 bytes) the server doesn't read. Client id? | RustDAoC capture | Open. |
| L4 | The 1.127 client sends 0x9D and 0xE8 before 0xBF; ClientSimulator (1.124) never sends 0x9D. | RustDAoC capture | Open. Confirm with our own capture. |
| L5 | Idle/link-death timeout: the original plan's ~30 s figure is unverified. | plan | Open. Find in OpenDAoC; measure live. |

## Not yet documented anywhere we've found

- UDP (port 10400): framing, update rate, when the client uses it.
