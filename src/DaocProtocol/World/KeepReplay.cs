// SPDX-License-Identifier: GPL-3.0-or-later
//
// The keep packets a server would send for a region, built from keep tables in DOL/OpenDAoC form (`keep` and
// `keepcomponent` rows, as JSON: Eve of Darkness db-public's data/Keep.json and data/KeepComponent.json, MIT). Our server
// has no New Frontiers keeps, so the client is checked against these: `mpk keeps` writes them to a file and the client
// replays it (--keeps) as if the server had sent it. The rules are OpenDAoC-Core's (GPL-3.0, @0ac41d4):
//   - which components load: use_new_keeps 0 → Skin < 20, 1 → Skin > 20 (KeepManager.Load); Skin 20 never does;
//   - on entering the region every keep's 0x69 then a 0x6C per component (AbstractGameKeep.SendKeepInit);
//   - a component's height is its keep's, from the keep's level (KeepManager.GetHeightFromLevel);
//   - health 100 %; status 0 for a keep that stands (every row loads as a GameKeep: tower creation is commented out),
//     made 0x02 when the component is climbable: a wall (skin 9) or new climbing wall (27) of a keep that isn't a portal
//     keep (BaseLevel ≥ 100), towers only with allow_tower_climb (off by default) (GameKeepComponent.Climbing).
// The object ids are ours (the server assigns them at load).

using System.Text;
using System.Text.Json;
using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.World;

/// <summary>A `keep` row: the fields the packets use.</summary>
public sealed record KeepRow(ushort KeepId, string Name, int Region, uint X, uint Y, int Z, ushort Heading, byte Realm,
    byte Level, int BaseLevel);

/// <summary>A `keepcomponent` row.</summary>
public sealed record KeepComponentRow(ushort KeepId, ushort Id, byte Skin, sbyte X, sbyte Y, byte Heading);

public static class KeepReplay
{
    /// <summary>OpenDAoC's component skins that are climbable walls (Wall, NewSkinClimbingWall).</summary>
    private const byte WallSkin = 9, ClimbingWallSkin = 27;

    /// <summary>OpenDAoC's build height for a keep level: > 15 → 5, > 10 → 4, > 7 → 3, > 4 → 2, > 1 → 1, else 0.</summary>
    public static byte HeightFromLevel(int level) => level switch { > 15 => 5, > 10 => 4, > 7 => 3, > 4 => 2, > 1 => 1, _ => 0 };

    /// <summary>The packets for <paramref name="region"/>'s keeps, in the order the server sends them. <paramref name="level"/>,
    /// when given, replaces every keep's level (gaheris sets region 163 to 10: build height 3, the one that draws).
    /// <paramref name="health"/> sets every component's health % (0: broken, as OpenDAoC flags a dead piece), and
    /// <paramref name="raised"/> flags every tower's components raised (bit 2; towers are keep ids with a high byte): for
    /// checking damage and raised towers offline.</summary>
    public static List<ServerPacket> Build(IEnumerable<KeepRow> keeps, IEnumerable<KeepComponentRow> components, int region,
        bool newKeeps, int? level = null, byte health = 100, bool raised = false)
    {
        ILookup<ushort, KeepComponentRow> byKeep = components
            .Where(c => newKeeps ? c.Skin > 20 : c.Skin < 20)
            .ToLookup(c => c.KeepId);
        var packets = new List<ServerPacket>();
        uint objectId = 0x7000;
        foreach (KeepRow k in keeps.Where(k => k.Region == region).OrderBy(k => k.KeepId))
        {
            byte keepLevel = (byte)(level ?? k.Level);
            packets.Add(new ServerPacket(KeepInfo.Code, new KeepInfo(k.KeepId, k.X, k.Y, k.Heading, k.Realm, keepLevel).Payload()));
            byte height = HeightFromLevel(keepLevel);
            foreach (KeepComponentRow c in byKeep[k.KeepId].OrderBy(c => c.Id))
            {
                bool climbable = (c.Skin is WallSkin or ClimbingWallSkin) && k.BaseLevel < 100;
                byte flags = health == 0 ? KeepComponentInfo.Broken : climbable ? KeepComponentInfo.Climbable : (byte)0;
                if (raised && k.KeepId >> 8 != 0)
                    flags |= KeepComponentInfo.Raised;
                packets.Add(new ServerPacket(KeepComponentInfo.Code, new KeepComponentInfo(k.KeepId, c.Id, objectId++, c.Skin,
                    c.X, c.Y, c.Heading, height, health, flags).Payload()));
            }
        }
        return packets;
    }

    /// <summary>Reads db-public's Keep.json (an array of rows with OpenDAoC's column names).</summary>
    public static List<KeepRow> ReadKeeps(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => new KeepRow(
            (ushort)e.GetProperty("KeepID").GetInt32(), e.GetProperty("Name").GetString() ?? "", e.GetProperty("Region").GetInt32(),
            (uint)e.GetProperty("X").GetInt64(), (uint)e.GetProperty("Y").GetInt64(), e.GetProperty("Z").GetInt32(),
            (ushort)e.GetProperty("Heading").GetInt32(), (byte)e.GetProperty("Realm").GetInt32(), (byte)e.GetProperty("Level").GetInt32(),
            e.GetProperty("BaseLevel").GetInt32())).ToList();

    /// <summary>Reads db-public's KeepComponent.json.</summary>
    public static List<KeepComponentRow> ReadComponents(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => new KeepComponentRow(
            (ushort)e.GetProperty("KeepID").GetInt32(), (ushort)e.GetProperty("ID").GetInt32(), (byte)e.GetProperty("Skin").GetInt32(),
            (sbyte)e.GetProperty("X").GetInt32(), (sbyte)e.GetProperty("Y").GetInt32(), (byte)e.GetProperty("Heading").GetInt32())).ToList();

    /// <summary>The replay file: one packet per line, its code and payload in hex ("6C 0405000400..."); '#' starts a
    /// comment.</summary>
    public static string ToText(IEnumerable<ServerPacket> packets, string? header = null)
    {
        var sb = new StringBuilder();
        if (header is not null)
            foreach (string line in header.Split('\n'))
                sb.Append("# ").Append(line).Append('\n');
        foreach (ServerPacket p in packets)
            sb.Append($"{p.Code:X2} {Convert.ToHexString(p.Payload)}\n");
        return sb.ToString();
    }

    public static List<ServerPacket> ReadText(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 2))
            .Select(p => new ServerPacket(Convert.ToByte(p[0], 16), p.Length > 1 ? Convert.FromHexString(p[1]) : []))
            .ToList();
}
