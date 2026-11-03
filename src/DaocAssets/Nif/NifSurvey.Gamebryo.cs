// SPDX-License-Identifier: GPL-3.0-or-later
//
// The Gamebryo 10.1.0.0 half of the coverage survey. 10.1 bodies carry no size, so the survey runs
// the converter's own 10.1 parser (Gamebryo1010.cs) over the file: a decode that ends exactly at the
// footer, with every reference and bool checked on the way, is the proof that the layouts the
// converter reads are right. Running the same parser (rather than a second copy of the layouts) means
// the audit checks exactly what conversion uses. A file with a type we don't decode stops there: its
// blocks are reported as unchecked, not as passed.

namespace DaocAssets.Nif;

public sealed partial class NifSurvey
{
    /// <summary>The 10.1.0.0 types the parser (and so this survey) decodes byte for byte.</summary>
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

    private static NifSurvey Read1010(ReadOnlySpan<byte> bytes, NifVersion version)
    {
        Gb10File g = Gamebryo1010.Parse(bytes);
        var exact = new Dictionary<string, int>();
        var failed = new Dictionary<string, int>();
        var features = new Dictionary<string, int>();
        if (g.Result == SequentialDecode.Failed)
            Bump(failed, g.StopType == "(footer)" ? (g.Types.Count > 0 ? g.Types[^1] : "(footer)") : g.StopType!);
        if (g.Complete)
        {
            foreach (string t in g.Types)
                Bump(exact, t);
            // Features from a decode that went wrong are noise; keep them only from complete files.
            foreach ((string k, int n) in g.Features)
                features["[10.1] " + k] = n;
        }
        return new NifSurvey
        {
            Version = version,
            DeclaredBlocks = g.Types.Count,
            ScannedBlocks = g.Types.Count,
            BlockTypes = g.Types,
            ExactDecodes = exact,
            FailedDecodes = failed,
            Features = features,
            Sequential = g.Result,
            StopType = g.StopType,
            StopReason = g.StopReason,
        };
    }

    // NiKeyframeData (4.x and 10.1.0.0 share the layout): rotation keys (quaternion, or three
    // float key groups when the type is XYZ), translation key group, scale key group.
    internal static void KeyframeData(ref NifCursor c, Dictionary<string, int> f)
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
    internal static void Keys(ref NifCursor c, uint n, uint ktype, int valueSize)
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
}
