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

    public static NifModel Read(ReadOnlySpan<byte> bytes)
    {
        var c = new NifCursor(bytes);
        string header = c.LineString();
        if (!header.Contains("File Format", StringComparison.Ordinal))
            throw new NifFormatException($"Not a NIF file (header: '{header}').");

        // 4.2.x: version u32 then block count u32. 10.1.0.0 has a different header (endian byte +
        // a block-type table) — out of scope here until decoded.
        uint packed = c.U32();
        var version = new NifVersion((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        if (version >= V1010)
            throw new NifFormatException($"NIF version {version} (Gamebryo 10.x) is not supported yet; only NetImmerse 4.x.");
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
