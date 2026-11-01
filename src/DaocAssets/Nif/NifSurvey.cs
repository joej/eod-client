// SPDX-License-Identifier: GPL-3.0-or-later
//
// What a NIF contains, independent of what the converter does with it: the block types, whether
// we understand each one byte for byte, and the rendering features its blocks use. `mpk audit`
// runs this over a whole client install so gaps in the converter show up as a ranked list instead
// of as bugs seen in the world (see docs/nif-coverage.md).
//
// The understanding check is structural. A NetImmerse 4.x block has no size field; the reader finds
// block boundaries by scanning for the next type string. For every type in `DecodedTypes4x` the
// survey decodes the whole body from the field layouts in NifTools' nif.xml (GPL-3.0) and checks
// that the decode ends exactly where the next block begins (or where the file footer begins, for
// the last block). A type that decodes exactly on every block in the install is one whose layout
// we know completely; a mismatch is reported, never trusted. Gamebryo 10.1.0.0 files carry an exact
// block-type table in their header, so their type counts are exact, but block bodies are not
// decoded here (10.1 bodies have no boundaries to check against).

namespace DaocAssets.Nif;

/// <summary>How much of a block type the converter uses.</summary>
public enum NifCoverage
{
    /// <summary>Read and applied in full.</summary>
    Handled,
    /// <summary>Read, but some of its content is dropped; see <see cref="NifSurvey.CoverageNote"/>.</summary>
    Partial,
    /// <summary>Not read. The converter skips these blocks.</summary>
    Unhandled,
}

/// <summary>One NIF's contents (see the file header for what is checked).</summary>
public sealed class NifSurvey
{
    public required NifVersion Version { get; init; }
    /// <summary>Block count declared in the header.</summary>
    public required int DeclaredBlocks { get; init; }
    /// <summary>Block types in file order (4.x: as found by the type-string scan).</summary>
    public required IReadOnlyList<string> BlockTypes { get; init; }
    /// <summary>4.x: type strings the scan finds in the whole file, not stopping at the declared
    /// count. More than <see cref="DeclaredBlocks"/> means false "Ni…" matches inside block bodies.</summary>
    public int ScannedBlocks { get; init; }
    /// <summary>4.x: found block types that aren't plain identifiers ("Nicicle.tga") — certainly false
    /// matches inside a body, which split the real block and cut the block list short.</summary>
    public IReadOnlyList<string> FalseTypeNames { get; init; } = [];
    /// <summary>The block scan agrees with the header and found no impossible type names.</summary>
    public bool BoundariesMatchHeader => ScannedBlocks == DeclaredBlocks && FalseTypeNames.Count == 0;
    /// <summary>Per decoded type: blocks that decoded to a real block boundary further on, i.e. the
    /// decode was right and the scanner had split the block at a false "Ni…" string.</summary>
    public IReadOnlyDictionary<string, int> ScannerSplits { get; init; } = new Dictionary<string, int>();
    /// <summary>NetImmerse 2.x/3.x (copyright header, "Top Level Object" markers): the converter
    /// can't read these at all; types are listed from a type-string scan.</summary>
    public bool Legacy { get; init; }
    /// <summary>Per decoded type: blocks whose full decode ended exactly at the next block.</summary>
    public required IReadOnlyDictionary<string, int> ExactDecodes { get; init; }
    /// <summary>Per decoded type: blocks whose decode ended anywhere else or failed.</summary>
    public required IReadOnlyDictionary<string, int> FailedDecodes { get; init; }
    /// <summary>Rendering features found, as "Type: feature" → number of blocks.</summary>
    public required IReadOnlyDictionary<string, int> Features { get; init; }

    private static readonly NifVersion V1010 = new(10, 1, 0, 0);
    private static readonly NifVersion V41012 = new(4, 1, 0, 12);

    /// <summary>The 4.x types this survey decodes byte for byte.</summary>
    public static readonly IReadOnlySet<string> DecodedTypes4x = new HashSet<string>
    {
        "NiNode", "NiLODNode", "NiBillboardNode", "NiTriShape", "NiTriStrips", "NiTriShapeData",
        "NiTriStripsData", "NiTexturingProperty", "NiSourceTexture", "NiMaterialProperty",
        "NiAlphaProperty", "NiStencilProperty", "NiVertexColorProperty", "NiZBufferProperty",
        "NiSpecularProperty", "NiWireframeProperty", "NiShadeProperty", "NiDitherProperty",
    };

    /// <summary>
    /// How the converter (<see cref="NifFile.Read"/>) uses a block type. Keep this in step with the
    /// reader: it is what the audit reports and what conversion warns about.
    /// </summary>
    public static NifCoverage Coverage(string type, NifVersion version)
    {
        if (version.A < 4)
            return NifCoverage.Unhandled;   // NifFile.Read rejects 2.x/3.x files outright
        if (version >= V1010)
            return type switch
            {
                // 10.1 is a geometry scan: no node tree, transforms or per-shape properties.
                "NiTriShapeData" or "NiTriStripsData" => NifCoverage.Partial,
                "NiSourceTexture" => NifCoverage.Partial,
                _ => NifCoverage.Unhandled,
            };
        return type switch
        {
            "NiNode" or "NiTriShape" or "NiTriStrips" or "NiAlphaProperty" => NifCoverage.Handled,
            "NiLODNode" or "NiTriShapeData" or "NiTriStripsData" or "NiTexturingProperty"
                or "NiSourceTexture" or "NiMaterialProperty" or "NiStencilProperty" => NifCoverage.Partial,
            _ => NifCoverage.Unhandled,
        };
    }

    /// <summary>What a <see cref="NifCoverage.Partial"/> type loses in conversion.</summary>
    public static string CoverageNote(string type, NifVersion version)
    {
        if (version >= V1010)
            return type switch
            {
                "NiTriShapeData" or "NiTriStripsData" =>
                    "found by scanning, not through the node tree: no transforms, first UV set only",
                "NiSourceTexture" => "the file's first texture name is given to every mesh",
                _ => "",
            };
        return type switch
        {
            "NiLODNode" => "near level picked by node name; LOD ranges not read",
            "NiTriShapeData" or "NiTriStripsData" => "vertex colours and extra UV sets dropped",
            "NiTexturingProperty" => "base texture only; dark/detail/gloss/glow/bump/decal slots, " +
                "apply mode and clamp/filter/UV-set dropped",
            "NiSourceTexture" => "external file name only; embedded pixel data not decoded",
            "NiMaterialProperty" => "diffuse and alpha only; ambient/specular/emissive/glossiness dropped",
            "NiStencilProperty" => "draw mode (sidedness) only",
            _ => "",
        };
    }

    /// <summary>Surveys one NIF file. Throws <see cref="NifFormatException"/> if it isn't one.</summary>
    public static NifSurvey Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            throw new NifFormatException("Empty file.");
        var c = new NifCursor(bytes);
        string header = c.LineString();
        if (!header.Contains("File Format", StringComparison.Ordinal))
            throw new NifFormatException($"Not a NIF file (header: '{header}').");
        var legacy = System.Text.RegularExpressions.Regex.Match(header, @"Version ([23])\.(\d)");
        if (legacy.Success)
            return ReadLegacy(bytes, (byte)(legacy.Groups[1].Value[0] - '0'), (byte)(legacy.Groups[2].Value[0] - '0'));
        uint packed = c.U32();
        var version = new NifVersion((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        return version >= V1010 ? Read1010(ref c, version) : Read4x(bytes, ref c, version);
    }

    // NetImmerse 2.x/3.x: no block count; list the type strings that follow the header.
    private static NifSurvey ReadLegacy(ReadOnlySpan<byte> bytes, byte major, byte minor)
    {
        var types = new List<string>();
        int q = NifFile.NextTypeStringAt(bytes, 0);
        while (q >= 0)
        {
            string t = NifFile.TypeStringAtOffset(bytes, q)!;
            types.Add(t);
            q = NifFile.NextTypeStringAt(bytes, q + 4 + t.Length);
        }
        return new NifSurvey
        {
            Version = new NifVersion(major, minor, 0, 0),
            DeclaredBlocks = types.Count,
            ScannedBlocks = types.Count,
            BlockTypes = types,
            Legacy = true,
            ExactDecodes = new Dictionary<string, int>(),
            FailedDecodes = new Dictionary<string, int>(),
            Features = new Dictionary<string, int>(),
        };
    }

    private static NifSurvey Read1010(ref NifCursor c, NifVersion version)
    {
        c.U32();                                 // User Version
        int blockCount = checked((int)c.U32());
        int numTypes = c.U16();
        if (blockCount is < 0 or > 1_000_000 || numTypes > 10_000)
            throw new NifFormatException($"Implausible 10.1 header (blocks {blockCount}, types {numTypes}).");
        var names = new string[numTypes];
        for (int t = 0; t < numTypes; t++)
            names[t] = c.SizedString();
        var types = new List<string>(blockCount);
        for (int b = 0; b < blockCount; b++)
        {
            int index = c.U16();
            types.Add(index < names.Length ? names[index] : $"<bad type index {index}>");
        }
        return new NifSurvey
        {
            Version = version,
            DeclaredBlocks = blockCount,
            ScannedBlocks = blockCount,
            BlockTypes = types,
            ExactDecodes = new Dictionary<string, int>(),
            FailedDecodes = new Dictionary<string, int>(),
            Features = new Dictionary<string, int>(),
        };
    }

    private static NifSurvey Read4x(ReadOnlySpan<byte> bytes, ref NifCursor c, NifVersion version)
    {
        int declared = checked((int)c.U32());
        // Decode against the uncapped scan: the reader's scan stops at the declared count, so one false
        // split earlier in the file cuts off the real last block and every boundary after the split
        // shifts. Impossible type names (not identifiers) are false matches and are dropped here.
        (string Type, int Offset)[] all = NifFile.IndexBlocks(bytes, int.MaxValue);
        int scanned = all.Length;
        string[] falseNames = all.Select(b => b.Type).Where(t => !IsIdentifier(t)).ToArray();
        (string Type, int Offset)[] blocks = all.Where(b => IsIdentifier(b.Type)).ToArray();
        var exact = new Dictionary<string, int>();
        var failed = new Dictionary<string, int>();
        var splits = new Dictionary<string, int>();
        var features = new Dictionary<string, int>();
        var offsets = new HashSet<int>(blocks.Select(b => b.Offset));

        for (int i = 0; i < blocks.Length; i++)
        {
            (string type, int offset) = blocks[i];
            if (!DecodedTypes4x.Contains(type))
                continue;
            var b = new NifCursor(bytes);
            b.SeekTo(offset + 4 + type.Length);   // past the inline type string
            bool ok, split = false;
            try
            {
                Decode4x(ref b, type, version, features);
                ok = i + 1 < blocks.Length
                    ? b.Pos == blocks[i + 1].Offset
                    : IsFooterAt(bytes, b.Pos);
                // Ended past the next "block" but exactly on a later real one: the decode was right
                // and the scanner split this block at a false type string.
                split = !ok && i + 1 < blocks.Length && b.Pos > blocks[i + 1].Offset
                    && (offsets.Contains(b.Pos) || IsFooterAt(bytes, b.Pos)
                        || NifFile.TypeStringAtOffset(bytes, b.Pos) is not null);
            }
            catch (NifFormatException)
            {
                ok = false;
            }
            Bump(ok ? exact : split ? splits : failed, type);
        }

        return new NifSurvey
        {
            Version = version,
            DeclaredBlocks = declared,
            ScannedBlocks = scanned,
            FalseTypeNames = falseNames,
            BlockTypes = blocks.Select(b => b.Type).ToArray(),
            ExactDecodes = exact,
            FailedDecodes = failed,
            ScannerSplits = splits,
            Features = features,
        };
    }

    private static bool IsIdentifier(string t) => t.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    // The 4.x footer: Num Roots u32, then that many root refs, ending the file.
    private static bool IsFooterAt(ReadOnlySpan<byte> bytes, int pos)
    {
        if (pos + 4 > bytes.Length)
            return false;
        long roots = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos, 4));
        return pos + 4 + roots * 4 == bytes.Length;
    }

    private static void Bump(Dictionary<string, int> d, string key) =>
        d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;

    // Decodes one 4.x block body (after its type string) per nif.xml, counting features as it goes.
    private static void Decode4x(ref NifCursor c, string type, NifVersion v, Dictionary<string, int> f)
    {
        switch (type)
        {
            case "NiNode":
            case "NiBillboardNode":
            case "NiLODNode":
            {
                AvObject(ref c, type, f);
                uint children = c.U32();
                Skip(ref c, children * 4);
                uint effects = c.U32();
                Skip(ref c, effects * 4);
                if (effects > 0) Bump(f, $"{type}: has dynamic effects (lights/texture effects)");
                if (type == "NiLODNode")
                {
                    c.I32();                            // NiSwitchNode: Unknown Int 1
                    c.Vec3();                           // LOD Center (4.0.0.2 - 10.0.1.0)
                    uint levels = c.U32();
                    Skip(ref c, levels * 8);            // LODRange: near, far
                    Bump(f, $"NiLODNode: {levels} LOD level(s)");
                }
                break;
            }
            case "NiTriShape":
            case "NiTriStrips":
                AvObject(ref c, type, f);
                c.I32();                                // Data
                if (c.I32() >= 0) Bump(f, $"{type}: has skin instance (skinned)");
                break;
            case "NiTriShapeData":
            case "NiTriStripsData":
                GeometryData(ref c, type, f);
                break;
            case "NiTexturingProperty":
            {
                ObjectNet(ref c, type, f);
                c.U16();                                // Flags
                uint apply = c.U32();
                if (apply != 2) Bump(f, $"NiTexturingProperty: apply mode {ApplyModeName(apply)}");
                uint count = c.U32();
                string[] slots = ["base", "dark", "detail", "gloss", "glow", "bump", "decal 0"];
                foreach (string slot in slots)
                {
                    if (!c.Bool())
                        continue;
                    Bump(f, $"NiTexturingProperty: {slot} texture");
                    TexDesc(ref c, v, slot, f);
                    if (slot == "bump")
                        Skip(ref c, 2 * 4 + 4 * 4);     // luma scale, luma offset, 2x2 matrix
                }
                for (int d = 1; d <= 3; d++)            // decal 1-3 exist when Texture Count >= 7 + d
                {
                    if (count < 7 + d || !c.Bool())
                        continue;
                    Bump(f, $"NiTexturingProperty: decal {d} texture");
                    TexDesc(ref c, v, $"decal {d}", f);
                }
                break;
            }
            case "NiSourceTexture":
            {
                ObjectNet(ref c, type, f);
                byte external = c.U8();
                if (external == 1)
                {
                    c.SizedString();
                    Bump(f, "NiSourceTexture: external file");
                }
                else
                {
                    c.U8();                             // Unknown Byte (<= 10.0.1.0)
                    c.I32();                            // Pixel Data ref
                    Bump(f, "NiSourceTexture: EMBEDDED pixel data");
                }
                c.U32(); c.U32();                       // Pixel Layout, Use Mipmaps
                uint alphaFormat = c.U32();
                Bump(f, $"NiSourceTexture: alpha format {alphaFormat}");
                c.U8();                                 // Is Static
                break;
            }
            case "NiMaterialProperty":
            {
                ObjectNet(ref c, type, f);
                c.U16();
                System.Numerics.Vector3 ambient = c.Vec3(), diffuse = c.Vec3(), specular = c.Vec3(), emissive = c.Vec3();
                c.F32();                                // Glossiness
                float alpha = c.F32();
                if (emissive != System.Numerics.Vector3.Zero) Bump(f, "NiMaterialProperty: non-black emissive");
                if (alpha < 0.999f) Bump(f, "NiMaterialProperty: alpha < 1");
                _ = ambient; _ = diffuse; _ = specular;
                break;
            }
            case "NiAlphaProperty":
            {
                ObjectNet(ref c, type, f);
                ushort flags = c.U16();
                byte threshold = c.U8();
                (SurfaceAlpha mode, _) = NifFile.AlphaMode(flags, threshold);
                Bump(f, $"NiAlphaProperty: flags 0x{flags:X4} threshold {threshold} -> {mode}");
                break;
            }
            case "NiStencilProperty":
            {
                ObjectNet(ref c, type, f);
                c.U16();
                if (c.U8() != 0) Bump(f, "NiStencilProperty: stencil test enabled");
                Skip(ref c, 6 * 4);
                Bump(f, $"NiStencilProperty: draw mode {c.U32()}");
                break;
            }
            case "NiVertexColorProperty":
            {
                ObjectNet(ref c, type, f);
                c.U16();
                uint vertexMode = c.U32(), lightingMode = c.U32();
                Bump(f, $"NiVertexColorProperty: vertex mode {vertexMode}, lighting mode {lightingMode}");
                break;
            }
            case "NiZBufferProperty":
            {
                ObjectNet(ref c, type, f);
                ushort flags = c.U16();
                if (v >= V41012) c.U32();               // Function (4.1.0.12 - 20.0.0.5)
                Bump(f, $"NiZBufferProperty: flags 0x{flags:X4} (test {(flags & 1) != 0}, write {(flags & 2) != 0})");
                break;
            }
            case "NiSpecularProperty":
            case "NiWireframeProperty":
            case "NiShadeProperty":
            case "NiDitherProperty":
            {
                ObjectNet(ref c, type, f);
                ushort flags = c.U16();
                Bump(f, $"{type}: flags 0x{flags:X4}");
                break;
            }
        }
    }

    // NiObjectNET (4.x): Name, Extra Data ref, Controller ref.
    private static void ObjectNet(ref NifCursor c, string type, Dictionary<string, int> f)
    {
        c.SizedString();
        if (c.I32() >= 0) Bump(f, $"{type}: has extra data");
        if (c.I32() >= 0) Bump(f, $"{type}: has controller (animated)");
    }

    // NiAVObject (4.x): NiObjectNET, Flags, Translation, Rotation, Scale, Velocity, properties,
    // Has Bounding Box (+ BoundingBox: unknown u32, translation, rotation 3x3, radius).
    private static void AvObject(ref NifCursor c, string type, Dictionary<string, int> f)
    {
        ObjectNet(ref c, type, f);
        ushort flags = c.U16();
        if ((flags & 1) != 0) Bump(f, $"{type}: Hidden flag");
        Skip(ref c, 12 + 36 + 4 + 12);
        uint props = c.U32();
        Skip(ref c, props * 4);
        if (c.Bool())
        {
            Bump(f, $"{type}: has bounding box (reader currently drops this block's transform)");
            Skip(ref c, 4 + 12 + 36 + 12);
        }
    }

    // TexDesc (4.x): Source, Clamp Mode, Filter Mode, UV Set, PS2 L, PS2 K, Unknown1 (<= 4.1.0.12).
    private static void TexDesc(ref NifCursor c, NifVersion v, string slot, Dictionary<string, int> f)
    {
        c.I32();
        uint clamp = c.U32();
        c.U32();                                        // Filter Mode
        uint uvSet = c.U32();
        c.U16(); c.U16();                               // PS2 L, PS2 K
        if (v <= V41012) c.U16();                       // Unknown1
        if (clamp != 3) Bump(f, $"NiTexturingProperty: {slot} clamp mode {clamp} (not wrap)");
        if (uvSet != 0) Bump(f, $"NiTexturingProperty: {slot} uses UV set {uvSet}");
    }

    // NiGeometryData (4.x) + NiTriBasedGeomData + NiTriShapeData / NiTriStripsData.
    private static void GeometryData(ref NifCursor c, string type, Dictionary<string, int> f)
    {
        int nv = c.U16();
        if (c.Bool()) Skip(ref c, (uint)nv * 12);
        bool normals = c.Bool();
        if (normals) Skip(ref c, (uint)nv * 12);
        else Bump(f, $"{type}: no normals");
        Skip(ref c, 16);                                // Center, Radius
        if (c.Bool())
        {
            Bump(f, $"{type}: vertex colours");
            Skip(ref c, (uint)nv * 16);
        }
        int uvSets = c.U16() & 63;
        if (uvSets == 0) Bump(f, $"{type}: no UVs");
        if (uvSets > 1) Bump(f, $"{type}: {uvSets} UV sets");
        Skip(ref c, (uint)(uvSets * nv * 8));
        int triangles = c.U16();
        if (type == "NiTriShapeData")
        {
            c.U32();                                    // Num Triangle Points
            Skip(ref c, (uint)triangles * 6);
            int groups = c.U16();
            for (int g = 0; g < groups; g++)
                Skip(ref c, (uint)c.U16() * 2);
        }
        else
        {
            int strips = c.U16();
            var lengths = new int[strips];
            for (int s = 0; s < strips; s++) lengths[s] = c.U16();
            foreach (int len in lengths) Skip(ref c, (uint)len * 2);
        }
    }

    private static string ApplyModeName(uint mode) => mode switch
    {
        0 => "REPLACE", 1 => "DECAL", 2 => "MODULATE", 3 => "HILIGHT", 4 => "HILIGHT2", _ => mode.ToString(),
    };

    private static void Skip(ref NifCursor c, long count)
    {
        if (count < 0 || c.Pos + count > c.Length)
            throw new NifFormatException("Block runs past the end of the file.");
        c.SeekTo(c.Pos + (int)count);
    }
}
