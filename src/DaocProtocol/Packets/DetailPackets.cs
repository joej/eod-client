// SPDX-License-Identifier: GPL-3.0-or-later
//
// Asking the server for the full description of something ("delve") and its answer as a text window. Layouts: OpenDAoC-Core
// @f0aaa1e (GPL-3.0), GameServer/packets/Client/168/DetailDisplayHandler.cs (0xD8) and PacketLib181.SendCustomTextWindow
// with PacketLib168.WriteCustomTextWindowData (0xC4); the request's 12 bytes match the one 0xD8 in the 1.127e capture.

using DaocProtocol.Net;

namespace DaocProtocol.Packets;

/// <summary>
/// C→S 0xD8 DetailRequest: u16 BE type, 2 bytes the server doesn't read, u32 BE id, u32 BE extra (12 bytes). The server
/// reads the type, then u32 (the padding and the id's high half) and u16 (the id's low half), so an id fits in 16 bits.
/// Types used here: 1 an inventory item (id = its slot), 100 / 101 / 102 repair, self-craft and salvage it (mini_info's
/// buttons); 24–28 a spell, style, song, realm ability or ability by its id. An item's description comes back as a 0xC4
/// <see cref="DetailWindow"/>, the others as a 0xD8 <see cref="DelveRecord"/>.
/// </summary>
public static class DetailRequest
{
    public const byte Code = 0xD8;
    public const ushort InventoryItem = 1, Repair = 100, SelfCraft = 101, Salvage = 102;

    /// <summary>The 1.110+ delves, by the server's id for the thing (the id in its 0x16 entry): answered with a 0xD8
    /// <see cref="DelveRecord"/> (a song with two: its Song record and its Spell). OpenDAoC answers each (type, id) once an
    /// hour per connection, so the client keeps what it gets.</summary>
    public const ushort SpellDelve = 24, StyleDelve = 25, SongDelve = 26, RealmAbilityDelve = 27, AbilityDelve = 28;

    public static byte[] Payload(ushort type, uint id, uint extra = 0) =>
        new PayloadWriter().U16BE(type).U16BE(0).U32BE(id).U32BE(extra).ToArray();
}

/// <summary>
/// S→C 0xC4 DetailWindow, the text window: u8 mode (0 a description, 1 the statistics and titles window), u8 more (not 0:
/// the next 0xC4 continues this window), u8-length caption, then (u8 line number, u8-length text) pairs until a 0 byte.
/// The client appends the lines in order and ignores their numbers.
/// </summary>
public sealed record DetailWindow(byte Mode, byte More, string Caption, IReadOnlyList<string> Lines)
{
    public const byte Code = 0xC4;

    public static DetailWindow Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload);
        byte mode = r.U8(), more = r.U8();
        string caption = r.StringU8();
        var lines = new List<string>();
        while (r.Remaining > 0)
        {
            if (r.U8() == 0)
                break;
            lines.Add(r.StringU8());
        }
        return new DetailWindow(mode, more, caption, lines);
    }

    public byte[] ToBytes()
    {
        var w = new PayloadWriter().U8(Mode).U8(More).StringU8(Caption);
        for (int i = 0; i < Lines.Count; i++)
            w.U8((byte)(i + 1)).StringU8(Lines[i]);
        return w.U8(0).ToArray();
    }
}

/// <summary>
/// S→C 0xD8 DelveInfo: one structured record as text, <c>(Kind (key "value")(key "value")…)</c>, then a 0 byte. Kinds the
/// client reads: Spell, Style, Song, RealmAbility, Ability. Values have no escaping (no <c>"</c> inside) and may hold line
/// breaks; the client reads numbers as integers. OpenDAoC (DelveBuilder) leaves out values that are empty or "0".
/// </summary>
public sealed record DelveRecord(string Kind, IReadOnlyList<KeyValuePair<string, string>> Values)
{
    public const byte Code = 0xD8;

    /// <summary>The thing's id (the delve request's id): Index, written as a signed or unsigned 16-bit number.</summary>
    public ushort Index => (ushort)Int("Index");

    public string Name => Get("Name") ?? "";

    public string? Get(string key)
    {
        foreach (var kv in Values)
            if (kv.Key == key)
                return kv.Value;
        return null;
    }

    /// <summary>A value as the client reads it: an integer (the part before any decimal point), 0 when absent.</summary>
    public int Int(string key)
    {
        string? v = Get(key);
        if (v is null)
            return 0;
        int end = v.IndexOf('.');
        return int.TryParse(end < 0 ? v : v[..end], System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : 0;
    }

    public static DelveRecord Parse(ReadOnlySpan<byte> payload) => Parse(new PayloadReader(payload).StringRestNulTerminated());

    public static DelveRecord Parse(string text)
    {
        int i = 0;
        void Expect(char c)
        {
            while (i < text.Length && text[i] == ' ')
                i++;
            if (i >= text.Length || text[i] != c)
                throw new InvalidDataException($"0xD8: expected '{c}' at {i} in \"{text}\".");
            i++;
        }
        Expect('(');
        int start = i;
        while (i < text.Length && text[i] is not (' ' or '(' or ')'))
            i++;
        string kind = text[start..i];
        var values = new List<KeyValuePair<string, string>>();
        while (true)
        {
            while (i < text.Length && text[i] == ' ')
                i++;
            if (i >= text.Length)
                throw new InvalidDataException($"0xD8: unterminated record \"{text}\".");
            if (text[i] == ')')
                break;
            Expect('(');
            start = i;
            while (i < text.Length && text[i] is not (' ' or '"'))
                i++;
            string key = text[start..i];
            Expect('"');
            int close = text.IndexOf('"', i);
            if (close < 0)
                throw new InvalidDataException($"0xD8: unterminated value in \"{text}\".");
            values.Add(new(key, text[i..close]));
            i = close + 1;
            Expect(')');
        }
        return new DelveRecord(kind, values);
    }

    public override string ToString() => $"({Kind} {string.Concat(Values.Select(kv => $"({kv.Key} \"{kv.Value}\")"))})";

    public byte[] ToBytes() => new PayloadWriter().Bytes(System.Text.Encoding.Latin1.GetBytes(ToString())).U8(0).ToArray();
}
