# Test fixtures

## rustdaoc_login_20260714.bin

A recorded login session for DAoC client **1.127e**, from
[RustDAoC](https://github.com/latentcollapse/RustDAoC) (GPL-3.0-or-later), file
`crates/caer-world/tests/fixtures/rustdaoc_login_20260714.bin` at commit `ee3689ba66c8`.
Used here under the same license, unmodified.

- SHA-256: `8e9f0bc52f9daee0f187655ff3d6f71fae128fc29eb9c80e890c6ee57e6e2374`
- Format: a sequence of records `[u8 direction][u64 BE ms][u32 BE length][bytes]`.
  Direction 1 = client→server, 2 = server→client. This file holds two records, one whole TCP
  stream per direction: 171 client packets and 1,886 server packets.
- Account and password in the stream are both `rustdaoc` (a test account).
- Unknown: whether the retail client or RustDAoC's own client produced it. It is unencrypted
  and every client packet carries a valid checksum.
