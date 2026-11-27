// SPDX-License-Identifier: GPL-3.0-or-later
//
// A model's material animations: what changes on a mesh over time other than its place. NetImmerse/Gamebryo controllers on
// the mesh, its material or its texturing (the client runs them as stock Gamebryo does, on an effect from its start, on
// scenery continuously):
// - NiAlphaController on the NiMaterialProperty: the material's alpha (a flash fading out); keys in NiFloatData.
// - NiMaterialColorController on the NiMaterialProperty: one of its colours (4.x: the colour in the controller's flags,
//   bits 4-6; 10.1: a Target Color field; 0 ambient, 1 diffuse, 2 specular, 3 self-illumination); keys in NiPosData.
// - NiUVController on the shape: the texture's offset and tiling (a waterfall, a magic surface); NiUVData holds four key
//   groups: U offset, V offset, U tiling, V tiling.
// - NiTextureTransformController on the NiTexturingProperty (10.1): one transform of a texture slot (operation 0 U
//   offset, 1 V offset, 2 rotation, 3 U tiling, 4 V tiling); keys in NiFloatData.
// - NiVisController on the shape or a node above it: whether it is drawn.
// Layouts: NifTools' nif.xml (GPL-3.0). Interpolation between keys is linear here (quadratic and TBC keys are read for
// their values only).

using System.Numerics;

namespace DaocAssets.Nif;

/// <summary>One animated property of one mesh: what it drives, its keys (time in seconds, value: one to three numbers)
/// and its controller's span and flags (bits 1-2: 0 loop, 1 reverse, 2 clamp = play once).</summary>
public sealed record NifMaterialAnim(string Mesh, string Kind, int Target, (float Time, float[] Value)[] Keys, ushort Flags,
    float StartTime, float StopTime)
{
    public const string Alpha = "alpha", Color = "color", Uv = "uv", TextureTransform = "texture", Visibility = "vis";
}

/// <summary>Reads the material-animation blocks of both NIF versions.</summary>
internal static class NifMaterialAnimReader
{
    /// <summary>A material/texture/UV controller as read: what it drives and the block holding its keys.</summary>
    internal sealed record Controller(string Kind, int Next, ushort Flags, float StartTime, float StopTime, int Target, int Data);

    /// <summary>Key data as read: one or more groups of keys (NiUVData has four), each value one to three numbers.</summary>
    internal sealed record KeyData((float Time, float[] Value)[][] Groups);

    internal static bool Reads(string type) => type is "NiAlphaController" or "NiMaterialColorController" or "NiUVController"
        or "NiTextureTransformController" or "NiFloatData" or "NiPosData" or "NiUVData";

    internal static object? Read(ref NifCursor c, string type, bool gamebryo10)
    {
        switch (type)
        {
            case "NiFloatData":
                return new KeyData([Keys(ref c, 1)]);
            case "NiPosData":
                return new KeyData([Keys(ref c, 3)]);
            case "NiUVData":
                return new KeyData([Keys(ref c, 1), Keys(ref c, 1), Keys(ref c, 1), Keys(ref c, 1)]);
        }
        // NiTimeController: Next Controller, Flags, Frequency, Phase, Start Time, Stop Time, Target.
        int next = c.I32();
        ushort flags = c.U16();
        c.F32(); c.F32();
        float start = c.F32(), stop = c.F32();
        c.I32();
        switch (type)
        {
            case "NiAlphaController":
                return new Controller(NifMaterialAnim.Alpha, next, flags, start, stop, 0, c.I32());
            case "NiMaterialColorController":
            {
                int target = gamebryo10 ? c.U16() : (flags >> 4) & 7;
                return new Controller(NifMaterialAnim.Color, next, flags, start, stop, target, c.I32());
            }
            case "NiUVController":
                c.U16();                                     // texture set
                return new Controller(NifMaterialAnim.Uv, next, flags, start, stop, 0, c.I32());
            case "NiTextureTransformController":
            {
                c.U8();                                      // shader map
                c.U32();                                     // texture slot
                int op = (int)c.U32();
                return new Controller(NifMaterialAnim.TextureTransform, next, flags, start, stop, op, c.I32());
            }
        }
        return null;
    }

    // KeyGroup: count, key type (when there are keys), keys of time + value (+ tangents for quadratic, + TBC).
    private static (float, float[])[] Keys(ref NifCursor c, int size)
    {
        uint n = c.U32();
        if (n == 0)
            return [];
        if (n > 100_000) throw new NifFormatException($"Implausible key count {n}.");
        uint type = c.U32();
        if (type is < 1 or > 5) throw new NifFormatException($"Key type {type}.");
        var keys = new (float, float[])[n];
        for (int i = 0; i < n; i++)
        {
            float t = c.F32();
            var v = new float[size];
            for (int k = 0; k < size; k++) v[k] = c.F32();
            if (type == 2) for (int k = 0; k < 2 * size; k++) c.F32();
            if (type == 3) for (int k = 0; k < 3; k++) c.F32();
            keys[i] = (t, v);
        }
        return keys;
    }

    /// <summary>
    /// The tracks of one mesh: the controllers on its material (and its texturing property), on the shape itself, and the
    /// visibility controller on it or the nearest node above it. <paramref name="raw"/> gives a block's parsed data,
    /// <paramref name="next"/> the next controller after any controller block.
    /// </summary>
    internal static IEnumerable<NifMaterialAnim> Tracks(string mesh, IEnumerable<int> chains, int visController,
        Func<int, object?> raw, Func<int, int> next)
    {
        foreach (int first in chains)
        {
            int seen = 0;
            for (int i = first; i >= 0 && seen++ < 32; i = next(i))
            {
                if (raw(i) is not Controller ctl || raw(ctl.Data) is not KeyData data)
                    continue;
                if (ctl.Kind == NifMaterialAnim.Uv)
                {
                    // U offset, V offset, U tiling, V tiling: one track per group with keys (target = the group).
                    for (int g = 0; g < data.Groups.Length; g++)
                        if (data.Groups[g].Length > 0)
                            yield return new NifMaterialAnim(mesh, ctl.Kind, g, data.Groups[g], ctl.Flags, ctl.StartTime, ctl.StopTime);
                }
                else if (data.Groups.Length > 0 && data.Groups[0].Length > 0)
                    yield return new NifMaterialAnim(mesh, ctl.Kind, ctl.Target, data.Groups[0], ctl.Flags, ctl.StartTime, ctl.StopTime);
            }
        }
        if (visController >= 0 && raw(visController) is NifParticleReader.VisController vc && raw(vc.Data) is NifParticleReader.VisData vd
            && vd.Keys.Length > 0)
            yield return new NifMaterialAnim(mesh, NifMaterialAnim.Visibility, 0,
                vd.Keys.Select(k => (k.Time, new[] { k.Visible ? 1f : 0f })).ToArray(), vc.Flags, vc.StartTime, vc.StopTime);
    }
}
