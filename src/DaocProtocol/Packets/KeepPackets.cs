// SPDX-License-Identifier: GPL-3.0-or-later
//
// Keep hookpoints (1.127): the client asks for a component's hookpoints and a hookpoint's store with C→S 0x6F; the
// server answers with S→C 0x65 (the free hookpoints) and 0x63 (what can be bought there). Layouts from OpenDAoC-Core
// (GPL-3.0): KeepComponentInteractHandler (0x6F), PacketLib170.SendKeepComponentHookPoint /
// SendClearKeepComponentHookPoint (0x65), PacketLib1127.SendHookPointStore (0x63). Used by tools/DaocCli; the game
// client has no keep windows yet.

namespace DaocProtocol.Packets;

/// <summary>C→S 0x6F KeepComponentInteract: keep id, component id, response, hookpoint index, all u16 BE.</summary>
public static class KeepComponentInteract
{
    public const byte Code = 0x6F;

    /// <summary>What the client asks: 0 the component's info (0x61), 1 its hookpoints (0x65), 2 a hookpoint's store
    /// (0x65 with no hookpoints, then 0x63).</summary>
    public const ushort ShowInfo = 0, ShowHookpoints = 1, SelectHookpoint = 2;

    public static byte[] Payload(ushort keepId, ushort componentId, ushort response, ushort hookpoint) =>
        new Net.PayloadWriter().U16BE(keepId).U16BE(componentId).U16BE(response).U16BE(hookpoint).ToArray();
}

/// <summary>S→C 0x65 KeepComponentHookpointUpdate: keep id, component id (u16 BE), count, the selected index, then
/// count free hookpoint ids (u8). Answering a selection, the count is 0.</summary>
public sealed record KeepHookpoints(ushort KeepId, ushort ComponentId, byte Selected, IReadOnlyList<byte> Free)
{
    public const byte Code = 0x65;

    public static KeepHookpoints Parse(ReadOnlySpan<byte> payload)
    {
        var r = new Net.PayloadReader(payload);
        ushort keep = r.U16BE(), component = r.U16BE();
        byte count = r.U8(), selected = r.U8();
        var free = new byte[count];
        for (int i = 0; i < count; i++)
            free[i] = r.U8();
        return new KeepHookpoints(keep, component, selected, free);
    }
}

/// <summary>One thing a hookpoint sells (0x63 row).</summary>
public sealed record HookpointItem(byte Index, ushort Weight, uint Price, ushort Model, string Name);

/// <summary>
/// S→C 0x63 KeepComponentHookpointStore, 1.127 layout: count, 0, three bytes, then keep id, component id and hookpoint
/// id (u16 LE); per row an index, 6 bytes, weight u16 LE, price u32 LE, model u16 LE and the name (u32 LE length
/// counting a NUL, the bytes, the NUL).
/// </summary>
public sealed record HookpointStore(ushort KeepId, ushort ComponentId, ushort HookpointId, IReadOnlyList<HookpointItem> Items)
{
    public const byte Code = 0x63;

    public static HookpointStore Parse(ReadOnlySpan<byte> payload)
    {
        var r = new Net.PayloadReader(payload);
        byte count = r.U8();
        r.Skip(4);
        ushort keep = r.U16LE(), component = r.U16LE(), hookpoint = r.U16LE();
        var items = new List<HookpointItem>(count);
        for (int i = 0; i < count; i++)
        {
            byte index = r.U8();
            r.Skip(6);
            ushort weight = r.U16LE();
            uint price = r.U32LE();
            ushort model = r.U16LE();
            items.Add(new HookpointItem(index, weight, price, model, r.StringIntLE()));
        }
        return new HookpointStore(keep, component, hookpoint, items);
    }
}
