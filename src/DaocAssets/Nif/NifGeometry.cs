// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;

namespace DaocAssets.Nif;

/// <summary>The contents of a NiTriShapeData / NiTriStripsData block (strips already as triangles).</summary>
internal sealed class NifGeometryData
{
    public int VertexCount;
    /// <summary>Empty when the block has no vertices.</summary>
    public Vector3[] Vertices = [];
    public Vector3[]? Normals;
    public Vector4[]? Colors;
    public Vector2[][] UvSets = [];
    /// <summary>Empty when the block has no triangles (10.1 "Has Triangles" / "Has Points" off).</summary>
    public (ushort A, ushort B, ushort C)[] Triangles = [];
}

/// <summary>
/// The one decoder for triangle geometry, NetImmerse 4.x and Gamebryo 10.1.0.0, used by the converter
/// (<see cref="NifFile"/>, <see cref="Gamebryo1010"/>) and the audit (<see cref="NifSurvey"/>) alike,
/// so what the audit checks is the code that converts. Field order per <c>nif.xml</c>, gates resolved:
/// <code>
/// NiGeometryData       4.x                              10.1.0.0
///   Num Vertices u16   yes                              yes
///   Keep, Compress u8  -                                yes
///   Vertices           bool + Vec3s                     bool + Vec3s
///   Data Flags u16     after the colours                here (low 6 bits UV sets, bit 12 tangents)
///   Normals            bool + Vec3s                     bool + Vec3s (+ tangents, bitangents)
///   Center, Radius     yes                              yes
///   Vertex Colors      bool + Color4s                   bool + Color4s
///   UV sets            Data Flags &amp; 63, each Vec2s       same
///   Consistency u16    -                                yes
/// NiTriBasedGeomData: Num Triangles u16.
/// NiTriShapeData: Num Triangle Points u32 (= 3 x triangles), [10.1: Has Triangles bool], triangles,
///   Num Match Groups u16 + groups.
/// NiTriStripsData: Num Strips u16, Strip Lengths u16 x strips, [10.1: Has Points bool], points.
/// </code>
/// Bools must be 0 or 1 and indices must name a vertex, so a wrong layout fails close to where it
/// goes wrong. Feature counts ("Type: feature") go to <c>features</c> when given.
/// </summary>
internal static class NifGeometry
{
    public static NifGeometryData Read(ref NifCursor c, string type, bool gamebryo10, Dictionary<string, int>? features)
    {
        NifGeometryData g = ReadBase(ref c, type, gamebryo10, features);
        int nv = g.VertexCount;
        int triangles = c.U16();                         // NiTriBasedGeomData
        if (type == "NiTriShapeData")
        {
            uint points = c.U32();
            if (points != (uint)triangles * 3)
                throw new NifFormatException($"Triangle point count {points} != 3 x {triangles}.");
            if (!gamebryo10 || Bool(ref c))
            {
                g.Triangles = new (ushort, ushort, ushort)[triangles];
                for (int t = 0; t < triangles; t++)
                    g.Triangles[t] = (Index(ref c, nv), Index(ref c, nv), Index(ref c, nv));
            }
            int groups = c.U16();                        // Match Groups
            for (int m = 0; m < groups; m++)
                Skip(ref c, c.U16() * 2L);
        }
        else if (type == "NiTriStripsData")
        {
            int strips = c.U16();
            var lengths = new int[strips];
            for (int s = 0; s < strips; s++)
                lengths[s] = c.U16();
            if (!gamebryo10 || Bool(ref c))
            {
                var tris = new List<(ushort, ushort, ushort)>();
                foreach (int len in lengths)
                {
                    var strip = new ushort[len];
                    for (int k = 0; k < len; k++)
                    {
                        strip[k] = c.U16();              // 0xFFFF = strip restart
                        if (strip[k] >= nv && strip[k] != 0xFFFF)
                            throw new NifFormatException($"Strip index {strip[k]} past {nv} vertices.");
                    }
                    NifFile.AppendStrip(strip, tris);
                }
                g.Triangles = [.. tris];
            }
        }
        else
        {
            throw new ArgumentException($"{type} is not triangle geometry.", nameof(type));
        }
        return g;
    }

    /// <summary>
    /// NiGeometryData alone (Num Vertices to the UV sets, or to Consistency Flags at 10.1): what
    /// triangle and particle data share.
    /// </summary>
    public static NifGeometryData ReadBase(ref NifCursor c, string type, bool gamebryo10, Dictionary<string, int>? f)
    {
        var g = new NifGeometryData();
        int nv = g.VertexCount = c.U16();
        if (gamebryo10)
        {
            c.U8(); c.U8();                              // Keep Flags, Compress Flags
        }
        if (Bool(ref c))
            g.Vertices = Vec3s(ref c, nv);
        int dataFlags = gamebryo10 ? c.U16() : 0;
        bool tangents = (dataFlags & 0x1000) != 0;
        if (Bool(ref c))
        {
            g.Normals = Vec3s(ref c, nv);
            if (tangents)
            {
                Skip(ref c, nv * 24L);                   // tangents, bitangents
                Bump(f, $"{type}: tangents");
            }
        }
        else
        {
            Bump(f, $"{type}: no normals");
        }
        Skip(ref c, 16);                                 // Center, Radius
        if (Bool(ref c))
        {
            Bump(f, $"{type}: vertex colours");
            g.Colors = new Vector4[nv];
            for (int v = 0; v < nv; v++)
                g.Colors[v] = new Vector4(c.F32(), c.F32(), c.F32(), c.F32());
        }
        if (!gamebryo10)
            dataFlags = c.U16();                         // Data Flags (until 4.2.2.0)
        int uvSets = dataFlags & 0x3F;
        if (uvSets == 0) Bump(f, $"{type}: no UVs");
        if (uvSets > 1) Bump(f, $"{type}: {uvSets} UV sets");
        g.UvSets = new Vector2[uvSets][];
        for (int s = 0; s < uvSets; s++)
        {
            var set = new Vector2[nv];
            for (int v = 0; v < nv; v++)
                set[v] = new Vector2(c.F32(), c.F32());
            g.UvSets[s] = set;
        }
        if (gamebryo10)
            c.U16();                                     // Consistency Flags
        return g;
    }

    private static bool Bool(ref NifCursor c) => c.U8() switch
    {
        0 => false,
        1 => true,
        byte b => throw new NifFormatException($"Bool byte {b} at {c.Pos - 1} (not 0 or 1)."),
    };

    private static ushort Index(ref NifCursor c, int nv)
    {
        ushort i = c.U16();
        if (i >= nv)
            throw new NifFormatException($"Vertex index {i} past {nv} vertices.");
        return i;
    }

    private static Vector3[] Vec3s(ref NifCursor c, int n)
    {
        var a = new Vector3[n];
        for (int i = 0; i < n; i++)
            a[i] = c.Vec3();
        return a;
    }

    private static void Skip(ref NifCursor c, long count)
    {
        if (count < 0 || c.Pos + count > c.Length)
            throw new NifFormatException("Block runs past the end of the file.");
        c.SeekTo(c.Pos + (int)count);
    }

    private static void Bump(Dictionary<string, int>? f, string key)
    {
        if (f is not null)
            f[key] = f.GetValueOrDefault(key) + 1;
    }
}
