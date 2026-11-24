#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Packet coverage: every packet OpenDAoC can send a 1.127 client, and every one it accepts from it, against ours.

    tools/packetcoverage.py <OpenDAoC tree> [--out docs/packet-coverage.md] [--tiers tools/packet-coverage-tiers.txt]

Server side, from the OpenDAoC source (GameServer/packets):
  - S->C: each eServerPackets code, the Send* methods of the 1.127 PacketLib chain (PacketLib1127 down to PacketLib168
    and AbstractPacketLib, most-derived override wins) that write it, and how many call sites each method has in the
    rest of GameServer (0: nothing in the game sends it).
  - C->S: each eClientPackets code with a [PacketHandler] and the client state it needs.
Our side, from src/ and godot/scripts:
  - S->C: decoded when NetworkBridge.Dispatch or ClientSession reads it; shown when an event it raises has a
    subscriber under godot/scripts.
  - C->S: sent when something calls SendAsync with its code.
Each packet gets a tier and feature from the tiers file (hand-kept); a packet with none is listed as unclassified, so a
rerun after a server or client change shows what is new.
"""

import argparse
import pathlib
import re
import sys
from collections import defaultdict

ROOT = pathlib.Path(__file__).resolve().parent.parent


def read(p):
    return p.read_text(encoding="utf-8", errors="replace")


def enum(text, name):
    m = re.search(r"enum\s+" + name + r"\s*:\s*byte\s*\{(.*?)\}", text, re.S)
    out = {}
    for n, v in re.findall(r"(\w+)\s*=\s*(0x[0-9A-Fa-f]+)", m.group(1)):
        out[n] = int(v, 16)
    return out


def bodies(text):
    """(method name, body) for each method definition in a C# file, by brace matching."""
    for m in re.finditer(r"(?:public|protected|private|internal)[\w\s<>,\[\]]*?\s(\w+)\s*\([^;{)]*\)\s*\{", text):
        depth, i = 1, m.end()
        while i < len(text) and depth:
            depth += {"{": 1, "}": -1}.get(text[i], 0)
            i += 1
        yield m.group(1), text[m.end():i]


def server(tree):
    pk = tree / "GameServer" / "packets"
    lib = read(pk / "Server" / "IPacketLib.cs")
    s2c, c2s = enum(lib, "eServerPackets"), enum(lib, "eClientPackets")
    # The 1.127 chain: PacketLib1127 -> ... -> PacketLib168 -> AbstractPacketLib.
    chain = []
    cls = "PacketLib1127"
    while cls:
        f = pk / "Server" / f"{cls}.cs"
        chain.append(f)
        m = re.search(r"class\s+" + cls + r"\s*:\s*(\w+)", read(f))
        cls = m.group(1) if m and m.group(1).startswith("PacketLib") else None
    chain.append(pk / "Server" / "AbstractPacketLib.cs")
    # name -> codes. The most-derived definition wins; one that calls base.Name() also writes what its base writes.
    # Overloads in the same file count together (PacketLib1110's SendUpdateIcons(IList, ref int) only calls its other
    # overload, which writes 0x7F).
    methods, open_base, defined_in = {}, set(), {}
    for f in chain:
        for name, body in bodies(read(f)):
            if not name.startswith("Send") or (name in methods and name not in open_base and defined_in[name] != f):
                continue
            defined_in[name] = f
            codes = {s2c[c] for c in re.findall(r"eServerPackets\.(\w+)", body) if c in s2c}
            methods[name] = methods.get(name, set()) | codes
            if re.search(r"\bbase\." + name + r"\s*\(", body):
                open_base.add(name)
            else:
                open_base.discard(name)
    callers = defaultdict(int)
    for f in (tree / "GameServer").rglob("*.cs"):
        if (pk / "Server") in f.parents:
            continue
        for name in re.findall(r"\.(Send\w+)\s*\(", read(f)):
            callers[name] += 1
    # Calls between PacketLib methods count too (a Send that only another Send uses).
    for f in chain:
        for name, body in bodies(read(f)):
            for inner in re.findall(r"\b(Send\w+)\s*\(", body):
                if inner != name:
                    callers[inner] += 1
    writers = defaultdict(list)
    for name, codes in methods.items():
        for c in codes:
            writers[c].append(name)
    # Packets the game writes itself, outside the PacketLib (e.g. the line-of-sight request in LosCheckHandler).
    for f in (tree / "GameServer").rglob("*.cs"):
        if (pk / "Server") in f.parents:
            continue
        for c in set(re.findall(r"eServerPackets\.(\w+)", read(f))):
            if c in s2c:
                name = f"{f.stem}.cs"
                writers[s2c[c]].append(name)
                callers[name] = max(callers[name], 1)
    handlers = {}
    for f in (pk / "Client").rglob("*.cs"):
        for code, state in re.findall(r"\[PacketHandler(?:Attribute)?\(PacketHandlerType\.TCP,\s*eClientPackets\.(\w+)\s*,[^,]*,\s*eClientStatus\.(\w+)", read(f)):
            if code in c2s:
                handlers[c2s[code]] = (code, state, f.stem)
    return s2c, c2s, writers, callers, handlers


def outgoing(type_name, const):
    """A *Request type's codes go to the server, except the replies it waits for (ReplyCode, EndOfBurstCode)."""
    return type_name.endswith("Request") and "Reply" not in const and "EndOf" not in const


def ours():
    src = [p for d in ("src", "godot/scripts") for p in (ROOT / d).rglob("*.cs")]
    codes = {}
    for p in (ROOT / "src" / "DaocProtocol" / "Packets").rglob("*.cs"):
        last = None
        for line in read(p).splitlines():
            m = re.search(r"\b(?:class|record|struct)\s+(?!struct\b|class\b)(\w+)", line)
            if m:
                last = m.group(1)
            if last and re.search(r"const\s+byte\s", line):
                for name, value in re.findall(r"(\w*Code)\s*=\s*(0x[0-9A-Fa-f]+)", line):
                    codes[f"{last}.{name}"] = int(value, 16)
    bridge = read(ROOT / "src" / "DaocProtocol" / "Session" / "NetworkBridge.cs")
    session = read(ROOT / "src" / "DaocProtocol" / "Session" / "ClientSession.cs")
    decoded, events = set(), defaultdict(set)
    helpers = {name: body for name, body in bodies(bridge)}

    def raised(body, depth=0):
        """The events a case raises, itself or through the bridge's own helper methods."""
        found = set(re.findall(r"(\w+)\?\.Invoke", body))
        if depth < 3:
            for call in set(re.findall(r"\b(\w+)\s*\(", body)):
                if call in helpers and call != "Dispatch":
                    found |= raised(helpers[call], depth + 1)
        return found
    ref = r"(?:\w+\.)*?(\w+)\.(\w*Code)\b"   # Type.Code or Namespace.Type.SomeCode
    for m in re.finditer(r"case\s+" + ref + r"\s*:(.*?)(?=\n\s*case\s|\n\s*default\s*:)", bridge, re.S):
        key = f"{m.group(1)}.{m.group(2)}"
        if key in codes:
            decoded.add(codes[key])
            events[codes[key]] |= raised(m.group(3))
    # Sent: a code constant on a line that sends, or one of a *Request type (handed to a sender, e.g. UseRequest's).
    sent = set()
    for p in src:
        for line in read(p).splitlines():
            if "const byte" in line:
                continue
            for t, c in re.findall(ref, line):
                key = f"{t}.{c}"
                if key in codes and ("Send" in line or outgoing(t, c)):
                    sent.add(codes[key])
    by_session = set()
    for line in session.splitlines():
        if "Send" in line:
            continue
        for t, c in re.findall(ref, line):
            if f"{t}.{c}" in codes and not outgoing(t, c):
                decoded.add(codes[f"{t}.{c}"])
                by_session.add(codes[f"{t}.{c}"])
    godot = "\n".join(read(p) for p in (ROOT / "godot" / "scripts").rglob("*.cs"))
    subscribed = {e for e in set().union(*events.values()) if re.search(r"\b" + e + r"\s*\+=", godot)} if events else set()
    # Read by ClientSession: the login, character screen and world entry steps; they drive the session itself.
    shown = {c for c, es in events.items() if es & subscribed} | by_session
    return decoded, shown, sent, events


def tiers(path):
    t = {}
    if path.exists():
        for line in read(path).splitlines():
            line = line.split("#", 1)[0].strip()
            if not line:
                continue
            parts = [s.strip() for s in line.split("|")]
            d, code = parts[0].split()
            t[(d, int(code, 16))] = (parts[1], parts[2] if len(parts) > 2 else "")
    return t


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("tree")
    ap.add_argument("--out", default=str(ROOT / "docs" / "packet-coverage.md"))
    ap.add_argument("--tiers", default=str(ROOT / "tools" / "packet-coverage-tiers.txt"))
    ap.add_argument("--label", default="")
    a = ap.parse_args()
    s2c, c2s, writers, callers, handlers = server(pathlib.Path(a.tree))
    decoded, shown, sent, events = ours()
    tier = tiers(pathlib.Path(a.tiers))
    rows, unclassified = [], []

    def status_in(code):
        if code in shown:
            return "shown"
        return "decoded" if code in decoded else "—"

    for name, code in sorted(s2c.items(), key=lambda kv: kv[1]):
        ws = sorted(writers.get(code, []))
        live = [w for w in ws if callers.get(w, 0) > 0]
        if not ws:
            continue   # in the enum, written by nothing in the 1.127 chain
        t, feat = tier.get(("S", code), ("?", ""))
        if t == "?":
            unclassified.append(f"S 0x{code:02X} {name}")
        rows.append(("S→C", code, name, status_in(code), t, feat,
                     ", ".join(f"{w} ({callers.get(w, 0)})" for w in ws), bool(live)))
    for code, (name, state, f) in sorted(handlers.items()):
        t, feat = tier.get(("C", code), ("?", ""))
        if t == "?":
            unclassified.append(f"C 0x{code:02X} {name}")
        rows.append(("C→S", code, name, "sent" if code in sent else "—", t, feat, f"{f} ({state})", True))

    order = {"0": 0, "P1": 1, "P2": 2, "P3": 3, "R": 4, "dev": 5, "-": 6, "?": 7}
    out = [
        "# Packet coverage",
        "",
        "Generated by `tools/packetcoverage.py` from the pinned OpenDAoC source"
        + (f" ({a.label})" if a.label else "")
        + " and this repo; tiers and features from `tools/packet-coverage-tiers.txt`. Rerun after a server or client",
        "change; a packet without a tier shows as `?`.",
        "",
        "- **S→C:** what OpenDAoC's 1.127 packet writers can send. *Ours*: `shown` = decoded and something in the game",
        "  reacts to it (or `ClientSession` reads it as a login or world-entry step); `decoded` = parsed but nothing uses it; `—` = not read. *Writers*: the Send methods that write it,",
        "  with their call sites in the server (0 = nothing in the game calls it).",
        "- **C→S:** what OpenDAoC has a handler for. *Ours*: `sent` when the client sends it.",
        "- **Tier:** 0 = needed already (login, world, movement); P1 = minimal playable (PvE to level 20, other players and",
        "  NPCs, quests); P2 = post-20 PvE and battlegrounds; P3 = PvP and RvR; R = retail servers only; dev = GM/debug;",
        "  `-` = not needed (an older client's form, unused by the game).",
        "",
    ]
    summary = defaultdict(lambda: [0, 0])
    for d, code, name, st, t, feat, w, live in rows:
        done = st in ("shown", "sent")
        if live:
            summary[(t, d)][0] += 1
            summary[(t, d)][1] += done
    out += ["## Summary", "", "| Tier | Direction | Packets | Handled | Missing |", "|---|---|---|---|---|"]
    for (t, d), (n, h) in sorted(summary.items(), key=lambda kv: (order.get(kv[0][0], 9), kv[0][1])):
        out.append(f"| {t} | {d} | {n} | {h} | {n - h} |")
    out.append("")
    for t in sorted({r[4] for r in rows}, key=lambda x: order.get(x, 9)):
        out += [f"## Tier {t}", "", "| Dir | Code | Packet | Ours | Feature | Server |", "|---|---|---|---|---|---|"]
        for d, code, name, st, tt, feat, w, live in sorted(rows, key=lambda r: (r[0], r[1])):
            if tt != t:
                continue
            note = "" if live else " *(never called)*"
            out.append(f"| {d} | 0x{code:02X} | {name} | {st} | {feat} | {w}{note} |")
        out.append("")
    pathlib.Path(a.out).write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"packetcoverage: {sum(1 for r in rows if r[0] == 'S→C')} S→C, {sum(1 for r in rows if r[0] == 'C→S')} C→S, "
          f"{len(unclassified)} without a tier -> {a.out}")
    for u in unclassified:
        print("  unclassified:", u)
    return 0


if __name__ == "__main__":
    sys.exit(main())
