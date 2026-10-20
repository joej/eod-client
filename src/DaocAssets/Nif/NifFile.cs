// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;

namespace DaocAssets.Nif;

/// <summary>
/// Parses a NIF file into its static meshes. Entry point: <see cref="Read(ReadOnlySpan{byte})"/>.
/// Implements the NetImmerse 4.2.x path (items); Gamebryo 10.1.0.0 (creatures) is handled
/// separately once decoded. Unknown block types are skipped by walking to the next recognised
/// block, so a file we don't fully model still yields the geometry we do understand.
/// </summary>
public static class NifFile
{
    private static readonly NifVersion V4000 = new(4, 0, 0, 2);
    private static readonly NifVersion V1010 = new(10, 1, 0, 0);
    private static readonly NifVersion V1020 = new(10, 2, 0, 0);

    public static NifModel Read(ReadOnlySpan<byte> bytes)
    {
        var c = new NifCursor(bytes);
        string header = c.LineString();
        if (!header.Contains("File Format", StringComparison.Ordinal))
            throw new NifFormatException($"Not a NIF file (header: '{header}').");

        // Version u32 follows the header line in both layouts. The layout after it differs:
        //  - 4.2.x: num-blocks u32, then each block is an inline sized type-name string + body.
        //  - 10.1.0.0 (Gamebryo): user-version u32, num-blocks u32, a block-type table (count +
        //    sized strings) and a per-block type index; block bodies carry NO inline type string.
        uint packed = c.U32();
        var version = new NifVersion((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        if (version >= V1020)
            throw new NifFormatException($"NIF version {version} is newer than supported (4.2.x and 10.1.0.0 only).");
        if (version >= V1010)
            return ReadGamebryo1010(bytes, c, version);
        if (version <= V4000)
            throw new NifFormatException($"NIF version {version} is older than supported (4.1.0.1+).");

        int blockCount = checked((int)c.U32());
        var meshes = new List<NifMesh>();
        string? textureFile = null;

        // Walk blocks. Each block in 4.2.x is a sized type-name string followed by its body. We
        // fully parse the blocks that carry geometry and read enough of the others to stay aligned;
        // where a body's length isn't known we rely on the geometry blocks being self-delimiting.
        // The 4.2.x layout places NiTriShapeData bodies last and self-sized, so we parse each
        // recognised block and skip forward over unrecognised ones to the next type string.
        for (int i = 0; i < blockCount; i++)
        {
            string type;
            try { type = c.SizedString(); }
            catch (NifFormatException) { break; }

            switch (type)
            {
                case "NiTriShapeData":
                    meshes.Add(ReadTriShapeData(ref c, version, $"mesh{i}"));
                    break;
                case "NiSourceTexture":
                    textureFile ??= ReadSourceTextureFileName(ref c, bytes);
                    // Fall through to resync: we read only the external file name, not the whole body.
                    if (!ResyncToNextTypeString(ref c, bytes))
                        i = blockCount;
                    break;
                default:
                    // Not a geometry-data block: resync to the next block's type string so the
                    // block count stays meaningful even though we don't model this body.
                    if (!ResyncToNextTypeString(ref c, bytes))
                        i = blockCount; // nothing more recognisable; stop
                    break;
            }
        }

        if (meshes.Count == 0)
            throw new NifFormatException("No NiTriShapeData geometry found in NIF.");

        // Item NIFs carry a single external texture; attach it to meshes that named none. We don't
        // resolve the full block-reference graph (NiTriShape -> NiTexturingProperty ->
        // NiSourceTexture), so this is a best effort that is unambiguous for single-material models.
        if (textureFile is not null)
            for (int m = 0; m < meshes.Count; m++)
                meshes[m] = meshes[m] with { TextureFile = meshes[m].TextureFile ?? textureFile };

        return new NifModel { Version = version, Meshes = meshes };
    }

    // Gamebryo 10.1.0.0 (creatures). The header after the Version u32 is: User Version u32,
    // Num Blocks u32, Num Block Types u16, [SizedString x Num Block Types], Block Type Index
    // (u16 x Num Blocks). Block bodies carry no inline type string — the type comes from the index
    // table — and the intermediate blocks (NiNode, NiKeyframeController/Data, NiSkin*) have large
    // version-gated layouts we don't need. Since the goal is geometry, we read the header to bound
    // and validate the file, then locate each NiTriShapeData body by a strict, self-validating scan
    // (every field consistency-checked), mirroring the 4.2.x approach of finding geometry without
    // modelling every intermediate block. Verified byte-for-byte against a real creature NIF.
    private static NifModel ReadGamebryo1010(ReadOnlySpan<byte> bytes, NifCursor c, NifVersion version)
    {
        c.U32();                                  // User Version
        int blockCount = checked((int)c.U32());   // Num Blocks
        int numTypes = c.U16();                    // Num Block Types
        if (blockCount is < 1 or > 100000 || numTypes is < 1 or > 10000)
            throw new NifFormatException($"Implausible 10.1.0.0 header (blocks {blockCount}, types {numTypes}).");

        var typeNames = new string[numTypes];
        for (int t = 0; t < numTypes; t++)
            typeNames[t] = c.SizedString();
        bool hasShape = false, hasStrips = false;
        foreach (string t in typeNames)
        {
            hasShape |= t == "NiTriShapeData";
            hasStrips |= t == "NiTriStripsData";
        }
        // Block Type Index (u16 per block) follows; skipping it lands the cursor at the first body.
        for (int b = 0; b < blockCount; b++)
            c.U16();
        int bodiesStart = c.Pos;

        if (!hasShape && !hasStrips)
            throw new NifFormatException("No NiTriShapeData/NiTriStripsData type in 10.1.0.0 NIF (no geometry we read).");

        // Scan for geometry bodies. A file holds one kind (shape or strips); try whichever the type
        // table says is present, each a strict self-validating parse so a wrong offset is rejected.
        var meshes = new List<NifMesh>();
        int scan = bodiesStart;
        while (scan < bytes.Length)
        {
            if (hasShape && TryReadTriShapeData1010(bytes, scan, $"mesh{meshes.Count}", out NifMesh mesh, out int next))
            {
                meshes.Add(mesh);
                scan = next;
            }
            else if (hasStrips && TryReadTriStripsData1010(bytes, scan, $"mesh{meshes.Count}", out NifMesh strip, out int nextS))
            {
                meshes.Add(strip);
                scan = nextS;
            }
            else
            {
                scan++;
            }
        }

        if (meshes.Count == 0)
            throw new NifFormatException("No NiTriShapeData/NiTriStripsData geometry decoded in 10.1.0.0 NIF.");

        string? textureFile = FindFirstTextureName(bytes);
        if (textureFile is not null)
            for (int m = 0; m < meshes.Count; m++)
                meshes[m] = meshes[m] with { TextureFile = meshes[m].TextureFile ?? textureFile };

        return new NifModel { Version = version, Meshes = meshes };
    }

    // The geometry data shared by NiTriShapeData and NiTriStripsData at 10.1.0.0 (the NiGeometryData
    // -> NiTriBasedGeomData part), read up to and including Num Triangles. Field order (gates
    // resolved for 10.1.0.0, verified against nif.xml):
    //   Num Vertices u16; Keep Flags u8; Compress Flags u8; Has Vertices bool(1) + Vertices;
    //   Data Flags u16 (low 6 bits = UV-set count, bit 4096 = tangents present); Has Normals bool(1)
    //   + Normals [+ Tangents + Bitangents if bit 4096]; Bounding Sphere (centre+radius);
    //   Has Vertex Colors bool(1) + Color4s; UV sets; Consistency Flags u16; Num Triangles u16.
    // Returns false on any inconsistency so it can drive a self-validating scan for the block.
    private static bool TryReadGeometryCommon1010(
        ref NifCursor c, out int numVertices, out Vector3[] vertices,
        out Vector3[]? normals, out Vector2[]? texCoords, out int numTriangles)
    {
        numVertices = 0; vertices = []; normals = null; texCoords = null; numTriangles = 0;

        int nv = c.U16();
        if (nv is < 1 or > 65535)
            return false;
        c.U8();                      // Keep Flags
        c.U8();                      // Compress Flags

        if (!TryBool(ref c, out bool hasVerts) || !hasVerts) // geometry we use always has vertices
            return false;
        vertices = ReadVec3Array(ref c, nv);

        int dataFlags = c.U16();
        int uvSets = dataFlags & 0x3F;
        bool hasTangents = (dataFlags & 0x1000) != 0;

        if (!TryBool(ref c, out bool hasNormals)) return false;
        normals = hasNormals ? ReadVec3Array(ref c, nv) : null;
        if (hasNormals && hasTangents)
            for (int k = 0; k < nv; k++) { c.Vec3(); c.Vec3(); } // tangents + bitangents

        c.Vec3(); c.F32();           // Bounding Sphere

        if (!TryBool(ref c, out bool hasColors)) return false;
        if (hasColors)
            for (int k = 0; k < nv * 4; k++) c.F32();

        for (int s = 0; s < uvSets; s++)
        {
            var set = new Vector2[nv];
            for (int v = 0; v < nv; v++)
                set[v] = new Vector2(c.F32(), c.F32());
            texCoords ??= set;
        }

        c.U16();                     // Consistency Flags
        numTriangles = c.U16();
        numVertices = nv;
        return true;
    }

    // NiTriShapeData body at 10.1.0.0 (self-validating). After the shared geometry:
    //   Num Triangle Points u32 (=3x); Has Triangles bool(1) + Triangles; Num Match Groups u16.
    private static bool TryReadTriShapeData1010(
        ReadOnlySpan<byte> bytes, int offset, string name, out NifMesh mesh, out int next)
    {
        mesh = null!;
        next = offset;
        try
        {
            var c = new NifCursor(bytes);
            c.SeekTo(offset);

            if (!TryReadGeometryCommon1010(ref c, out int nv, out Vector3[] vertices,
                    out Vector3[]? normals, out Vector2[]? texCoords, out int numTriangles))
                return false;
            if (numTriangles == 0)
                return false;

            uint numTrianglePoints = c.U32();
            if (numTrianglePoints != (uint)numTriangles * 3)
                return false;
            if (!TryBool(ref c, out bool hasTriangles) || !hasTriangles)
                return false;

            var triangles = new (ushort, ushort, ushort)[numTriangles];
            for (int t = 0; t < numTriangles; t++)
            {
                ushort a = c.U16(), b = c.U16(), cc = c.U16();
                if (a >= nv || b >= nv || cc >= nv)
                    return false;
                triangles[t] = (a, b, cc);
            }
            int matchGroups = c.U16();
            if (matchGroups is < 0 or > 70000)
                return false;

            mesh = new NifMesh
            {
                Name = name,
                Vertices = vertices,
                Normals = normals,
                TexCoords = texCoords,
                Triangles = triangles,
            };
            next = c.Pos;
            return true;
        }
        catch (NifFormatException)
        {
            return false;
        }
    }

    // NiTriStripsData body at 10.1.0.0 (self-validating). After the shared geometry:
    //   Num Strips u16; Strip Lengths u16 x Num Strips; Has Points bool(1); Points u16 (one array
    //   per strip, each Strip Lengths[i] long). We convert the strips to a triangle list, flipping
    //   winding on odd triangles and dropping degenerate (stitch) triangles.
    private static bool TryReadTriStripsData1010(
        ReadOnlySpan<byte> bytes, int offset, string name, out NifMesh mesh, out int next)
    {
        mesh = null!;
        next = offset;
        try
        {
            var c = new NifCursor(bytes);
            c.SeekTo(offset);

            if (!TryReadGeometryCommon1010(ref c, out int nv, out Vector3[] vertices,
                    out Vector3[]? normals, out Vector2[]? texCoords, out int numTriangles))
                return false;
            if (numTriangles == 0)
                return false;

            int numStrips = c.U16();
            if (numStrips is < 1 or > 10000)
                return false;
            var lengths = new int[numStrips];
            long totalPoints = 0;
            for (int s = 0; s < numStrips; s++)
            {
                lengths[s] = c.U16();
                totalPoints += lengths[s];
            }
            if (totalPoints is 0 or > 2_000_000)
                return false;
            if (!TryBool(ref c, out bool hasPoints) || !hasPoints)
                return false;

            var triangles = new List<(ushort, ushort, ushort)>();
            for (int s = 0; s < numStrips; s++)
            {
                int len = lengths[s];
                var strip = new ushort[len];
                for (int i = 0; i < len; i++)
                {
                    ushort idx = c.U16();
                    // 0xFFFF is a strip-restart sentinel in some data; otherwise indices must be valid.
                    if (idx != 0xFFFF && idx >= nv)
                        return false;
                    strip[i] = idx;
                }
                AppendStripTriangles(strip, triangles);
            }
            if (triangles.Count == 0)
                return false;

            mesh = new NifMesh
            {
                Name = name,
                Vertices = vertices,
                Normals = normals,
                TexCoords = texCoords,
                Triangles = triangles.ToArray(),
            };
            next = c.Pos;
            return true;
        }
        catch (NifFormatException)
        {
            return false;
        }
    }

    // Converts one triangle strip to triangles: triangle i is (p[i], p[i+1], p[i+2]) with winding
    // flipped on odd i to keep consistent facing. Degenerate triangles (a repeated index, used to
    // stitch strips together, plus the 0xFFFF restart sentinel) are dropped.
    private static void AppendStripTriangles(ushort[] strip, List<(ushort, ushort, ushort)> into)
    {
        for (int i = 0; i + 2 < strip.Length; i++)
        {
            ushort a = strip[i], b = strip[i + 1], cc = strip[i + 2];
            if (a == b || b == cc || a == cc || a == 0xFFFF || b == 0xFFFF || cc == 0xFFFF)
                continue;
            into.Add((i & 1) == 0 ? (a, b, cc) : (b, a, cc));
        }
    }

    // A 1-byte bool that must be exactly 0 or 1 (used to reject a wrong scan offset).
    private static bool TryBool(ref NifCursor c, out bool value)
    {
        byte b = c.U8();
        value = b != 0;
        return b is 0 or 1;
    }

    // Scans for the first external texture file name (a sized string ending in a known image
    // extension). Works for both the 4.2.x and 10.1.0.0 NiSourceTexture layouts.
    private static string? FindFirstTextureName(ReadOnlySpan<byte> bytes)
    {
        for (int q = 0; q + 4 <= bytes.Length; q++)
        {
            uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(q, 4));
            if (len is < 5 or > 255 || q + 4 + (int)len > bytes.Length)
                continue;
            ReadOnlySpan<byte> s = bytes.Slice(q + 4, (int)len);
            bool printable = true;
            foreach (byte ch in s)
                if (ch is < 0x20 or > 0x7E) { printable = false; break; }
            if (!printable)
                continue;
            string str = System.Text.Encoding.Latin1.GetString(s);
            if (str.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                || str.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                || str.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                return str;
        }
        return null;
    }

    // NiGeometryData -> NiTriBasedGeomData -> NiTriShapeData, field order for 4.2.2.0 (verified).
    private static NifMesh ReadTriShapeData(ref NifCursor c, NifVersion version, string name)
    {
        int numVertices = c.U16();

        Vector3[] vertices = c.Bool() ? ReadVec3Array(ref c, numVertices) : [];
        Vector3[]? normals = c.Bool() ? ReadVec3Array(ref c, numVertices) : null;
        // Bounding sphere: centre + radius.
        c.Vec3();
        c.F32();
        if (c.Bool()) // vertex colours (Color4), read and discard
            for (int k = 0; k < numVertices * 4; k++)
                c.F32();

        // Data Flags (u16, field `until=4.2.2.0`): low 6 bits = UV-set count. No Has-UV byte here.
        int uvSets = c.U16() & 0x3F;
        Vector2[]? texCoords = null;
        for (int s = 0; s < uvSets; s++)
        {
            Vector2[] set = new Vector2[numVertices];
            for (int v = 0; v < numVertices; v++)
                set[v] = new Vector2(c.F32(), c.F32());
            texCoords ??= set; // keep the first UV set
        }

        int numTriangles = c.U16();          // NiTriBasedGeomData
        uint numTrianglePoints = c.U32();     // NiTriShapeData
        if (numTrianglePoints != (uint)numTriangles * 3)
            throw new NifFormatException($"Triangle point count {numTrianglePoints} != 3 x {numTriangles}.");
        // `Has Triangles` is `since 10.1.0.0`, so at 4.2.x triangles follow directly.

        var triangles = new (ushort, ushort, ushort)[numTriangles];
        for (int t = 0; t < numTriangles; t++)
        {
            ushort a = c.U16(), b = c.U16(), cc = c.U16();
            if (a >= numVertices || b >= numVertices || cc >= numVertices)
                throw new NifFormatException($"Triangle {t} indexes a vertex past {numVertices}.");
            triangles[t] = (a, b, cc);
        }

        return new NifMesh
        {
            Name = name,
            Vertices = vertices,
            Normals = normals,
            TexCoords = texCoords,
            Triangles = triangles,
        };
    }

    // NiSourceTexture body (4.2.x), from here positioned just past the type string:
    //   u32 unknown, 2x link ref (ignored), Use External (1-byte bool). If external, a sized-string
    //   File Name follows. We read only the file name and let the caller resync past the rest.
    private static string? ReadSourceTextureFileName(ref NifCursor c, ReadOnlySpan<byte> bytes)
    {
        try
        {
            c.U32();               // unknown/flags
            c.U32(); c.U32();      // link refs (no parent here)
            if (!c.Bool())         // Use External
                return null;
            string name = c.SizedString();
            // Sanity: an external texture name is a short Latin-1 file name, not arbitrary bytes.
            return name.Length is > 0 and < 256 && name.All(ch => ch is >= ' ' and < (char)127)
                ? name
                : null;
        }
        catch (NifFormatException)
        {
            return null;
        }
    }

    private static Vector3[] ReadVec3Array(ref NifCursor c, int n)
    {
        var a = new Vector3[n];
        for (int i = 0; i < n; i++)
            a[i] = c.Vec3();
        return a;
    }

    // Advances the cursor to the next position that begins a plausible sized type string naming a
    // "Ni*" block, so parsing can continue past a block body we don't model. Returns false if none.
    private static bool ResyncToNextTypeString(ref NifCursor c, ReadOnlySpan<byte> bytes)
    {
        for (int q = c.Pos; q + 6 <= bytes.Length; q++)
        {
            uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(q, 4));
            if (len is >= 4 and <= 40 && q + 4 + (int)len <= bytes.Length
                && bytes[q + 4] == (byte)'N' && bytes[q + 5] == (byte)'i')
            {
                c.SeekTo(q);
                return true;
            }
        }
        return false;
    }
}
