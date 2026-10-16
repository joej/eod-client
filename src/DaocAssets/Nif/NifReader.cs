// SPDX-License-Identifier: GPL-3.0-or-later
//
// Reader for DAoC NIF (NetImmerse/Gamebryo) model files, enough to pull static mesh geometry
// (vertices, normals, UVs, triangles) for conversion to glTF. Field layouts reimplemented from
// the niftools nif.xml spec (GPL-3.0), verified field-for-field against real OpenDAoC 1.127
// client files (see private notes). No third-party source copied.
//
// Scope: static meshes only. The client's models are NIF 4.2.2.0 / 4.2.1.0 (NetImmerse, items)
// and 10.1.0.0 (Gamebryo, creatures). There are no .kf animation files, so no skeleton/skinning
// is read for the first proof; creature models are internally rigged but we take their bind-pose
// geometry. This file implements the 4.2.x path; 10.1.0.0 follows (different header + block table).
//
// Key version facts (nif.xml): a NIF `bool` is 1 byte from 4.1.0.1 on. At 4.2.2.0 the geometry
// block has no Has-UV byte (UV-set count lives in a Data Flags u16, field `until=4.2.2.0`) and no
// Has-Triangles byte (that is `since 10.1.0.0`), so triangles follow the triangle-point count.

using System.Buffers.Binary;

namespace DaocAssets.Nif;

/// <summary>Thrown when a file is not a NIF we can read, or is malformed.</summary>
public sealed class NifFormatException(string message) : Exception(message);

/// <summary>A NIF version as its four bytes, comparable for version-gating (e.g. 4.2.2.0).</summary>
public readonly record struct NifVersion(byte A, byte B, byte C, byte D) : IComparable<NifVersion>
{
    public uint Packed => ((uint)A << 24) | ((uint)B << 16) | ((uint)C << 8) | D;
    public int CompareTo(NifVersion other) => Packed.CompareTo(other.Packed);
    public static bool operator <(NifVersion l, NifVersion r) => l.Packed < r.Packed;
    public static bool operator >(NifVersion l, NifVersion r) => l.Packed > r.Packed;
    public static bool operator <=(NifVersion l, NifVersion r) => l.Packed <= r.Packed;
    public static bool operator >=(NifVersion l, NifVersion r) => l.Packed >= r.Packed;
    public override string ToString() => $"{A}.{B}.{C}.{D}";
}

/// <summary>A sequential little-endian cursor over a NIF's bytes, with the NIF primitive reads.</summary>
internal ref struct NifCursor(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    public int Pos { get; private set; }

    /// <summary>Jumps the cursor to an absolute offset (used to skip block bodies we don't model).</summary>
    public void SeekTo(int offset)
    {
        if (offset < 0 || offset > _data.Length)
            throw new NifFormatException($"Seek to {offset} is outside the NIF.");
        Pos = offset;
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (Pos + n > _data.Length)
            throw new NifFormatException($"Unexpected end of NIF at offset {Pos} (needed {n} bytes).");
        ReadOnlySpan<byte> s = _data.Slice(Pos, n);
        Pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public bool Bool() => U8() != 0;          // 1-byte bool (4.1.0.1 and later)
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public System.Numerics.Vector3 Vec3() => new(F32(), F32(), F32());

    /// <summary>A sized string: u32 length then that many bytes (Latin-1, as the client writes).</summary>
    public string SizedString()
    {
        int n = checked((int)U32());
        return System.Text.Encoding.Latin1.GetString(Take(n));
    }

    /// <summary>Reads up to and including a 0x0A, returning the bytes before it as ASCII.</summary>
    public string LineString()
    {
        int start = Pos;
        while (Pos < _data.Length && _data[Pos] != 0x0A)
            Pos++;
        if (Pos >= _data.Length)
            throw new NifFormatException("NIF header version line is not terminated.");
        string s = System.Text.Encoding.ASCII.GetString(_data.Slice(start, Pos - start));
        Pos++; // consume the 0x0A
        return s;
    }
}
