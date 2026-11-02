// SPDX-License-Identifier: GPL-3.0-or-later
//
// The Gamebryo 10.1.0.0 half of the coverage survey: almost every figure (creature, player body part,
// skeleton) and a third of the .kfa animations are 10.1 files. Their header lists every block's type,
// but block bodies carry no size, so the only way to know where a block ends is to decode it. The
// survey decodes the blocks in file order using NifTools' nif.xml (GPL-3.0) layouts resolved for
// 10.1.0.0, and the check is that the decode of the last block ends exactly at the file footer
// (Num Roots + root refs, ending the file). Every reference along the way must point at a real
// block (or be -1) and every bool must be 0 or 1, so a wrong layout fails close to where it goes
// wrong rather than only at the end. A file with a type we don't decode stops there: its blocks
// are reported as unchecked, not as passed.
//
// Field gates that differ from 4.x at 10.1.0.0 (nif.xml): NiObjectNET has an extra-data *list*
// (count + refs) instead of a chain; NiAVObject drops Velocity and the bounding box and gains a
// Collision Object ref; NiGeometry gains Has Shader (+ name, int); NiMaterialProperty and
// NiTexturingProperty lose their Flags; TexDesc loses Unknown1 and gains a texture transform;
// NiSourceTexture gains a ref after the file name; NiSkinInstance has no partition ref (that comes
// at 10.2); NiSkinPartition gains Has Vertex Map / Has Vertex Weights / Has Faces bools; NiExtraData
// gains a Name and loses Next Extra Data. Not in nif.xml: every block starts with a u32 zero.

namespace DaocAssets.Nif;

public sealed partial class NifSurvey
{
    /// <summary>The 10.1.0.0 types this survey decodes byte for byte.</summary>
    public static readonly IReadOnlySet<string> DecodedTypes1010 = new HashSet<string>
    {
        "NiNode", "NiTriShape", "NiTriStrips", "NiTriShapeData", "NiTriStripsData",
        "NiSkinInstance", "NiSkinData", "NiSkinPartition",
        "NiMaterialProperty", "NiTexturingProperty", "NiSourceTexture", "NiVertexColorProperty",
        "NiZBufferProperty", "NiDitherProperty", "NiWireframeProperty", "NiSpecularProperty",
        "NiShadeProperty", "NiAlphaProperty", "NiStencilProperty",
        "NiKeyframeController", "NiKeyframeData", "NiGeomMorpherController", "NiMorphData",
        "NiStringExtraData", "NiStringsExtraData", "NiIntegerExtraData", "NiIntegersExtraData",
        "NiFloatExtraData", "NiBooleanExtraData", "NiColorExtraData", "NiTextKeyExtraData",
    };

    /// <summary>How a sequential 10.1 decode ended.</summary>
    public enum SequentialDecode
    {
        /// <summary>Not a 10.1 file.</summary>
        NotApplicable,
        /// <summary>Every block decoded and the last one ended exactly at the footer.</summary>
        Complete,
        /// <summary>Reached a block type the survey doesn't decode (<see cref="StopType"/>).</summary>
        Stopped,
        /// <summary>A decode broke a check (bad ref, bool, size) or ended off the footer.</summary>
        Failed,
    }

    /// <summary>10.1: how the sequential decode ended.</summary>
    public SequentialDecode Sequential { get; init; }
    /// <summary>10.1: the type being decoded (Failed) or the first undecoded type (Stopped);
    /// "(footer)" when every block decoded but the end missed the footer.</summary>
    public string? StopType { get; init; }
    /// <summary>10.1: why the decode failed, for the report.</summary>
    public string? StopReason { get; init; }

    private static NifSurvey Read1010(ReadOnlySpan<byte> bytes, ref NifCursor c, NifVersion version)
    {
        uint userVersion = c.U32();
        int blockCount = checked((int)c.U32());
        // User Version 2 and Export Info follow only for user version >= 10 or 1; DAoC writes 0.
        if (userVersion is >= 10 or 1)
            throw new NifFormatException($"10.1 user version {userVersion} (export-info header) is not supported.");
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
        c.U32();                                 // Unknown Int 2 (10.0.1.0 and later; 0 in DAoC's files)

        var features = new Dictionary<string, int>();
        var exact = new Dictionary<string, int>();
        var failed = new Dictionary<string, int>();
        SequentialDecode result = SequentialDecode.Complete;
        string? stopType = null, stopReason = null;
        var blockFeatures = new Dictionary<string, int>();
        for (int i = 0; i < types.Count; i++)
        {
            string type = types[i];
            if (!DecodedTypes1010.Contains(type))
            {
                (result, stopType) = (SequentialDecode.Stopped, type);
                break;
            }
            try
            {
                // Each 10.0-10.1 block starts with a u32 0 that nif.xml doesn't list (seen in every
                // DAoC 10.1 file; OfflineDAoC's reader skips it too).
                if (c.U32() != 0)
                    throw new NifFormatException("Block does not start with the 10.1 zero marker.");
                Decode1010(ref c, type, types, blockFeatures);
            }
            catch (NifFormatException e)
            {
                (result, stopType, stopReason) = (SequentialDecode.Failed, type, $"block {i}: {e.Message}");
                Bump(failed, type);
                break;
            }
        }
        if (result == SequentialDecode.Complete && !IsFooter1010(bytes, c.Pos, blockCount))
        {
            (result, stopType, stopReason) = (SequentialDecode.Failed, "(footer)",
                $"decode ended at {c.Pos} of {bytes.Length}, not at the footer");
            Bump(failed, types.Count > 0 ? types[^1] : "(footer)");
        }
        if (result == SequentialDecode.Complete)
            foreach (string t in types)
                Bump(exact, t);
        // Features from a decode that went wrong are noise; keep them only from complete files.
        if (result == SequentialDecode.Complete)
            foreach ((string k, int n) in blockFeatures)
                features["[10.1] " + k] = n;

        return new NifSurvey
        {
            Version = version,
            DeclaredBlocks = blockCount,
            ScannedBlocks = blockCount,
            BlockTypes = types,
            ExactDecodes = exact,
            FailedDecodes = failed,
            Features = features,
            Sequential = result,
            StopType = stopType,
            StopReason = stopReason,
        };
    }

    // The 10.1 footer: Num Roots u32, then that many refs to real blocks, ending the file.
    private static bool IsFooter1010(ReadOnlySpan<byte> bytes, int pos, int blockCount)
    {
        if (!IsFooterAt(bytes, pos))
            return false;
        uint roots = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos, 4));
        for (int r = 0; r < roots; r++)
        {
            int root = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos + 4 + 4 * r, 4));
            if (root < 0 || root >= blockCount)
                return false;
        }
        return true;
    }

    // Decodes one 10.1.0.0 block body per nif.xml, counting features as it goes. Throws
    // NifFormatException on any check that fails.
    private static void Decode1010(ref NifCursor c, string type, IReadOnlyList<string> types, Dictionary<string, int> f)
    {
        int nb = types.Count;
        switch (type)
        {
            case "NiNode":
            {
                AvObject1010(ref c, type, nb, f);
                RefList(ref c, nb);                     // Children
                if (RefList(ref c, nb) > 0) Bump(f, "NiNode: has dynamic effects");
                break;
            }
            case "NiTriShape":
            case "NiTriStrips":
            {
                AvObject1010(ref c, type, nb, f);
                Ref(ref c, nb);                         // Data
                if (Ref(ref c, nb) >= 0) Bump(f, $"{type}: skinned");
                if (StrictBool(ref c))
                {
                    Bump(f, $"{type}: shader '{c.SizedString()}'");
                    c.I32();                            // Unknown Integer
                }
                break;
            }
            case "NiTriShapeData":
            case "NiTriStripsData":
                GeometryData1010(ref c, type, f);
                break;
            case "NiSkinInstance":
            {
                Ref(ref c, nb);                         // Data
                // Skeleton Root and Bones are pointers, but a separate body part (fig3 parts) is
                // skinned to a skeleton in another file: its bone pointers don't reach bone nodes
                // here, and the bones are found by name ("bonelist" NiStringsExtraData). Read them
                // raw and report whether they do.
                int root = c.I32();
                uint bones = c.U32();
                if (bones > 1000) throw new NifFormatException($"Implausible bone count {bones}.");
                var distinct = new HashSet<int>();
                bool notNodes = false;
                for (int b = 0; b < bones; b++)
                {
                    int bone = c.I32();
                    notNodes |= bone < 0 || bone >= nb || types[bone] != "NiNode";
                    distinct.Add(bone);
                }
                _ = root;
                Bump(f, $"NiSkinInstance: {Bucket((int)bones)} bones");
                Bump(f, notNodes ? "NiSkinInstance: bone pointers outside this file's nodes"
                    : distinct.Count < bones ? "NiSkinInstance: bone pointers repeat (bones bound by name to a separate skeleton)"
                    : "NiSkinInstance: one distinct bone node per bone");
                break;
            }
            case "NiSkinData":
            {
                Skip(ref c, 52);                        // Skin Transform: rotation, translation, scale
                uint bones = c.U32();
                if (bones > 1000) throw new NifFormatException($"Implausible bone count {bones}.");
                Ref(ref c, nb);                         // Skin Partition (4.0.0.2 - 10.1.0.0)
                byte hasWeights = c.U8();
                if (hasWeights > 1) throw new NifFormatException($"Has Vertex Weights {hasWeights}.");
                if (hasWeights == 0) Bump(f, "NiSkinData: no vertex weights (partition only)");
                var influences = new Dictionary<int, (int Count, float Sum)>();
                for (int b = 0; b < bones; b++)
                {
                    Skip(ref c, 52 + 16);               // Skin Transform, bounding sphere
                    int nv = c.U16();
                    if (hasWeights == 0)
                        continue;
                    for (int v = 0; v < nv; v++)
                    {
                        int index = c.U16();
                        float w = c.F32();
                        influences.TryGetValue(index, out var e);
                        influences[index] = (e.Count + 1, e.Sum + w);
                    }
                }
                if (influences.Count > 0)
                {
                    Bump(f, $"NiSkinData: up to {influences.Values.Max(e => e.Count)} bone(s) per vertex");
                    if (influences.Values.Any(e => Math.Abs(e.Sum - 1) > 0.01f))
                        Bump(f, "NiSkinData: some vertex weights don't sum to 1");
                }
                break;
            }
            case "NiSkinPartition":
            {
                uint parts = c.U32();
                if (parts > 1000) throw new NifFormatException($"Implausible partition count {parts}.");
                Bump(f, $"NiSkinPartition: {parts} partition(s)");
                for (int p = 0; p < parts; p++)
                {
                    int nv = c.U16(), nt = c.U16(), nbones = c.U16(), ns = c.U16(), wpv = c.U16();
                    Bump(f, $"NiSkinPartition: {wpv} weight(s) per vertex");
                    Skip(ref c, nbones * 2);
                    if (StrictBool(ref c)) Skip(ref c, nv * 2);              // Vertex Map
                    if (StrictBool(ref c)) Skip(ref c, (long)nv * wpv * 4);  // Vertex Weights
                    var lengths = new int[ns];
                    for (int s = 0; s < ns; s++) lengths[s] = c.U16();
                    if (StrictBool(ref c))                                    // Has Faces
                        Skip(ref c, ns != 0 ? lengths.Sum() * 2L : nt * 6L);
                    if (StrictBool(ref c)) Skip(ref c, (long)nv * wpv);      // Bone Indices
                }
                break;
            }
            case "NiMaterialProperty":
            {
                ObjectNet1010(ref c, type, nb, f);
                System.Numerics.Vector3 ambient = c.Vec3(), diffuse = c.Vec3(), specular = c.Vec3(), emissive = c.Vec3();
                c.F32();                                // Glossiness
                float alpha = c.F32();
                if (emissive != System.Numerics.Vector3.Zero) Bump(f, "NiMaterialProperty: non-black emissive");
                if (alpha < 0.999f) Bump(f, "NiMaterialProperty: alpha < 1");
                _ = ambient; _ = diffuse; _ = specular;
                break;
            }
            case "NiTexturingProperty":
            {
                ObjectNet1010(ref c, type, nb, f);
                uint apply = c.U32();
                if (apply != 2) Bump(f, $"NiTexturingProperty: apply mode {ApplyModeName(apply)}");
                uint count = c.U32();
                string[] slots = ["base", "dark", "detail", "gloss", "glow", "bump", "decal 0"];
                foreach (string slot in slots)
                {
                    if (!StrictBool(ref c))
                        continue;
                    Bump(f, $"NiTexturingProperty: {slot} texture");
                    TexDesc1010(ref c, nb, slot, f);
                    if (slot == "bump")
                        Skip(ref c, 2 * 4 + 4 * 4);     // luma scale, luma offset, 2x2 matrix
                }
                for (int d = 1; d <= 3; d++)
                {
                    if (count < 7 + d || !StrictBool(ref c))
                        continue;
                    Bump(f, $"NiTexturingProperty: decal {d} texture");
                    TexDesc1010(ref c, nb, $"decal {d}", f);
                }
                uint shaderTextures = c.U32();
                if (shaderTextures > 64) throw new NifFormatException($"Implausible shader texture count {shaderTextures}.");
                for (int s = 0; s < shaderTextures; s++)
                {
                    if (!StrictBool(ref c))
                        continue;
                    Bump(f, "NiTexturingProperty: shader texture");
                    TexDesc1010(ref c, nb, "shader", f);
                    c.U32();                            // Map Index
                }
                break;
            }
            case "NiSourceTexture":
            {
                ObjectNet1010(ref c, type, nb, f);
                byte external = c.U8();
                if (external > 1) throw new NifFormatException($"Use External {external}.");
                c.SizedString();                        // File Name (both cases at 10.1)
                Ref(ref c, nb);                         // Unknown Link / Pixel Data
                Bump(f, external == 1 ? "NiSourceTexture: external file" : "NiSourceTexture: EMBEDDED pixel data");
                c.U32(); c.U32();                       // Pixel Layout, Use Mipmaps
                Bump(f, $"NiSourceTexture: alpha format {c.U32()}");
                c.U8();                                 // Is Static
                break;
            }
            case "NiVertexColorProperty":
            {
                ObjectNet1010(ref c, type, nb, f);
                c.U16();
                uint vertexMode = c.U32(), lightingMode = c.U32();
                Bump(f, $"NiVertexColorProperty: vertex mode {vertexMode}, lighting mode {lightingMode}");
                break;
            }
            case "NiZBufferProperty":
            {
                ObjectNet1010(ref c, type, nb, f);
                ushort flags = c.U16();
                c.U32();                                // Function
                Bump(f, $"NiZBufferProperty: flags 0x{flags:X4}");
                break;
            }
            case "NiDitherProperty":
            case "NiWireframeProperty":
            case "NiSpecularProperty":
            case "NiShadeProperty":
            {
                ObjectNet1010(ref c, type, nb, f);
                Bump(f, $"{type}: flags 0x{c.U16():X4}");
                break;
            }
            case "NiAlphaProperty":
            {
                ObjectNet1010(ref c, type, nb, f);
                ushort flags = c.U16();
                byte threshold = c.U8();
                (SurfaceAlpha mode, _) = NifFile.AlphaMode(flags, threshold);
                Bump(f, $"NiAlphaProperty: flags 0x{flags:X4} threshold {threshold} -> {mode}");
                break;
            }
            case "NiStencilProperty":
            {
                ObjectNet1010(ref c, type, nb, f);
                if (c.U8() != 0) Bump(f, "NiStencilProperty: stencil test enabled");
                Skip(ref c, 6 * 4);
                Bump(f, $"NiStencilProperty: draw mode {c.U32()}");
                break;
            }
            case "NiKeyframeController":
                TimeController(ref c, nb);
                Ref(ref c, nb);                         // Data
                break;
            case "NiGeomMorpherController":
                TimeController(ref c, nb);
                c.U16();                                // Extra Flags
                Ref(ref c, nb);                         // Data
                c.U8();                                 // Always Update
                break;
            case "NiKeyframeData":
                KeyframeData(ref c, f);
                break;
            case "NiMorphData":
            {
                uint morphs = c.U32(), nv = c.U32();
                if (morphs > 1000 || nv > 65535) throw new NifFormatException($"Implausible morph data ({morphs} x {nv}).");
                c.U8();                                 // Relative Targets
                Bump(f, $"NiMorphData: {Bucket((int)morphs)} morph(s)");
                for (int m = 0; m < morphs; m++)
                {
                    uint keys = c.U32();
                    uint interp = c.U32();              // present even with no keys
                    Keys(ref c, keys, interp, 4);
                    Skip(ref c, nv * 12L);              // Vectors
                }
                break;
            }
            case "NiStringExtraData":
                Bump(f, $"NiStringExtraData: named '{Fold(c.SizedString())}'");
                c.SizedString();
                break;
            case "NiStringsExtraData":
            {
                Bump(f, $"NiStringsExtraData: named '{Fold(c.SizedString())}'");
                uint n = c.U32();
                if (n > 10000) throw new NifFormatException($"Implausible string count {n}.");
                for (int s = 0; s < n; s++) c.SizedString();
                break;
            }
            case "NiIntegersExtraData":
            {
                Bump(f, $"NiIntegersExtraData: named '{Fold(c.SizedString())}'");
                Skip(ref c, c.U32() * 4L);
                break;
            }
            case "NiIntegerExtraData":
            case "NiFloatExtraData":
                c.SizedString();
                c.U32();
                break;
            case "NiBooleanExtraData":
                c.SizedString();
                c.U8();
                break;
            case "NiColorExtraData":
                c.SizedString();
                Skip(ref c, 16);
                break;
            case "NiTextKeyExtraData":
            {
                c.SizedString();
                uint n = c.U32();
                if (n > 10000) throw new NifFormatException($"Implausible text key count {n}.");
                for (int k = 0; k < n; k++) { c.F32(); c.SizedString(); }
                Bump(f, "NiTextKeyExtraData: text keys (clip markers)");
                break;
            }
            default:
                throw new NifFormatException($"No 10.1 decode for {type}.");
        }
    }

    // NiObjectNET (10.1): Name, Num Extra Data List + refs, Controller.
    private static void ObjectNet1010(ref NifCursor c, string type, int nb, Dictionary<string, int> f)
    {
        c.SizedString();
        if (RefList(ref c, nb) > 0) Bump(f, $"{type}: has extra data");
        if (Ref(ref c, nb) >= 0) Bump(f, $"{type}: has controller (animated)");
    }

    // NiAVObject (10.1): NiObjectNET, Flags, Translation, Rotation, Scale, properties, Collision Object.
    private static void AvObject1010(ref NifCursor c, string type, int nb, Dictionary<string, int> f)
    {
        ObjectNet1010(ref c, type, nb, f);
        ushort flags = c.U16();
        if ((flags & 1) != 0) Bump(f, $"{type}: Hidden flag");
        Skip(ref c, 12 + 36 + 4);
        RefList(ref c, nb);                             // Properties
        if (Ref(ref c, nb) >= 0) Bump(f, $"{type}: has collision object");
    }

    // TexDesc (10.1): Source, Clamp, Filter, UV Set, PS2 L, PS2 K, Has Texture Transform (+ 32 bytes).
    private static void TexDesc1010(ref NifCursor c, int nb, string slot, Dictionary<string, int> f)
    {
        Ref(ref c, nb);
        uint clamp = c.U32();
        c.U32();                                        // Filter Mode
        uint uvSet = c.U32();
        c.U16(); c.U16();                               // PS2 L, PS2 K
        if (StrictBool(ref c))
        {
            Bump(f, $"NiTexturingProperty: {slot} texture transform");
            Skip(ref c, 8 + 8 + 4 + 4 + 8);             // translation, tiling, W rotation, type, centre
        }
        if (clamp != 3) Bump(f, $"NiTexturingProperty: {slot} clamp mode {clamp} (not wrap)");
        if (uvSet != 0) Bump(f, $"NiTexturingProperty: {slot} uses UV set {uvSet}");
    }

    // NiGeometryData + NiTriBasedGeomData + NiTriShapeData / NiTriStripsData at 10.1.0.0 (the same
    // layout NifFile's 10.1 geometry reader uses).
    private static void GeometryData1010(ref NifCursor c, string type, Dictionary<string, int> f)
    {
        int nv = c.U16();
        c.U8(); c.U8();                                 // Keep Flags, Compress Flags
        if (StrictBool(ref c)) Skip(ref c, nv * 12L);
        int dataFlags = c.U16();
        int uvSets = dataFlags & 0x3F;
        bool tangents = (dataFlags & 0x1000) != 0;
        bool normals = StrictBool(ref c);
        if (normals) Skip(ref c, nv * 12L * (tangents ? 3 : 1));
        else Bump(f, $"{type}: no normals");
        if (normals && tangents) Bump(f, $"{type}: tangents");
        Skip(ref c, 16);                                // Center, Radius
        if (StrictBool(ref c))
        {
            Bump(f, $"{type}: vertex colours");
            Skip(ref c, nv * 16L);
        }
        if (uvSets == 0) Bump(f, $"{type}: no UVs");
        if (uvSets > 1) Bump(f, $"{type}: {uvSets} UV sets");
        Skip(ref c, (long)uvSets * nv * 8);
        c.U16();                                        // Consistency Flags
        int triangles = c.U16();
        if (type == "NiTriShapeData")
        {
            if (c.U32() != (uint)triangles * 3) throw new NifFormatException("Num Triangle Points != 3 x triangles.");
            if (StrictBool(ref c)) Skip(ref c, triangles * 6L);
            int groups = c.U16();
            for (int g = 0; g < groups; g++)
                Skip(ref c, c.U16() * 2L);
        }
        else
        {
            int strips = c.U16();
            var lengths = new int[strips];
            for (int s = 0; s < strips; s++) lengths[s] = c.U16();
            if (StrictBool(ref c))
                foreach (int len in lengths) Skip(ref c, len * 2L);
        }
    }

    // NiTimeController (10.1, as 4.x): Next Controller, Flags, Frequency, Phase, Start, Stop, Target.
    private static void TimeController(ref NifCursor c, int nb)
    {
        Ref(ref c, nb);
        c.U16();
        Skip(ref c, 16);
        Ref(ref c, nb);
    }

    // NiKeyframeData (4.x and 10.1.0.0 share the layout): rotation keys (quaternion, or three
    // float key groups when the type is XYZ), translation key group, scale key group.
    private static void KeyframeData(ref NifCursor c, Dictionary<string, int> f)
    {
        uint rotations = c.U32();
        if (rotations > 0)
        {
            uint rtype = c.U32();
            Bump(f, $"NiKeyframeData: rotation keys {KeyTypeName(rtype)}");
            if (rtype == 4)
            {
                c.F32();                                // Unknown Float (<= 10.1.0.0)
                for (int axis = 0; axis < 3; axis++)
                    KeyGroup(ref c, 4, f, null);
            }
            else
            {
                if (rtype is < 1 or > 5) throw new NifFormatException($"Rotation key type {rtype}.");
                if (rotations > 100_000) throw new NifFormatException($"Implausible key count {rotations}.");
                Skip(ref c, rotations * (4L + 16 + (rtype == 3 ? 12 : 0)));
            }
        }
        KeyGroup(ref c, 12, f, "translation");
        KeyGroup(ref c, 4, f, "scale");
    }

    private static void KeyGroup(ref NifCursor c, int valueSize, Dictionary<string, int> f, string? what)
    {
        uint n = c.U32();
        if (n == 0)
            return;
        uint ktype = c.U32();
        if (what is not null) Bump(f, $"NiKeyframeData: {what} keys {KeyTypeName(ktype)}");
        Keys(ref c, n, ktype, valueSize);
    }

    // n keys of one interpolation type: time, value, + forward/backward (quadratic) or TBC.
    private static void Keys(ref NifCursor c, uint n, uint ktype, int valueSize)
    {
        if (n == 0)
            return;
        if (ktype is < 1 or > 5) throw new NifFormatException($"Key type {ktype}.");
        if (n > 100_000) throw new NifFormatException($"Implausible key count {n}.");
        long size = 4 + valueSize + (ktype == 2 ? 2L * valueSize : 0) + (ktype == 3 ? 12 : 0);
        Skip(ref c, n * size);
    }

    private static string KeyTypeName(uint t) => t switch
    {
        1 => "LINEAR", 2 => "QUADRATIC", 3 => "TBC", 4 => "XYZ", 5 => "CONST", _ => $"type {t}",
    };

    // A ref or ptr: -1 or a block index.
    private static int Ref(ref NifCursor c, int nb)
    {
        int r = c.I32();
        if (r < -1 || r >= nb)
            throw new NifFormatException($"Reference {r} outside the file's {nb} blocks.");
        return r;
    }

    private static int RefList(ref NifCursor c, int nb)
    {
        uint n = c.U32();
        if (n > (uint)nb) throw new NifFormatException($"Reference list of {n} in a file of {nb} blocks.");
        for (int i = 0; i < n; i++)
            Ref(ref c, nb);
        return (int)n;
    }

    private static bool StrictBool(ref NifCursor c)
    {
        byte b = c.U8();
        return b switch
        {
            0 => false,
            1 => true,
            _ => throw new NifFormatException($"Bool byte {b}."),
        };
    }

    // Extra-data names fold their digits ("Name12" -> "Name#") so the report groups them.
    private static string Fold(string name) => System.Text.RegularExpressions.Regex.Replace(name, "[0-9]+", "#");

    private static string Bucket(int n) => n switch
    {
        <= 1 => n.ToString(),
        <= 10 => "2-10",
        <= 30 => "11-30",
        <= 60 => "31-60",
        _ => "61+",
    };
}
