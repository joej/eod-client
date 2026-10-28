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
        return ReadNetImmerse4x(bytes, version, blockCount);
    }

    // A rigid transform (rotation 3x3 + uniform scale + translation), applied to a point.
    private readonly struct Xform(float[] rot, float scale, Vector3 trans)
    {
        public static readonly Xform Identity = new([1, 0, 0, 0, 1, 0, 0, 0, 1], 1f, Vector3.Zero);
        public readonly float[] Rot = rot;
        public readonly float Scale = scale;
        public readonly Vector3 Trans = trans;

        public Vector3 Apply(Vector3 v)
        {
            float x = (Rot[0] * v.X + Rot[1] * v.Y + Rot[2] * v.Z) * Scale;
            float y = (Rot[3] * v.X + Rot[4] * v.Y + Rot[5] * v.Z) * Scale;
            float z = (Rot[6] * v.X + Rot[7] * v.Y + Rot[8] * v.Z) * Scale;
            return new Vector3(x + Trans.X, y + Trans.Y, z + Trans.Z);
        }

        // Rotate a direction (normals): rotation only, no translation, no scale.
        public Vector3 ApplyDir(Vector3 v) => new(
            Rot[0] * v.X + Rot[1] * v.Y + Rot[2] * v.Z,
            Rot[3] * v.X + Rot[4] * v.Y + Rot[5] * v.Z,
            Rot[6] * v.X + Rot[7] * v.Y + Rot[8] * v.Z);

        // world = parent ∘ local.
        public Xform Compose(Xform local)
        {
            float[] r = new float[9];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    r[row * 3 + col] =
                        Rot[row * 3 + 0] * local.Rot[0 * 3 + col] +
                        Rot[row * 3 + 1] * local.Rot[1 * 3 + col] +
                        Rot[row * 3 + 2] * local.Rot[2 * 3 + col];
            return new Xform(r, Scale * local.Scale, Apply(local.Trans));
        }
    }

    // A parsed NiNode or NiTriShape: its local transform and either its children (node) or its
    // geometry-data block index (shape). Other block types are recorded by type only.
    private sealed class Block
    {
        public required string Type;
        public required int Offset;
        public Xform Local = Xform.Identity;
        public int[] Children = [];
        public int DataRef = -1;      // NiTriShape -> its NiTriShapeData/NiTriStripsData block
        public int[] Props = [];      // NiTriShape -> its property refs (one is a NiTexturingProperty)
        public int BaseTextureRef = -1; // NiTexturingProperty -> its base NiSourceTexture block
        public string? TextureFile;   // for NiSourceTexture blocks
        public Vector4? MaterialColor; // NiMaterialProperty -> diffuse RGB + alpha (0..1)
    }

    // NetImmerse 4.x. Blocks reference each other by index and carry transforms in a NiNode/NiTriShape
    // hierarchy, so a multi-part model (a keep, a tree) only assembles correctly once each shape's
    // accumulated world transform is baked into its vertices. We index every block by scanning for
    // its type string (bodies aren't self-sized), parse the NiNode/NiTriShape transforms and refs,
    // walk from the scene root composing transforms, and bake them into the geometry each shape
    // points at. Verified against a 57-node/118-shape keep.
    private static NifModel ReadNetImmerse4x(ReadOnlySpan<byte> bytes, NifVersion version, int blockCount)
    {
        Block[] blocks = IndexBlocks4x(bytes, blockCount);

        // Parse the blocks we model (nodes, shapes, source textures). Geometry-data blocks are read
        // later, only for shapes that reference them.
        foreach (Block b in blocks)
        {
            var c = new NifCursor(bytes);
            c.SeekTo(b.Offset);
            try { c.SizedString(); } catch (NifFormatException) { continue; } // skip its type string
            switch (b.Type)
            {
                case "NiNode":
                case "NiLODNode":
                    if (TryReadAvObject(ref c, out Xform nx))
                    {
                        b.Local = nx;
                        int nch = (int)c.U32();
                        var ch = new int[Math.Clamp(nch, 0, 100000)];
                        for (int k = 0; k < ch.Length; k++) ch[k] = c.I32();
                        b.Children = ch;
                    }
                    break;
                case "NiTriShape":
                case "NiTriStrips":
                    if (TryReadAvObject(ref c, out Xform sx, out int[] props))
                    {
                        b.Local = sx;
                        b.Props = props;
                        b.DataRef = c.I32();   // NiGeometry: Data ref (the geometry-data block)
                    }
                    break;
                case "NiTexturingProperty":
                    b.BaseTextureRef = ReadTexturingPropertyBaseRef(ref c);
                    break;
                case "NiSourceTexture":
                    b.TextureFile = ReadSourceTextureFileName(ref c, bytes);
                    break;
                case "NiMaterialProperty":
                    b.MaterialColor = ReadMaterialPropertyColor(ref c);
                    break;
            }
        }

        // Walk from the scene root (block 0) composing transforms; bake each shape's world transform
        // into the geometry it references.
        var meshes = new List<NifMesh>();
        var seen = new HashSet<int>();
        Walk4x(bytes, blocks, 0, Xform.Identity, seen, meshes);

        // Fallback: bake any shape not reached from the root (a file whose root isn't block 0, or a
        // shape outside the hierarchy) at its own local transform, so we still get its geometry.
        for (int i = 0; i < blocks.Length; i++)
        {
            Block b = blocks[i];
            if ((b.Type is "NiTriShape" or "NiTriStrips") && !seen.Contains(i))
            {
                NifMesh? geom = ReadGeometryAt(bytes, blocks, b.DataRef);
                if (geom is not null)
                    meshes.Add(BakeTransform(geom, b.Local, $"mesh{i}",
                        ShapeTexture(blocks, b), ShapeMaterialColor(blocks, b)));
            }
        }

        if (meshes.Count == 0)
            throw new NifFormatException("No NiTriShapeData geometry found in NIF.");

        return new NifModel { Version = version, Meshes = meshes };
    }

    // Recursively walks the node hierarchy, composing transforms and baking each reached shape's
    // geometry at its world transform. A static method (not a local function) because `bytes` is a
    // ref-like span that can't be captured in a closure.
    private static void Walk4x(ReadOnlySpan<byte> bytes, Block[] blocks, int index, Xform world,
        HashSet<int> seen, List<NifMesh> meshes)
    {
        if (index < 0 || index >= blocks.Length || !seen.Add(index))
            return;
        Block b = blocks[index];
        Xform w = world.Compose(b.Local);
        if (b.Type is "NiNode" or "NiLODNode")
        {
            foreach (int child in b.Children)
                Walk4x(bytes, blocks, child, w, seen, meshes);
        }
        else if (b.Type is "NiTriShape" or "NiTriStrips")
        {
            NifMesh? geom = ReadGeometryAt(bytes, blocks, b.DataRef);
            if (geom is not null)
                meshes.Add(BakeTransform(geom, w, $"mesh{index}",
                    ShapeTexture(blocks, b), ShapeMaterialColor(blocks, b)));
        }
    }

    // Resolves a shape's own base texture: its property list holds a NiTexturingProperty whose base
    // TexDesc points at a NiSourceTexture. This is what makes a multi-material model (a keep uses ten
    // skins across its parts) wear the right texture on each shape, instead of one shared skin.
    // Returns null when the shape has no NiTexturingProperty — such shapes are NOT textured; they are
    // flat surfaces coloured by their NiMaterialProperty (open question W3a). We deliberately do NOT
    // fall back to the model's first texture: that mislabels every untextured shape with an unrelated
    // skin and, with no UVs, samples it at texel (0,0) — the "flat tan towers" bug. Verified across
    // keep.NIF + 34 other NIFs: a shape has either its own texturing property or none, so no shape
    // relies on an ancestor's property that this per-shape lookup would miss.
    private static string? ShapeTexture(Block[] blocks, Block shape)
    {
        foreach (int p in shape.Props)
        {
            if (p < 0 || p >= blocks.Length || blocks[p].Type != "NiTexturingProperty")
                continue;
            int src = blocks[p].BaseTextureRef;
            if (src >= 0 && src < blocks.Length && blocks[src].TextureFile is { } name)
                return name;
        }
        return null;
    }

    // Resolves a shape's flat surface colour from its NiMaterialProperty (diffuse RGB + alpha). Used
    // on untextured shapes (no NiTexturingProperty) so they render in the right colour instead of a
    // mis-sampled texture; on a textured shape it tints the texture (DAoC's keep skins are white, so
    // no visible tint). Null when the shape names no material property.
    private static Vector4? ShapeMaterialColor(Block[] blocks, Block shape)
    {
        foreach (int p in shape.Props)
        {
            if (p >= 0 && p < blocks.Length && blocks[p].Type == "NiMaterialProperty"
                && blocks[p].MaterialColor is { } color)
                return color;
        }
        return null;
    }

    // Bakes a world transform into a mesh's vertices and normals, attaching the texture name and the
    // shape's material colour.
    private static NifMesh BakeTransform(NifMesh mesh, Xform w, string name, string? texture,
        Vector4? materialColor)
    {
        var verts = new Vector3[mesh.Vertices.Length];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = w.Apply(mesh.Vertices[i]);
        Vector3[]? normals = null;
        if (mesh.Normals is not null)
        {
            normals = new Vector3[mesh.Normals.Length];
            for (int i = 0; i < normals.Length; i++)
                normals[i] = w.ApplyDir(mesh.Normals[i]);
        }
        return mesh with
        {
            Name = name,
            Vertices = verts,
            Normals = normals,
            TextureFile = mesh.TextureFile ?? texture,
            BaseColor = mesh.BaseColor ?? materialColor,
        };
    }

    // Reads the geometry-data block at `dataRef` (NiTriShapeData or NiTriStripsData), or null.
    private static NifMesh? ReadGeometryAt(ReadOnlySpan<byte> bytes, Block[] blocks, int dataRef)
    {
        if (dataRef < 0 || dataRef >= blocks.Length)
            return null;
        Block d = blocks[dataRef];
        var c = new NifCursor(bytes);
        c.SeekTo(d.Offset);
        try
        {
            c.SizedString(); // skip the type string
            return d.Type switch
            {
                "NiTriShapeData" => ReadTriShapeData(ref c, default, "geom"),
                "NiTriStripsData" => ReadTriStripsData4x(ref c, "geom"),
                _ => null,
            };
        }
        catch (NifFormatException)
        {
            return null;
        }
    }

    // Builds the block index by scanning for each block's type string: bodies aren't self-sized, so
    // a block ends where the next "Ni*" sized type string begins. Returns blocks[index] = (type,
    // offset-of-type-string). Verified to recover all blocks on real files.
    private static Block[] IndexBlocks4x(ReadOnlySpan<byte> bytes, int blockCount)
    {
        var list = new List<Block>(blockCount);
        int q = FindFirstTypeString(bytes);
        while (list.Count < blockCount && q >= 0 && q < bytes.Length)
        {
            string? type = TypeStringAt(bytes, q);
            if (type is null)
                break;
            list.Add(new Block { Type = type, Offset = q });
            int after = q + 4 + type.Length;
            q = NextTypeString(bytes, after);
        }
        return [.. list];
    }

    private static int FindFirstTypeString(ReadOnlySpan<byte> bytes)
    {
        // After the header line + version u32 + block-count u32, the first block's type string begins.
        int nl = bytes.IndexOf((byte)0x0A);
        if (nl < 0 || nl + 1 + 8 > bytes.Length)
            return -1;
        return nl + 1 + 8;
    }

    private static int NextTypeString(ReadOnlySpan<byte> bytes, int from)
    {
        for (int q = from; q + 6 <= bytes.Length; q++)
            if (TypeStringAt(bytes, q) is not null)
                return q;
        return -1;
    }

    // A valid block type string at q: u32 length in [3,40], then that many printable bytes starting "Ni".
    private static string? TypeStringAt(ReadOnlySpan<byte> bytes, int q)
    {
        if (q + 6 > bytes.Length)
            return null;
        uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(q, 4));
        if (len is < 3 or > 40 || q + 4 + (int)len > bytes.Length)
            return null;
        if (bytes[q + 4] != (byte)'N' || bytes[q + 5] != (byte)'i')
            return null;
        ReadOnlySpan<byte> s = bytes.Slice(q + 4, (int)len);
        foreach (byte ch in s)
            if (ch is < 0x20 or > 0x7E)
                return null;
        return System.Text.Encoding.Latin1.GetString(s);
    }

    // NiObjectNET + NiAVObject prefix shared by NiNode and NiTriShape at 4.x. Reads through the
    // transform and leaves the cursor at the first subclass field (children for NiNode, Data for
    // NiTriShape). Returns false if a bounding volume is present (we don't decode it) — the caller
    // then leaves the block's transform at identity rather than misparse.
    private static bool TryReadAvObject(ref NifCursor c, out Xform xform) =>
        TryReadAvObject(ref c, out xform, out _);

    // Reads NiObjectNET + NiAVObject, yielding the local transform and the block's property refs
    // (one of which, on a shape, is the NiTexturingProperty that names its base texture).
    private static bool TryReadAvObject(ref NifCursor c, out Xform xform, out int[] props)
    {
        xform = Xform.Identity;
        props = [];
        c.SizedString();           // NiObjectNET.Name
        c.I32();                   // Extra Data ref (single, until 4.2.2.0)
        c.I32();                   // Controller ref
        c.U16();                   // NiAVObject.Flags (ushort at <= 4.2.2.0)
        Vector3 trans = c.Vec3();
        float[] rot = new float[9];
        for (int i = 0; i < 9; i++) rot[i] = c.F32();
        float scale = c.F32();
        c.Vec3();                  // Velocity (until 4.2.2.0)
        int numProps = (int)c.U32();
        int[] p = new int[Math.Clamp(numProps, 0, 100000)];
        for (int i = 0; i < p.Length; i++) p[i] = c.I32();   // Property refs
        if (c.Bool())              // Has Bounding Volume (until 4.2.2.0)
            return false;          // present only rarely; bail to identity rather than misparse
        xform = new Xform(rot, scale, trans);
        props = p;
        return true;
    }

    // NiTexturingProperty body (4.x), positioned just past the type string. Layout (verified by
    // decode against a real multi-material building, cross-checked with nif.xml): NiObjectNET
    // (Name + Extra Data ref + Controller ref), Flags u16, Apply Mode u32, Texture Count u32,
    // then Has Base Texture (1-byte bool); if set, a base TexDesc whose first field is the Source
    // ref (i32 -> a NiSourceTexture). We take only that base source ref (the shape's main skin).
    private static int ReadTexturingPropertyBaseRef(ref NifCursor c)
    {
        try
        {
            c.SizedString();   // Name
            c.I32(); c.I32();  // Extra Data ref, Controller ref
            c.U16();           // Flags
            c.U32();           // Apply Mode
            c.U32();           // Texture Count
            if (!c.Bool())     // Has Base Texture
                return -1;
            return c.I32();    // base TexDesc: Source ref
        }
        catch (NifFormatException)
        {
            return -1;
        }
    }

    // NiMaterialProperty body (4.x), positioned just past the type string. Layout (verified by byte
    // dump against keep.NIF and 34 other building/item NIFs, matches nif.xml): NiObjectNET (Name +
    // Extra Data ref + Controller ref), Flags u16, then four Color3s — Ambient, Diffuse, Specular,
    // Emissive — then Glossiness f32 and Alpha f32. We keep the Diffuse colour and Alpha; a shape
    // with no NiTexturingProperty renders flat in this colour (open question W3a). Returns null on a
    // short/garbled read rather than throwing, so one odd block doesn't fail the whole file.
    private static Vector4? ReadMaterialPropertyColor(ref NifCursor c)
    {
        try
        {
            c.SizedString();            // Name
            c.I32(); c.I32();           // Extra Data ref, Controller ref
            c.U16();                    // Flags
            c.Vec3();                   // Ambient (discarded)
            Vector3 diffuse = c.Vec3(); // Diffuse
            c.Vec3();                   // Specular (discarded)
            c.Vec3();                   // Emissive (discarded)
            c.F32();                    // Glossiness
            float alpha = c.F32();      // Alpha
            return new Vector4(diffuse, alpha);
        }
        catch (NifFormatException)
        {
            return null;
        }
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

    // The geometry data shared by NiTriShapeData and NiTriStripsData at 4.x (NiGeometryData ->
    // NiTriBasedGeomData), read up to and including Num Triangles. Field order for 4.2.x (verified):
    //   Num Vertices u16; Has Vertices bool(1) + Vertices; Has Normals bool(1) + Normals; Bounding
    //   Sphere (centre+radius); Has Vertex Colors bool(1) + Color4s; Data Flags u16 (`until=4.2.2.0`,
    //   low 6 bits = UV-set count); UV sets; Num Triangles u16. No Keep/Compress bytes and no
    //   Consistency Flags (those are `since 10.x`); no Has-UV byte (that is `until=4.0.0.2`).
    private static void ReadGeometryCommon4x(
        ref NifCursor c, out int numVertices, out Vector3[] vertices,
        out Vector3[]? normals, out Vector2[]? texCoords, out int numTriangles)
    {
        numVertices = c.U16();
        vertices = c.Bool() ? ReadVec3Array(ref c, numVertices) : [];
        normals = c.Bool() ? ReadVec3Array(ref c, numVertices) : null;
        c.Vec3(); c.F32();           // Bounding sphere: centre + radius
        if (c.Bool())                // vertex colours (Color4), read and discard
            for (int k = 0; k < numVertices * 4; k++)
                c.F32();

        int uvSets = c.U16() & 0x3F; // Data Flags (until=4.2.2.0): low 6 bits = UV-set count
        texCoords = null;
        for (int s = 0; s < uvSets; s++)
        {
            Vector2[] set = new Vector2[numVertices];
            for (int v = 0; v < numVertices; v++)
                set[v] = new Vector2(c.F32(), c.F32());
            texCoords ??= set;       // keep the first UV set
        }

        numTriangles = c.U16();      // NiTriBasedGeomData
    }

    // NiTriShapeData at 4.2.x. After the shared geometry: Num Triangle Points u32 (=3x), triangles.
    // (`Has Triangles` is `since 10.1.0.0`, so at 4.2.x triangles follow directly.)
    private static NifMesh ReadTriShapeData(ref NifCursor c, NifVersion version, string name)
    {
        ReadGeometryCommon4x(ref c, out int numVertices, out Vector3[] vertices,
            out Vector3[]? normals, out Vector2[]? texCoords, out int numTriangles);

        uint numTrianglePoints = c.U32();
        if (numTrianglePoints != (uint)numTriangles * 3)
            throw new NifFormatException($"Triangle point count {numTrianglePoints} != 3 x {numTriangles}.");

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

    // NiTriStripsData at 4.x. After the shared geometry: Num Strips u16; Strip Lengths u16 x strips;
    // Points u16 (one array per strip). `Has Points` is `since 10.0.1.3`, so there is NO Has-Points
    // byte at 4.x — points follow Strip Lengths directly. Strips are converted to a triangle list.
    private static NifMesh ReadTriStripsData4x(ref NifCursor c, string name)
    {
        ReadGeometryCommon4x(ref c, out int numVertices, out Vector3[] vertices,
            out Vector3[]? normals, out Vector2[]? texCoords, out int _);

        int numStrips = c.U16();
        var lengths = new int[numStrips];
        for (int s = 0; s < numStrips; s++)
            lengths[s] = c.U16();

        var triangles = new List<(ushort, ushort, ushort)>();
        for (int s = 0; s < numStrips; s++)
        {
            int len = lengths[s];
            var strip = new ushort[len];
            for (int i = 0; i < len; i++)
            {
                ushort idx = c.U16();
                if (idx != 0xFFFF && idx >= numVertices)
                    throw new NifFormatException($"Strip index {idx} past {numVertices} vertices.");
                strip[i] = idx;
            }
            AppendStripTriangles(strip, triangles);
        }

        return new NifMesh
        {
            Name = name,
            Vertices = vertices,
            Normals = normals,
            TexCoords = texCoords,
            Triangles = triangles.ToArray(),
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
