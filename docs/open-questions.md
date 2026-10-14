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
| C3 | Delete with a non-zero realm byte: the server subtracts the realm's slot offset twice. For Midgard/Hibernia the result (190–199, 280–289) matches no character, so nothing is deleted and, per C2, nothing is answered. Albion's offset is 0, so Albion is unaffected. A server comment says the real client sends no realm on delete, which avoids it. | OpenDAoC | Open: confirm what a retail client sends. History in C11. |
| C4 | The create handler doesn't check the invalid-names list (`invalidnames.txt`); only the separate name-check requests (0xCB, 0xC3) do. A client that skips the check can create a reserved name. | OpenDAoC | Open. Verify live with a harmless entry from the list; likely worth an OpenDAoC issue. |
| C5 | Duplicate names across accounts: the create handler only checks the creating account; the database column is unique. What does the client get when the insert fails? | OpenDAoC | Open. Test live. |
| C6 | A name failing `^[A-Z][a-zA-Z]` or shorter than 3 gets a player kicked, or banned with `ban_hackers` on (off by default). The pattern only checks the first two characters. Retail rules and penalty unknown. | OpenDAoC (inherited from DOLSharp) | Open. |
| C7 | Create packet tail: OpenDAoC reads "new constitution" 43 bytes after the stats and no region byte; RustDAoC writes region at +40 and new constitution at +44. | OpenDAoC vs RustDAoC | Open. Settle with a retail client capture of a create. |
| C8 | Race/gender byte 0x13 in the recorded overview: bit 0x10 isn't produced by OpenDAoC's encoding. | RustDAoC capture | Open. The recording's server may not be stock OpenDAoC. |
| C9 | Login reports server type 7 ("all realms" in the server's comments), but the overview's all-realms flag is 0 and choosing a realm was saved on the account. | live | **Resolved 2026-10-14.** `PacketLib175.SendLoginGranted` sends type 1 when `allow_all_realms` is on (or for GM/admin accounts), otherwise the rules' colour, which is 0 on a normal server; `PacketLib1127` turns 0 into 7, commented as a workaround for character-screen bugs. Verified live: type 7 with the setting off, 1 with it on. The overview flag (bit 0) shows the setting: 0x1 with it on, but 0x0 on an account with no characters (16-byte empty overview). |
| C10 | 0x10 CharacterSelectRequest with payload `00` is ignored; the server answers with 0x2A LoginGranted again plus 0x28. | OpenDAoC, live | Open. Why does the 1.127 client send it before the overview? |
| C11 | Suspected: on an all-realms server the account's realm stays 0, so deletes would never match. | OpenDAoC (reading) | **Refuted 2026-10-14.** I'd read the pre-1.124 overview path. The 1.124+ path (`_HandlePacket1124`) sets the account's realm to whichever realm page the client last requested, all-realms or not, and delete converts the slot with that realm. Live with `allow_all_realms` on, a fresh account created and deleted an Albion character fine (account realm saved as 1). Midgard/Hibernia not tested. History: DOLSharp PR #243 (2020-09-07) derived the realm from the slot instead; OpenDAoC commit 87d0ec8 (2022-07-05) replaced that with the current approach. Residual: a delete for a slot outside the last-viewed realm page silently does nothing (a real client deletes from the page on screen). No upstream report needed. |

## Login and session

| # | Oddity | Source | Status / next step |
|---|---|---|---|
| L1 | Our 0xF4 client-type byte 0x36 (copied from the capture) is logged as "LabyrinthOfTheMinotaur" plus add-on bits. What does a retail 1.127e install send? | live | Open. Retail client capture. |
| L2 | The 1.127e client sends a second 0xF4 with a 256-byte key block; OpenDAoC ignores it. | RustDAoC capture | Open. |
| L3 | 0xA7 has a 13-byte trailer (`2a`, u32 8, 8 bytes) the server doesn't read. Client id? | RustDAoC capture | Open. |
| L4 | The 1.127 client sends 0x9D and 0xE8 before 0xBF; ClientSimulator (1.124) never sends 0x9D. | RustDAoC capture | Open. Confirm with our own capture. |
| L5 | Idle/link-death timeout: the original plan's ~30 s figure is unverified. | plan | **Answered from source 2026-10-14** (OpenDAoC): no 0xA9 for 5 s → link death (`PlayerMovementComponent.SOFT_LINK_DEATH_THRESHOLD`); no 0xA3 for 150 s → disconnect (`ClientService.HARD_TIMEOUT`); idle kick to the character screen only if `kick_idle_player_status` is on (default off, 60 min). A link-dead character stays in the world 60 s, and logging in meanwhile is refused with AccountIsInLogoutProcedure (seen live). Retail values unknown. |
| L6 | 0x9D RegionListRequest: the recorded client sends the slot plus 28 bytes (`01 66 4c 21 5f 40 1e 1e 00 ff ff ff ff 0f 03 77 23 01 70 de 10 00 00 05 2d 00 00 08`); the server reads only the slot. | RustDAoC capture | Open. Machine or client info? We send the slot alone. |
| L7 | 0xA9 from the recorded client: object ID always 0 (the server skips it), heading sometimes has bit 0x1000 set, and the action byte is 0x80 (the server's enum calls it "torch"). | RustDAoC capture | Open. Retail client capture. |
| L8 | 0x10 is never sent with a character name on 1.127; the character is chosen by slot in 0x9D and 0xD4 (which the server converts with the account's current realm, like delete). | RustDAoC capture, OpenDAoC | Noted. The original plan's "0x10 with the chosen name" was 1.124-era. |

## Not yet documented anywhere we've found

- UDP (port 10400): framing, update rate, when the client uses it.
