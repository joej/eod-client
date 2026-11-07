// SPDX-License-Identifier: GPL-3.0-or-later
//
// Animation clips (.kfa, roadmap M7b), NetImmerse 4.x and Gamebryo 10.1 (the Catacombs clips). A clip names no bones directly: its root NiNode
// carries a chain of NiStringExtraData, each a number indexing figures/animnode.dat (a list of bone
// names: 0 Bip01, 1 Bip01 Pelvis, ...), and a chain of NiKeyframeControllers in the same order, each
// pointing at the NiKeyframeData with that bone's keys. Layouts from niftools nif.xml (GPL-3.0) at
// 4.2.1.0, the same ones NifSurvey checks against every clip in the install. Keys are bone-local, in
// the figure's NIF space (Z up, NIF units), so they drive the figure's joints directly.

using System.Numerics;

namespace DaocAssets.Nif;

/// <summary>One bone's keys. Times in seconds; rotations as quaternions (System.Numerics, x y z w).</summary>
public sealed record KfaTrack(
    string Bone,
    (float Time, Quaternion Value)[] Rotations,
    (float Time, Vector3 Value)[] Translations,
    (float Time, float Value)[] Scales);

/// <summary>A clip: its tracks and length (the controllers' stop time).</summary>
public sealed record KfaClip(float Duration, IReadOnlyList<KfaTrack> Tracks)
{
    /// <summary>Reads a 4.x .kfa. <paramref name="boneNames"/> is figures/animnode.dat, one name per line.</summary>
    public static KfaClip Read(ReadOnlySpan<byte> bytes, IReadOnlyList<string> boneNames)
    {
        var c = new NifCursor(bytes);
        string header = c.LineString();
        if (!header.Contains("File Format", StringComparison.Ordinal))
            throw new NifFormatException($"Not a NIF file (header: '{header}').");
        uint packed = c.U32();
        if (packed == 0x0A010000)
            return Read1010(bytes, boneNames);
        if (packed is < 0x04000002 or >= 0x0A000000)
            throw new NifFormatException($"Clip version {packed >> 24}.{(packed >> 16) & 0xFF} is not a 4.x NIF.");
        int blockCount = checked((int)c.U32());
        (string Type, int Offset)[] blocks = NifFile.IndexBlocks(bytes, blockCount);
        if (blocks.Length == 0 || blocks[0].Type != "NiNode")
            throw new NifFormatException("A clip starts with its root NiNode.");

        // Root NiObjectNET: Name, Extra Data ref, Controller ref.
        var r = At(bytes, blocks[0].Offset);
        r.SizedString();
        int extra = r.I32(), controller = r.I32();

        var bones = new List<string>();
        for (int e = extra, guard = 0; e >= 0 && guard < blocks.Length; guard++)
        {
            if (e >= blocks.Length || blocks[e].Type != "NiStringExtraData")
                throw new NifFormatException($"Extra data chain reaches block {e} ({Type(blocks, e)}).");
            var s = At(bytes, blocks[e].Offset);
            int next = s.I32();
            s.U32();                                            // Bytes Remaining
            string text = s.SizedString().Trim();
            bones.Add(int.TryParse(text, out int i) && i >= 0 && i < boneNames.Count ? boneNames[i] : text);
            e = next;
        }

        var tracks = new List<KfaTrack>();
        float duration = 0;
        int k = 0;
        for (int ctl = controller; ctl >= 0 && k < blocks.Length; k++)
        {
            if (ctl >= blocks.Length || blocks[ctl].Type != "NiKeyframeController")
                throw new NifFormatException($"Controller chain reaches block {ctl} ({Type(blocks, ctl)}).");
            var t = At(bytes, blocks[ctl].Offset);
            int next = t.I32();
            t.U16();                                            // Flags
            t.F32(); t.F32();                                   // Frequency, Phase
            t.F32();                                            // Start Time
            duration = Math.Max(duration, t.F32());             // Stop Time
            t.I32();                                            // Target (unset in clips)
            int data = t.I32();
            if (k < bones.Count && data >= 0 && data < blocks.Length && blocks[data].Type == "NiKeyframeData")
                tracks.Add(ReadKeys(bytes, blocks[data].Offset, bones[k]));
            ctl = next;
        }
        if (k != bones.Count)
            throw new NifFormatException($"{bones.Count} bone names but {k} controllers.");
        return new KfaClip(duration, tracks);
    }

    // A 10.1.0.0 clip (the Catacombs ones, Cat_*.kfa): the same blocks as 4.x, but the root lists its
    // NiStringExtraData in an array (no chain) and each holds only a name and the bone number. The
    // keyframe data layout is unchanged at 10.1 (nif.xml; checked by the audit on all 1,542 such clips).
    private static KfaClip Read1010(ReadOnlySpan<byte> bytes, IReadOnlyList<string> boneNames)
    {
        Gb10File g = Gamebryo1010.Parse(bytes);
        if (!g.Complete)
            throw new NifFormatException($"10.1 clip does not decode ({g.StopType}: {g.StopReason}).");
        Gb10Block[] blocks = g.Blocks!;
        Gb10Block root = blocks[g.Roots.Length > 0 ? g.Roots[0] : 0];
        var bones = new List<string>();
        foreach (int e in root.Extra)
            if (e >= 0 && blocks[e].Type == "NiStringExtraData")
            {
                string text = blocks[e].Strings.FirstOrDefault()?.Trim() ?? "";
                bones.Add(int.TryParse(text, out int i) && i >= 0 && i < boneNames.Count ? boneNames[i] : text);
            }
        var tracks = new List<KfaTrack>();
        float duration = 0;
        int k = 0;
        for (int ctl = root.Controller; ctl >= 0 && k < blocks.Length; k++)
        {
            Gb10Block t = blocks[ctl];
            if (t.Type != "NiKeyframeController")
                throw new NifFormatException($"Controller chain reaches block {ctl} ({t.Type}).");
            duration = Math.Max(duration, t.StopTime);
            if (k < bones.Count && t.DataRef >= 0 && blocks[t.DataRef].Type == "NiKeyframeData")
            {
                var c = new NifCursor(bytes);
                c.SeekTo(blocks[t.DataRef].Offset);
                tracks.Add(ReadKeys(ref c, bones[k]));
            }
            ctl = t.NextController;
        }
        if (k != bones.Count)
            throw new NifFormatException($"{bones.Count} bone names but {k} controllers.");
        return new KfaClip(duration, tracks);
    }

    // NiKeyframeData (4.x): rotations (quaternion keys, or type 4: three Euler key groups), then
    // translation and scale key groups.
    private static KfaTrack ReadKeys(ReadOnlySpan<byte> bytes, int offset, string bone)
    {
        var c = At(bytes, offset);
        return ReadKeys(ref c, bone);
    }

    private static KfaTrack ReadKeys(ref NifCursor c, string bone)
    {
        (float, Quaternion)[] rotations = [];
        uint count = c.U32();
        if (count > 0)
        {
            uint type = c.U32();
            if (type == 4)
            {
                c.F32();                                        // Unknown Float (4.x)
                var axes = new (float Time, float Value)[3][];
                for (int a = 0; a < 3; a++)
                    axes[a] = FloatGroup(ref c);
                rotations = Euler(axes);
            }
            else
            {
                if (type is < 1 or > 5 || count > 100_000)
                    throw new NifFormatException($"Rotation keys: type {type}, count {count}.");
                rotations = new (float, Quaternion)[count];
                for (int i = 0; i < count; i++)
                {
                    float time = c.F32();
                    float w = c.F32(), x = c.F32(), y = c.F32(), z = c.F32();
                    if (type == 3) { c.F32(); c.F32(); c.F32(); }   // TBC
                    rotations[i] = (time, Quaternion.Normalize(new Quaternion(x, y, z, w)));
                }
            }
        }
        (float, Vector3)[] translations = Group(ref c, 3, v => new Vector3(v[0], v[1], v[2]));
        (float, float)[] scales = Group(ref c, 1, v => v[0]);
        return Finite(new KfaTrack(bone, rotations, translations, scales));
    }

    /// <summary>
    /// The track without keys whose time or value is not a finite number. A few clips in the install carry
    /// such keys (DR_NecroServant_Death.kfa, found when the Death clips were added) though the blocks decode to their boundaries;
    /// glTF cannot hold them, and there is nothing to interpolate to, so the neighbouring keys stand in.
    /// </summary>
    public static KfaTrack Finite(KfaTrack t)
    {
        static bool Ok(float f) => float.IsFinite(f);
        return t with
        {
            Rotations = [.. t.Rotations.Where(k => Ok(k.Time) && Ok(k.Value.X) && Ok(k.Value.Y) && Ok(k.Value.Z) && Ok(k.Value.W))],
            Translations = [.. t.Translations.Where(k => Ok(k.Time) && Ok(k.Value.X) && Ok(k.Value.Y) && Ok(k.Value.Z))],
            Scales = [.. t.Scales.Where(k => Ok(k.Time) && Ok(k.Value))],
        };
    }

    // Euler (XYZ) rotation keys: each axis has its own key times; sampled at every time any axis has.
    // The order X then Y then Z (column vectors: R = Rz * Ry * Rx) is Gamebryo's NiMatrix3 FromEulerAnglesXYZ.
    private static (float, Quaternion)[] Euler((float Time, float Value)[][] axes)
    {
        float[] times = axes.SelectMany(a => a.Select(k => k.Time)).Distinct().Order().ToArray();
        if (times.Length == 0)
            return [];
        return times.Select(t =>
        {
            float x = Sample(axes[0], t), y = Sample(axes[1], t), z = Sample(axes[2], t);
            Quaternion q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z)
                         * Quaternion.CreateFromAxisAngle(Vector3.UnitY, y)
                         * Quaternion.CreateFromAxisAngle(Vector3.UnitX, x);
            return (t, Quaternion.Normalize(q));
        }).ToArray();
    }

    private static float Sample((float Time, float Value)[] keys, float t)
    {
        if (keys.Length == 0) return 0;
        if (t <= keys[0].Time) return keys[0].Value;
        for (int i = 1; i < keys.Length; i++)
            if (t <= keys[i].Time)
            {
                float u = (t - keys[i - 1].Time) / Math.Max(keys[i].Time - keys[i - 1].Time, 1e-6f);
                return keys[i - 1].Value + (keys[i].Value - keys[i - 1].Value) * u;
            }
        return keys[^1].Value;
    }

    private static (float Time, float Value)[] FloatGroup(ref NifCursor c) => Group(ref c, 1, v => v[0]);

    // A key group: count, then (if any) the interpolation type and the keys: time, value, plus forward
    // and backward tangents (quadratic, 2) or tension/bias/continuity (TBC, 3). Tangents are dropped:
    // the glTF side interpolates linearly between keys.
    private static (float Time, T Value)[] Group<T>(ref NifCursor c, int width, Func<float[], T> make)
    {
        uint n = c.U32();
        if (n == 0)
            return [];
        uint type = c.U32();
        if (type is < 1 or > 5 || n > 100_000)
            throw new NifFormatException($"Key group: type {type}, count {n}.");
        var keys = new (float, T)[n];
        var v = new float[width];
        for (int i = 0; i < n; i++)
        {
            float time = c.F32();
            for (int j = 0; j < width; j++) v[j] = c.F32();
            if (type == 2) for (int j = 0; j < 2 * width; j++) c.F32();
            if (type == 3) { c.F32(); c.F32(); c.F32(); }
            keys[i] = (time, make(v));
        }
        return keys;
    }

    // A cursor at a block's body (after its sized type string).
    private static NifCursor At(ReadOnlySpan<byte> bytes, int offset)
    {
        var c = new NifCursor(bytes);
        c.SeekTo(offset);
        c.SizedString();
        return c;
    }

    private static string Type((string Type, int Offset)[] blocks, int i) =>
        i >= 0 && i < blocks.Length ? blocks[i].Type : "outside the file";
}
