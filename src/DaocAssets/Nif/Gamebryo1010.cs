// SPDX-License-Identifier: GPL-3.0-or-later
//
// Gamebryo 10.1.0.0 parser: almost every figure (creature, player body part, skeleton), a third of the
// .kfa animations, and about half of the zone and item models are 10.1 files. Their header lists every
// block's type, but block bodies carry no size, so the only way to know where a block ends is to
// decode it. This parser decodes the blocks in file order with NifTools' nif.xml (GPL-3.0) layouts
// resolved for 10.1.0.0, keeping what the converter uses (nodes, shapes, geometry, skins, surface
// properties, bone lists). It is the one place 10.1 layouts live: the converter (NifFile) builds models
// from its blocks, and the coverage survey (NifSurvey) runs it over the whole install and checks that
// the last block ends exactly at the file footer (Num Roots + root refs, ending the file). Every
// reference along the way must point at a real block (or be -1) and every bool must be 0 or 1, so a
// wrong layout fails close to where it goes wrong. A file with a type we don't decode stops there.
//
// Field gates that differ from 4.x at 10.1.0.0 (nif.xml): NiObjectNET has an extra-data *list*
// (count + refs) instead of a chain; NiAVObject drops Velocity and the bounding box and gains a
// Collision Object ref; NiGeometry gains Has Shader (+ name, int); NiMaterialProperty and
// NiTexturingProperty lose their Flags; TexDesc loses Unknown1 and gains a texture transform;
// NiSourceTexture gains a ref after the file name; NiSkinInstance has no partition ref (that comes
// at 10.2); NiSkinPartition gains Has Vertex Map / Has Vertex Weights / Has Faces bools; NiExtraData
// gains a Name and loses Next Extra Data. Not in nif.xml: every block starts with a u32 zero.

using System.Numerics;

namespace DaocAssets.Nif;

/// <summary>A NiAVObject or skin transform as stored: rotation (3x3, file order), translation, scale.</summary>
internal readonly record struct NifTransform(float[] Rot, Vector3 Trans, float Scale)
{
    public static readonly NifTransform Identity = new([1, 0, 0, 0, 1, 0, 0, 0, 1], Vector3.Zero, 1f);
}

/// <summary>One decoded 10.1 block; which fields are set depends on <see cref="Type"/>.</summary>
internal sealed class Gb10Block
{
    public required string Type;
    // NiObjectNET / NiAVObject
    public string Name = "";
    public ushort Flags;
    public NifTransform Local = NifTransform.Identity;
    public int[] Props = [];
    public int[] Extra = [];
    public int Controller = -1;
    // Where the block's body starts in the file (after the 10.1 zero marker).
    public int Offset;
    // NiTimeController: the next controller in the chain and the stop time.
    public int NextController = -1;
    public float StopTime;
    // NiNode (and NiBillboardNode, NiLODNode)
    public int[] Children = [];
    // NiLODNode: its NiRangeLODData / NiScreenLODData. NiRangeLODData: each level's near extent.
    public int LodDataRef = -1;
    public float[] LodNear = [];
    // NiTriShape / NiTriStrips
    public int DataRef = -1;
    public int SkinRef = -1;
    // NiTriShapeData / NiTriStripsData
    public Vector3[] Vertices = [];
    public Vector3[]? Normals;
    public Vector2[][] UvSets = [];
    public (ushort A, ushort B, ushort C)[] Triangles = [];
    // NiSkinInstance
    public int SkinDataRef = -1;
    public int SkeletonRoot = -1;
    public int[] Bones = [];
    // NiSkinData
    public NifTransform SkinTransform = NifTransform.Identity;
    public NifTransform[] BoneBinds = [];
    public (ushort Vertex, float Weight)[][] BoneWeights = [];
    // NiTexturingProperty (base, dark and detail slots)
    public int BaseSourceRef = -1;
    public uint BaseUvSet;
    public int DarkSourceRef = -1, DetailSourceRef = -1;
    public uint DarkUvSet, DetailUvSet;
    // NiVertexColorProperty vertex mode (2 = vertex colours give ambient + diffuse), -1 when not one
    public int VertexMode = -1;
    // NiTriShapeData / NiTriStripsData vertex colours, if any
    public Vector4[]? Colors;
    // NiSourceTexture
    public string? TextureFile;
    // NiMaterialProperty
    public Vector4? MaterialColor;
    // NiAlphaProperty / NiStencilProperty
    public ushort AlphaFlags;
    public byte AlphaThreshold;
    public uint StencilDrawMode;
    // NiStringsExtraData / NiStringExtraData
    public string[] Strings = [];
    // NiMorphData: each morph's vectors (morph 0 is the base shape), and whether the others are relative
    public Vector3[][] MorphVectors = [];
    public bool RelativeMorphs;
}

/// <summary>A parsed 10.1 file: block types, the blocks decoded, and how the decode ended.</summary>
internal sealed class Gb10File
{
    public required IReadOnlyList<string> Types;
    /// <summary>Decoded blocks in file order; null from the stop point on.</summary>
    public required Gb10Block?[] Blocks;
    public required NifSurvey.SequentialDecode Result;
    public string? StopType;
    public string? StopReason;
    /// <summary>Feature counts ("Type: feature") from the blocks decoded.</summary>
    public required Dictionary<string, int> Features;
    public int[] Roots = [];
    public bool Complete => Result == NifSurvey.SequentialDecode.Complete;
}

internal static class Gamebryo1010
{
    public static Gb10File Parse(ReadOnlySpan<byte> bytes)
    {
        var c = new NifCursor(bytes);
        c.LineString();
        if (c.U32() != 0x0A010000)
            throw new NifFormatException("Not a 10.1.0.0 NIF.");
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

        var file = new Gb10File
        {
            Types = types,
            Blocks = new Gb10Block?[blockCount],
            Result = NifSurvey.SequentialDecode.Complete,
            Features = [],
        };
        for (int i = 0; i < types.Count; i++)
        {
            string type = types[i];
            if (!NifSurvey.DecodedTypes1010.Contains(type))
            {
                (file.Result, file.StopType) = (NifSurvey.SequentialDecode.Stopped, type);
                return file;
            }
            try
            {
                // Each 10.0-10.1 block starts with a u32 0 that nif.xml doesn't list (seen in every
                // DAoC 10.1 file; OfflineDAoC's reader skips it too).
                if (c.U32() != 0)
                    throw new NifFormatException("Block does not start with the 10.1 zero marker.");
                var block = new Gb10Block { Type = type, Offset = c.Pos };
                Decode(ref c, block, types, file.Features);
                file.Blocks[i] = block;
            }
            catch (NifFormatException e)
            {
                (file.Result, file.StopType, file.StopReason) = (NifSurvey.SequentialDecode.Failed, type, $"block {i}: {e.Message}");
                return file;
            }
        }
        if (!TryFooter(bytes, c.Pos, blockCount, out int[] roots))
        {
            (file.Result, file.StopType, file.StopReason) = (NifSurvey.SequentialDecode.Failed, "(footer)",
                $"decode ended at {c.Pos} of {bytes.Length}, not at the footer");
            return file;
        }
        file.Roots = roots;
        return file;
    }

    // The 10.1 footer: Num Roots u32, then that many refs to real blocks, ending the file.
    private static bool TryFooter(ReadOnlySpan<byte> bytes, int pos, int blockCount, out int[] roots)
    {
        roots = [];
        if (pos + 4 > bytes.Length)
            return false;
        long n = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(pos, 4));
        if (pos + 4 + n * 4 != bytes.Length)
            return false;
        roots = new int[n];
        for (int r = 0; r < n; r++)
        {
            roots[r] = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos + 4 + 4 * r, 4));
            if (roots[r] < 0 || roots[r] >= blockCount)
                return false;
        }
        return true;
    }

    // Decodes one 10.1.0.0 block body per nif.xml into `b`, counting features as it goes. Throws
    // NifFormatException on any check that fails.
    private static void Decode(ref NifCursor c, Gb10Block b, IReadOnlyList<string> types, Dictionary<string, int> f)
    {
        int nb = types.Count;
        string type = b.Type;
        switch (type)
        {
            case "NiNode":
                NodeBody(ref c, b, nb, f);
                break;
            case "NiBillboardNode":
                NodeBody(ref c, b, nb, f);
                Bump(f, $"NiBillboardNode: mode {c.U16()}");
                break;
            case "NiLODNode":
            {
                NodeBody(ref c, b, nb, f);
                c.U16();                                // Switch Node Flags (NiSwitchNode)
                c.U32();                                // Index
                b.LodDataRef = Ref(ref c, nb);
                break;
            }
            case "NiRangeLODData":
            {
                c.Vec3();                               // LOD Center
                uint levels = c.U32();
                if (levels > 64) throw new NifFormatException($"Implausible LOD level count {levels}.");
                b.LodNear = new float[levels];
                for (int l = 0; l < levels; l++) { b.LodNear[l] = c.F32(); c.F32(); }   // near, far extent
                Bump(f, $"NiRangeLODData: {levels} level(s)");
                break;
            }
            case "NiScreenLODData":
            {
                Skip(ref c, 16 + 16);                   // Bounding Sphere, World Bounding Sphere
                uint n = c.U32();
                if (n > 64) throw new NifFormatException($"Implausible LOD proportion count {n}.");
                Skip(ref c, n * 4L);
                Bump(f, "NiScreenLODData: screen-size LOD (near level = child 0)");
                break;
            }
            // Particle systems (NiParticleSystemController era): read to their end so the rest of the
            // file decodes; the converter draws no particles yet (roadmap M12).
            case "NiParticles":
            case "NiParticleMeshes":
            {
                AvObject(ref c, b, nb, f);
                Ref(ref c, nb);                         // Data
                Ref(ref c, nb);                         // Skin Instance
                if (StrictBool(ref c)) { c.SizedString(); c.I32(); }   // Material Data: shader
                Bump(f, $"{type}: particle system (not drawn)");
                break;
            }
            case "NiParticlesData":
            case "NiParticleMeshesData":
            {
                int nv = GeometryDataBase(ref c, b, f);
                if (StrictBool(ref c)) Skip(ref c, nv * 4L);       // Radii
                c.U16();                                            // Num Active
                if (StrictBool(ref c)) Skip(ref c, nv * 4L);       // Sizes
                if (StrictBool(ref c)) Skip(ref c, nv * 16L);      // Rotations (quaternions)
                if (type == "NiParticleMeshesData") Ref(ref c, nb);   // Container Node
                break;
            }
            case "NiParticleSystemController":
            {
                TimeController(ref c, nb);
                Skip(ref c, 6 * 4 + 12 + 16 + 4 * 3);   // speed, declination, planar angle (+ variations); normal; colour; size, emit start/stop
                c.U8();                                 // Reset Particle System
                Skip(ref c, 3 * 4);                     // birth rate, lifetime, lifetime variation
                c.U8(); c.U8();                         // Use Birth Rate, Spawn On Death
                c.Vec3();                               // Emitter Dimensions
                Ref(ref c, nb);                         // Emitter
                c.U16(); c.F32(); c.U16(); c.F32(); c.F32();   // spawn generations, %, multiplier, speed/dir chaos
                int particles = c.U16();
                c.U16();                                // Num Valid
                Skip(ref c, particles * 40L);           // NiParticleInfo
                Ref(ref c, nb); Ref(ref c, nb); Ref(ref c, nb);   // emitter modifier, particle modifier, collider
                c.U8();                                 // Static Target Bound
                break;
            }
            case "NiParticleColorModifier":
                ParticleModifier(ref c, nb);
                Ref(ref c, nb);                         // Color Data
                break;
            case "NiParticleGrowFade":
                ParticleModifier(ref c, nb);
                Skip(ref c, 8);                         // Grow, Fade
                break;
            case "NiParticleRotation":
                ParticleModifier(ref c, nb);
                c.U8();                                 // Random Initial Axis
                Skip(ref c, 12 + 4);                    // Initial Axis, Rotation Speed
                break;
            case "NiGravity":
                ParticleModifier(ref c, nb);
                Skip(ref c, 4 + 4 + 4 + 12 + 12);       // decay, force, field type, position, direction
                break;
            case "NiParticleBomb":
                ParticleModifier(ref c, nb);
                Skip(ref c, 4 * 4 + 4 + 4 + 12 + 12);   // decay, duration, delta V, start; decay type; symmetry; position; direction
                break;
            case "NiParticleMeshModifier":
                ParticleModifier(ref c, nb);
                RefList(ref c, nb);                     // Particle Meshes
                break;
            // Animation controllers and their key data: read past (no animation export for these yet).
            case "NiUVController":
                TimeController(ref c, nb);
                c.U16();                                // Texture Set
                Ref(ref c, nb);
                break;
            case "NiAlphaController":
            case "NiVisController":
                TimeController(ref c, nb);
                Ref(ref c, nb);                         // Data
                break;
            case "NiMaterialColorController":
            case "NiLightColorController":
                TimeController(ref c, nb);
                c.U16();                                // Target Color
                Ref(ref c, nb);
                break;
            case "NiTextureTransformController":
                TimeController(ref c, nb);
                StrictBool(ref c);                      // Shader Map
                c.U32(); c.U32();                       // Texture Slot, Operation
                Ref(ref c, nb);
                break;
            case "NiFlipController":
                TimeController(ref c, nb);
                c.U32();                                // Texture Slot
                Skip(ref c, 8);                         // Accum Time, Delta
                RefList(ref c, nb);                     // Sources
                break;
            case "NiPathController":
                TimeController(ref c, nb);
                c.U16(); c.I32(); c.F32(); c.F32(); c.U16();   // path flags, bank dir, max bank, smoothing, follow axis
                Ref(ref c, nb); Ref(ref c, nb);         // Path Data, Percent Data
                break;
            case "NiFloatData":
                NifSurvey.KeyGroup(ref c, 4, f, null);
                break;
            case "NiPosData":
                NifSurvey.KeyGroup(ref c, 12, f, null);
                break;
            case "NiColorData":
                NifSurvey.KeyGroup(ref c, 16, f, null);
                break;
            case "NiUVData":
                for (int g = 0; g < 4; g++) NifSurvey.KeyGroup(ref c, 4, f, null);
                break;
            case "NiVisData":
            {
                uint keys = c.U32();
                if (keys > 100_000) throw new NifFormatException($"Implausible key count {keys}.");
                Skip(ref c, keys * 5L);                 // time, byte
                break;
            }
            // Dynamic effects: environment maps and lights (not drawn yet).
            case "NiTextureEffect":
            {
                AvObject(ref c, b, nb, f);
                RefList(ref c, nb);                     // Affected Nodes
                Skip(ref c, 36 + 12);                   // model projection matrix, translation
                c.U32(); c.U32();                       // filtering, clamping
                Bump(f, $"NiTextureEffect: texture type {c.U32()}, coordinates {c.U32()}");
                Ref(ref c, nb);                         // Source Texture
                c.U8();                                 // Enable Plane
                Skip(ref c, 16);                        // Plane
                c.U16(); c.U16();                       // PS2 L, PS2 K
                break;
            }
            case "NiAmbientLight":
            case "NiDirectionalLight":
            case "NiPointLight":
            case "NiSpotLight":
            {
                AvObject(ref c, b, nb, f);
                RefList(ref c, nb);                     // Affected Nodes
                Skip(ref c, 4 + 3 * 12);                // dimmer; ambient, diffuse, specular colour
                if (type is "NiPointLight" or "NiSpotLight") Skip(ref c, 3 * 4);   // attenuation
                if (type == "NiSpotLight") Skip(ref c, 2 * 4);                      // outer angle, exponent
                Bump(f, $"{type}: light (not drawn)");
                break;
            }
            case "NiFogProperty":
                ObjectNet(ref c, b, nb, f);
                c.U16();                                // Flags
                Skip(ref c, 4 + 12);                    // depth, colour
                break;
            case "NiPalette":
            {
                c.U8();                                 // Has Alpha
                uint entries = c.U32();
                Skip(ref c, (entries == 16 ? 16 : 256) * 4L);
                break;
            }
            case "NiPixelData":
            {
                c.U32();                                // Pixel Format
                Skip(ref c, 4 * 4 + 4 + 8 + 4);         // masks, bits per pixel, old fast compare, tiling
                Ref(ref c, nb);                         // Palette
                uint mips = c.U32();
                if (mips > 32) throw new NifFormatException($"Implausible mipmap count {mips}.");
                c.U32();                                // Bytes Per Pixel
                Skip(ref c, mips * 12L);
                Skip(ref c, c.U32());                   // Pixel Data
                Bump(f, "NiPixelData: embedded texture pixels");
                break;
            }
            case "NiTriShape":
            case "NiTriStrips":
            {
                AvObject(ref c, b, nb, f);
                b.DataRef = Ref(ref c, nb);
                b.SkinRef = Ref(ref c, nb);
                if (b.SkinRef >= 0) Bump(f, $"{type}: skinned");
                if (StrictBool(ref c))
                {
                    Bump(f, $"{type}: shader '{c.SizedString()}'");
                    c.I32();                            // Unknown Integer
                }
                break;
            }
            case "NiTriShapeData":
            case "NiTriStripsData":
                GeometryData(ref c, b, f);
                break;
            case "NiSkinInstance":
            {
                b.SkinDataRef = Ref(ref c, nb);
                // Skeleton Root and Bones are pointers, but a separate body part (fig3 parts) is
                // skinned to a skeleton in another file: its bone pointers don't reach bone nodes
                // here (they repeat), and the bones are found by name ("bonelist"
                // NiStringsExtraData). Read them raw and report which case it is.
                b.SkeletonRoot = c.I32();
                uint bones = c.U32();
                if (bones > 1000) throw new NifFormatException($"Implausible bone count {bones}.");
                b.Bones = new int[bones];
                bool notNodes = false;
                for (int i = 0; i < bones; i++)
                {
                    int bone = c.I32();
                    notNodes |= bone < 0 || bone >= nb || types[bone] != "NiNode";
                    b.Bones[i] = bone;
                }
                Bump(f, $"NiSkinInstance: {Bucket((int)bones)} bones");
                Bump(f, notNodes ? "NiSkinInstance: bone pointers outside this file's nodes"
                    : b.Bones.Distinct().Count() < bones ? "NiSkinInstance: bone pointers repeat (bones bound by name to a separate skeleton)"
                    : "NiSkinInstance: one distinct bone node per bone");
                break;
            }
            case "NiSkinData":
            {
                b.SkinTransform = SkinTransform(ref c);
                uint bones = c.U32();
                if (bones > 1000) throw new NifFormatException($"Implausible bone count {bones}.");
                Ref(ref c, nb);                         // Skin Partition (4.0.0.2 - 10.1.0.0)
                byte hasWeights = c.U8();
                if (hasWeights > 1) throw new NifFormatException($"Has Vertex Weights {hasWeights}.");
                if (hasWeights == 0) Bump(f, "NiSkinData: no vertex weights (partition only)");
                b.BoneBinds = new NifTransform[bones];
                b.BoneWeights = new (ushort, float)[bones][];
                var influences = new Dictionary<int, (int Count, float Sum)>();
                for (int i = 0; i < bones; i++)
                {
                    b.BoneBinds[i] = SkinTransform(ref c);
                    Skip(ref c, 16);                    // bounding sphere
                    int nv = c.U16();
                    var weights = new (ushort, float)[hasWeights == 0 ? 0 : nv];
                    for (int v = 0; v < weights.Length; v++)
                    {
                        ushort index = c.U16();
                        float w = c.F32();
                        weights[v] = (index, w);
                        influences.TryGetValue(index, out var e);
                        influences[index] = (e.Count + 1, e.Sum + w);
                    }
                    b.BoneWeights[i] = weights;
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
                ObjectNet(ref c, b, nb, f);
                c.Vec3();                               // Ambient
                Vector3 diffuse = c.Vec3();
                c.Vec3();                               // Specular
                Vector3 emissive = c.Vec3();
                c.F32();                                // Glossiness
                float alpha = c.F32();
                b.MaterialColor = new Vector4(diffuse, alpha);
                if (emissive != Vector3.Zero) Bump(f, "NiMaterialProperty: non-black emissive");
                if (alpha < 0.999f) Bump(f, "NiMaterialProperty: alpha < 1");
                break;
            }
            case "NiTexturingProperty":
            {
                ObjectNet(ref c, b, nb, f);
                uint apply = c.U32();
                if (apply != 2) Bump(f, $"NiTexturingProperty: apply mode {ApplyModeName(apply)}");
                uint count = c.U32();
                string[] slots = ["base", "dark", "detail", "gloss", "glow", "bump", "decal 0"];
                foreach (string slot in slots)
                {
                    if (!StrictBool(ref c))
                        continue;
                    Bump(f, $"NiTexturingProperty: {slot} texture");
                    (int source, uint uvSet) = TexDesc(ref c, nb, slot, f);
                    if (slot == "base")
                        (b.BaseSourceRef, b.BaseUvSet) = (source, uvSet);
                    else if (slot == "dark")
                        (b.DarkSourceRef, b.DarkUvSet) = (source, uvSet);
                    else if (slot == "detail")
                        (b.DetailSourceRef, b.DetailUvSet) = (source, uvSet);
                    if (slot == "bump")
                        Skip(ref c, 2 * 4 + 4 * 4);     // luma scale, luma offset, 2x2 matrix
                }
                for (int d = 1; d <= 3; d++)
                {
                    if (count < 7 + d || !StrictBool(ref c))
                        continue;
                    Bump(f, $"NiTexturingProperty: decal {d} texture");
                    TexDesc(ref c, nb, $"decal {d}", f);
                }
                uint shaderTextures = c.U32();
                if (shaderTextures > 64) throw new NifFormatException($"Implausible shader texture count {shaderTextures}.");
                for (int s = 0; s < shaderTextures; s++)
                {
                    if (!StrictBool(ref c))
                        continue;
                    Bump(f, "NiTexturingProperty: shader texture");
                    TexDesc(ref c, nb, "shader", f);
                    c.U32();                            // Map Index
                }
                break;
            }
            case "NiSourceTexture":
            {
                ObjectNet(ref c, b, nb, f);
                byte external = c.U8();
                if (external > 1) throw new NifFormatException($"Use External {external}.");
                string fileName = c.SizedString();      // File Name (both cases at 10.1)
                Ref(ref c, nb);                         // Unknown Link / Pixel Data
                if (external == 1) b.TextureFile = Path.GetFileName(fileName.Replace('\\', '/'));
                Bump(f, external == 1 ? "NiSourceTexture: external file" : "NiSourceTexture: EMBEDDED pixel data");
                c.U32(); c.U32();                       // Pixel Layout, Use Mipmaps
                Bump(f, $"NiSourceTexture: alpha format {c.U32()}");
                c.U8();                                 // Is Static
                break;
            }
            case "NiVertexColorProperty":
            {
                ObjectNet(ref c, b, nb, f);
                c.U16();
                uint vertexMode = c.U32(), lightingMode = c.U32();
                b.VertexMode = (int)vertexMode;
                Bump(f, $"NiVertexColorProperty: vertex mode {vertexMode}, lighting mode {lightingMode}");
                break;
            }
            case "NiZBufferProperty":
            {
                ObjectNet(ref c, b, nb, f);
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
                ObjectNet(ref c, b, nb, f);
                Bump(f, $"{type}: flags 0x{c.U16():X4}");
                break;
            }
            case "NiAlphaProperty":
            {
                ObjectNet(ref c, b, nb, f);
                b.AlphaFlags = c.U16();
                b.AlphaThreshold = c.U8();
                (SurfaceAlpha mode, _) = NifFile.AlphaMode(b.AlphaFlags, b.AlphaThreshold);
                Bump(f, $"NiAlphaProperty: flags 0x{b.AlphaFlags:X4} threshold {b.AlphaThreshold} -> {mode}");
                break;
            }
            case "NiStencilProperty":
            {
                ObjectNet(ref c, b, nb, f);
                if (c.U8() != 0) Bump(f, "NiStencilProperty: stencil test enabled");
                Skip(ref c, 6 * 4);
                b.StencilDrawMode = c.U32();
                Bump(f, $"NiStencilProperty: draw mode {b.StencilDrawMode}");
                break;
            }
            case "NiKeyframeController":
                TimeController(ref c, nb, b);
                b.DataRef = Ref(ref c, nb);
                break;
            case "NiGeomMorpherController":
                TimeController(ref c, nb);
                c.U16();                                // Extra Flags
                b.DataRef = Ref(ref c, nb);
                c.U8();                                 // Always Update
                break;
            case "NiKeyframeData":
                NifSurvey.KeyframeData(ref c, f);
                break;
            case "NiMorphData":
            {
                uint morphs = c.U32(), nv = c.U32();
                if (morphs > 1000 || nv > 65535) throw new NifFormatException($"Implausible morph data ({morphs} x {nv}).");
                b.RelativeMorphs = c.U8() != 0;         // Relative Targets
                Bump(f, $"NiMorphData: {Bucket((int)morphs)} morph(s)");
                b.MorphVectors = new Vector3[morphs][];
                for (int m = 0; m < morphs; m++)
                {
                    uint keys = c.U32();
                    uint interp = c.U32();              // present even with no keys
                    NifSurvey.Keys(ref c, keys, interp, 4);
                    var vectors = new Vector3[nv];
                    for (int v = 0; v < nv; v++)
                        vectors[v] = c.Vec3();
                    b.MorphVectors[m] = vectors;
                }
                break;
            }
            case "NiStringExtraData":
                b.Name = c.SizedString();
                Bump(f, $"NiStringExtraData: named '{Fold(b.Name)}'");
                b.Strings = [c.SizedString()];
                break;
            case "NiStringsExtraData":
            {
                b.Name = c.SizedString();
                Bump(f, $"NiStringsExtraData: named '{Fold(b.Name)}'");
                uint n = c.U32();
                if (n > 10000) throw new NifFormatException($"Implausible string count {n}.");
                b.Strings = new string[n];
                for (int s = 0; s < n; s++) b.Strings[s] = c.SizedString();
                break;
            }
            case "NiIntegersExtraData":
            {
                b.Name = c.SizedString();
                Bump(f, $"NiIntegersExtraData: named '{Fold(b.Name)}'");
                Skip(ref c, c.U32() * 4L);
                break;
            }
            case "NiIntegerExtraData":
            case "NiFloatExtraData":
                b.Name = c.SizedString();
                c.U32();
                break;
            case "NiBooleanExtraData":
                b.Name = c.SizedString();
                c.U8();
                break;
            case "NiColorExtraData":
                b.Name = c.SizedString();
                Skip(ref c, 16);
                break;
            case "NiVectorExtraData":
                b.Name = c.SizedString();
                Skip(ref c, 16);
                break;
            case "NiBinaryExtraData":
                b.Name = c.SizedString();
                Skip(ref c, c.U32());
                break;
            case "NiTextKeyExtraData":
            {
                b.Name = c.SizedString();
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

    // NiNode (10.1): NiAVObject, children, dynamic effects.
    private static void NodeBody(ref NifCursor c, Gb10Block b, int nb, Dictionary<string, int> f)
    {
        AvObject(ref c, b, nb, f);
        b.Children = RefList(ref c, nb);
        if (RefList(ref c, nb).Length > 0) Bump(f, $"{b.Type}: has dynamic effects");
    }

    // NiParticleModifier (10.1): Next Modifier, Controller.
    private static void ParticleModifier(ref NifCursor c, int nb)
    {
        Ref(ref c, nb);
        Ref(ref c, nb);
    }

    // NiObjectNET (10.1): Name, Num Extra Data List + refs, Controller.
    private static void ObjectNet(ref NifCursor c, Gb10Block b, int nb, Dictionary<string, int> f)
    {
        b.Name = c.SizedString();
        b.Extra = RefList(ref c, nb);
        b.Controller = Ref(ref c, nb);
        if (b.Extra.Length > 0) Bump(f, $"{b.Type}: has extra data");
        if (b.Controller >= 0) Bump(f, $"{b.Type}: has controller (animated)");
    }

    // NiAVObject (10.1): NiObjectNET, Flags, Translation, Rotation, Scale, properties, Collision Object.
    private static void AvObject(ref NifCursor c, Gb10Block b, int nb, Dictionary<string, int> f)
    {
        ObjectNet(ref c, b, nb, f);
        b.Flags = c.U16();
        if ((b.Flags & 1) != 0) Bump(f, $"{b.Type}: Hidden flag");
        Vector3 trans = c.Vec3();
        float[] rot = new float[9];
        for (int i = 0; i < 9; i++) rot[i] = c.F32();
        float scale = c.F32();
        (rot, scale, trans) = NifFile.Sanitize(rot, scale, trans);
        b.Local = new NifTransform(rot, trans, scale);
        b.Props = RefList(ref c, nb);
        if (Ref(ref c, nb) >= 0) Bump(f, $"{b.Type}: has collision object");
    }

    // SkinTransform: Rotation (3x3), Translation, Scale.
    internal static NifTransform SkinTransform(ref NifCursor c)
    {
        float[] rot = new float[9];
        for (int i = 0; i < 9; i++) rot[i] = c.F32();
        Vector3 trans = c.Vec3();
        return new NifTransform(rot, trans, c.F32());
    }

    // TexDesc (10.1): Source, Clamp, Filter, UV Set, PS2 L, PS2 K, Has Texture Transform (+ 32 bytes).
    private static (int Source, uint UvSet) TexDesc(ref NifCursor c, int nb, string slot, Dictionary<string, int> f)
    {
        int source = Ref(ref c, nb);
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
        return (source, uvSet);
    }

    // NiGeometryData at 10.1.0.0, shared by triangle and particle data (NifGeometry.ReadBase).
    private static int GeometryDataBase(ref NifCursor c, Gb10Block b, Dictionary<string, int> f)
    {
        NifGeometryData g = NifGeometry.ReadBase(ref c, b.Type, gamebryo10: true, f);
        Store(b, g);
        return g.VertexCount;
    }

    // NiTriShapeData / NiTriStripsData at 10.1.0.0 (NifGeometry.Read).
    private static void GeometryData(ref NifCursor c, Gb10Block b, Dictionary<string, int> f)
    {
        NifGeometryData g = NifGeometry.Read(ref c, b.Type, gamebryo10: true, f);
        Store(b, g);
        b.Triangles = g.Triangles;
    }

    private static void Store(Gb10Block b, NifGeometryData g)
    {
        b.Vertices = g.Vertices;
        b.Normals = g.Normals;
        b.Colors = g.Colors;
        b.UvSets = g.UvSets;
    }

    // NiTimeController (10.1, as 4.x): Next Controller, Flags, Frequency, Phase, Start, Stop, Target.
    // NiTimeController: Next Controller, Flags, Frequency, Phase, Start Time, Stop Time, Target.
    private static void TimeController(ref NifCursor c, int nb, Gb10Block? b = null)
    {
        int next = Ref(ref c, nb);
        c.U16();
        Skip(ref c, 12);
        float stop = c.F32();
        Ref(ref c, nb);
        if (b is not null)
            (b.NextController, b.StopTime) = (next, stop);
    }

    // A ref or ptr: -1 or a block index.
    private static int Ref(ref NifCursor c, int nb)
    {
        int r = c.I32();
        if (r < -1 || r >= nb)
            throw new NifFormatException($"Reference {r} outside the file's {nb} blocks.");
        return r;
    }

    private static int[] RefList(ref NifCursor c, int nb)
    {
        uint n = c.U32();
        if (n > (uint)nb) throw new NifFormatException($"Reference list of {n} in a file of {nb} blocks.");
        var refs = new int[n];
        for (int i = 0; i < n; i++)
            refs[i] = Ref(ref c, nb);
        return refs;
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

    private static void Skip(ref NifCursor c, long count)
    {
        if (count < 0 || c.Pos + count > c.Length)
            throw new NifFormatException("Block runs past the end of the file.");
        c.SeekTo(c.Pos + (int)count);
    }

    private static void Bump(Dictionary<string, int> d, string key) =>
        d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;

    private static string ApplyModeName(uint mode) => mode switch
    {
        0 => "REPLACE", 1 => "DECAL", 2 => "MODULATE", 3 => "HILIGHT", 4 => "HILIGHT2", _ => mode.ToString(),
    };

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
