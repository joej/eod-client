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

// Keeps and their components (New Frontiers keeps are built by the client from these; Old Frontiers keeps are zone
// fixtures). Layouts from OpenDAoC-Core (GPL-3.0, field order only): PacketLib1124.SendKeepInfo (0x69),
// PacketLib170.SendKeepComponentInfo (0x6C), SendKeepComponentDetailUpdate (0x6D), SendKeepComponentUpdate (0x67),
// SendKeepRealmUpdate (0x6A), SendKeepRemove (0x6B), SendKeepComponentRemove (0x6E). The server sends 0x69 and a 0x6C
// per component for every keep in the region when the player enters it (its 0xE8 PlayerInit). Each record also writes
// its payload, for tests and the offline test keep.

/// <summary>S→C 0x69 KeepInfo: keep id, 0, x and y (u32, region coordinates), heading, realm, level, 0, model (OpenDAoC
/// writes 0), 0. No z: the client finds the ground.</summary>
public sealed record KeepInfo(ushort KeepId, uint X, uint Y, ushort Heading, byte Realm, byte Level, byte Model = 0)
{
    public const byte Code = 0x69;

    public static KeepInfo Parse(ReadOnlySpan<byte> payload)
    {
        var r = new Net.PayloadReader(payload);
        ushort id = r.U16BE();
        r.Skip(2);
        uint x = r.U32BE(), y = r.U32BE();
        ushort heading = r.U16BE();
        byte realm = r.U8(), level = r.U8();
        r.Skip(2);
        return new KeepInfo(id, x, y, heading, realm, level, r.U8());
    }

    public byte[] Payload() => new Net.PayloadWriter().U16BE(KeepId).U16BE(0).U32BE(X).U32BE(Y).U16BE(Heading)
        .U8(Realm).U8(Level).U16BE(0).U8(Model).U8(0).ToArray();
}

/// <summary>S→C 0x6C KeepComponentInfo: keep id, component id, object id, skin (the component type), x and y offsets
/// (signed steps from the keep), heading (quarter turns, 0–3), height (0–5, from the keep's level), health %, status
/// flags, 0.</summary>
public sealed record KeepComponentInfo(ushort KeepId, ushort ComponentId, uint ObjectId, byte Skin, sbyte X, sbyte Y,
    byte Heading, byte Height, byte Health, byte Flags)
{
    public const byte Code = 0x6C;

    /// <summary>Status flags (daoc-re's packet table): bit 0 broken, bit 1 climbable, bit 2 a raised tower.</summary>
    public const byte Broken = 0x01, Climbable = 0x02, Raised = 0x04;

    public static KeepComponentInfo Parse(ReadOnlySpan<byte> payload)
    {
        var r = new Net.PayloadReader(payload);
        return new KeepComponentInfo(r.U16BE(), r.U16BE(), r.U32BE(), r.U8(), (sbyte)r.U8(), (sbyte)r.U8(), r.U8(), r.U8(),
            r.U8(), r.U8());
    }

    public byte[] Payload() => new Net.PayloadWriter().U16BE(KeepId).U16BE(ComponentId).U32BE(ObjectId).U8(Skin)
        .U8((byte)X).U8((byte)Y).U8(Heading).U8(Height).U8(Health).U8(Flags).U8(0).ToArray();
}

/// <summary>S→C 0x6D KeepComponentDetailUpdate: a component's height, health % and flags changed.</summary>
public sealed record KeepComponentDetail(ushort KeepId, ushort ComponentId, byte Height, byte Health, byte Flags)
{
    public const byte Code = 0x6D;

    public static KeepComponentDetail Parse(ReadOnlySpan<byte> payload)
    {
        var r = new Net.PayloadReader(payload);
        return new KeepComponentDetail(r.U16BE(), r.U16BE(), r.U8(), r.U8(), r.U8());
    }

    public byte[] Payload() => new Net.PayloadWriter().U16BE(KeepId).U16BE(ComponentId).U8(Height).U8(Health).U8(Flags)
        .U8(0).ToArray();
}

/// <summary>S→C 0x67 KeepComponentUpdate: a keep's realm and level, and one byte per component (in the order they were
/// sent): its height, plus 0x80 climbable, 0x40 dead, 0x20 levelling up, 0x10 raised.</summary>
public sealed record KeepUpdate(ushort KeepId, byte Realm, byte Level, IReadOnlyList<byte> Components)
{
    public const byte Code = 0x67;
    public const byte HeightMask = 0x0F, RaisedBit = 0x10, LevelUpBit = 0x20, DeadBit = 0x40, ClimbableBit = 0x80;

    public static KeepUpdate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new Net.PayloadReader(payload);
        ushort id = r.U16BE();
        byte realm = r.U8(), level = r.U8(), count = r.U8();
        var parts = new byte[count];
        for (int i = 0; i < count; i++)
            parts[i] = r.U8();
        return new KeepUpdate(id, realm, level, parts);
    }

    public byte[] Payload()
    {
        var w = new Net.PayloadWriter().U16BE(KeepId).U8(Realm).U8(Level).U8((byte)Components.Count);
        foreach (byte b in Components)
            w.U8(b);
        return w.U8(0).ToArray();
    }
}

/// <summary>S→C 0x6A KeepRealmUpdate: a keep changed realm or level.</summary>
public sealed record KeepRealm(ushort KeepId, byte Realm, byte Level)
{
    public const byte Code = 0x6A;

    public static KeepRealm Parse(ReadOnlySpan<byte> payload)
    {
        var r = new Net.PayloadReader(payload);
        return new KeepRealm(r.U16BE(), r.U8(), r.U8());
    }

    public byte[] Payload() => new Net.PayloadWriter().U16BE(KeepId).U8(Realm).U8(Level).ToArray();
}

/// <summary>S→C 0x6B KeepRemove (keep id) and 0x6E KeepComponentRemove (keep id, component id).</summary>
public static class KeepRemove
{
    public const byte Code = 0x6B, ComponentCode = 0x6E;

    public static ushort Parse(ReadOnlySpan<byte> payload) => new Net.PayloadReader(payload).U16BE();

    public static (ushort KeepId, ushort ComponentId) ParseComponent(ReadOnlySpan<byte> payload)
    {
        var r = new Net.PayloadReader(payload);
        return (r.U16BE(), r.U16BE());
    }
}
