// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using DaocAssets.Nif;
using Xunit;
using Xunit.Abstractions;

namespace DaocAssets.Tests;

/// <summary>
/// The coverage survey behind `mpk audit`: block types, the byte-exact decode check, and features.
/// </summary>
public class NifSurveyTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> PrivateNifs()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "live-nif");
        if (!Directory.Exists(dir))
            yield break;
        foreach (string p in Directory.GetFiles(dir, "*.nif").Order())
            yield return [Path.GetFileName(p)];
    }

    [Theory]
    [MemberData(nameof(PrivateNifs))]
    public void EveryDecodedBlockEndsExactlyAtTheNextOne(string name)
    {
        // Every real fixture (git-ignored; from the owner's client): the 4.x block scan finds the
        // header's block count, and every block of a type we decode is consumed byte for byte.
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "live-nif", name));
        NifSurvey s = NifSurvey.Read(bytes);
        output.WriteLine($"{name}: {s.Version}, {s.BlockTypes.Count}/{s.DeclaredBlocks} blocks");
        foreach (var g in s.BlockTypes.GroupBy(t => t))
            output.WriteLine($"  {g.Key} x{g.Count()}  exact {s.ExactDecodes.GetValueOrDefault(g.Key)}" +
                $"  failed {s.FailedDecodes.GetValueOrDefault(g.Key)}");

        Assert.True(s.BoundariesMatchHeader);
        Assert.Empty(s.FailedDecodes);
    }

    [Fact]
    public void ADecodeThatOverrunsOrFallsShortIsReported()
    {
        // One NiAlphaProperty, decoded exactly; then the same with a stray byte in the body.
        NifSurvey good = NifSurvey.Read(OneAlphaBlock(extraByte: false));
        Assert.Equal(1, good.ExactDecodes["NiAlphaProperty"]);
        Assert.Empty(good.FailedDecodes);
        Assert.Equal(1, good.Features["NiAlphaProperty: flags 0x1221 threshold 75 -> Cutout"]);

        NifSurvey bad = NifSurvey.Read(OneAlphaBlock(extraByte: true));
        Assert.Equal(1, bad.FailedDecodes["NiAlphaProperty"]);
        Assert.Empty(bad.ExactDecodes);
    }

    [Fact]
    public void EveryTypeTheConverterUsesIsOneTheSurveyDecodes()
    {
        // A type the converter claims to handle must be one whose layout the audit can verify.
        var v4 = new NifVersion(4, 2, 2, 0);
        string[] all = [.. NifSurvey.DecodedTypes4x, "NiKeyframeController", "NiStringExtraData"];
        foreach (string t in all)
            if (NifSurvey.Coverage(t, v4) != NifCoverage.Unhandled)
                Assert.Contains(t, NifSurvey.DecodedTypes4x);
        Assert.Equal(NifCoverage.Unhandled, NifSurvey.Coverage("NiKeyframeController", v4));
        Assert.NotEmpty(NifSurvey.CoverageNote("NiTexturingProperty", v4));
    }

    [Fact]
    public void AuditReportListsTypesAndIntegrity()
    {
        var audit = new NifAudit();
        audit.Add("zones", "a.nif", NifSurvey.Read(OneAlphaBlock(extraByte: false)));
        audit.Add("items", "b.nif", NifSurvey.Read(OneAlphaBlock(extraByte: true)));
        audit.AddUnreadable("items", "c.nif", "Not a NIF file");
        string md = audit.ToMarkdown("test");

        Assert.Contains("| NiAlphaProperty | 4.x Handled | 2 | 2 | 4.x | 1/2 |", md);
        Assert.Contains("**NiAlphaProperty**: 1 block(s) did not decode exactly, e.g. b.nif", md);
        Assert.Contains("- **Not a NIF file** (1): c.nif", md);
    }

    // A minimal NetImmerse 4.2.2.0 file: header line, version, one NiAlphaProperty block (empty name,
    // no extra data, no controller, flags 0x1221, threshold 75), then the footer (one root, block 0).
    private static byte[] OneAlphaBlock(bool extraByte)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("NetImmerse File Format, Version 4.2.2.0\n"));
        w.Write(0x04020200u);
        w.Write(1u);
        w.Write((uint)"NiAlphaProperty".Length);
        w.Write(Encoding.ASCII.GetBytes("NiAlphaProperty"));
        w.Write(0u);          // Name (empty)
        w.Write(-1);          // Extra Data
        w.Write(-1);          // Controller
        w.Write((ushort)0x1221);
        w.Write((byte)75);
        if (extraByte)
            w.Write((byte)0);
        w.Write(1u);          // Num Roots
        w.Write(0);           // root: block 0
        return ms.ToArray();
    }
}

public class NifBlockScanTests
{
    [Fact]
    public void TextStartingWithNiInsideABodyIsNotABlock()
    {
        // NiStringExtraData whose text is "NiMAXPSysConverter flag" (as 3ds Max writes it), then a real
        // NiAlphaProperty. Accepting the text as a type name used to end the 2-block scan early and
        // drop the NiAlphaProperty.
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("NetImmerse File Format, Version 4.2.2.0\n"));
        w.Write(0x04020200u);
        w.Write(2u);
        void Sized(string text) { w.Write((uint)text.Length); w.Write(Encoding.ASCII.GetBytes(text)); }
        Sized("NiStringExtraData");
        w.Write(-1);                     // Next Extra Data
        w.Write(0u);                     // Bytes Remaining
        Sized("NiMAXPSysConverter flag");
        Sized("NiAlphaProperty");
        w.Write(0u); w.Write(-1); w.Write(-1); w.Write((ushort)0x1221); w.Write((byte)75);
        w.Write(1u); w.Write(0);         // footer

        // The converter's own scan, capped at the header's block count as NifFile.Read uses it.
        Assert.Equal(["NiStringExtraData", "NiAlphaProperty"],
            NifFile.IndexBlocks(ms.ToArray(), 2).Select(b => b.Type));

        NifSurvey s = NifSurvey.Read(ms.ToArray());
        Assert.Equal(["NiStringExtraData", "NiAlphaProperty"], s.BlockTypes);
        Assert.True(s.BoundariesMatchHeader);
        Assert.Equal(1, s.ExactDecodes["NiAlphaProperty"]);
    }
}
