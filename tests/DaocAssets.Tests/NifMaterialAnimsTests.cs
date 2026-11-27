// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Nif;

namespace DaocAssets.Tests;

/// <summary>Material animations (fades, colour, scrolling, blinking) read from both NIF versions.</summary>
public sealed class NifMaterialAnimsTests
{
    private static List<byte> TimeController(ushort flags, int data, params byte[] between)
    {
        var w = new List<byte>();
        w.AddRange(BitConverter.GetBytes(-1));
        w.AddRange(BitConverter.GetBytes(flags));
        foreach (float f in new[] { 1f, 0f, 0f, 2f }) w.AddRange(BitConverter.GetBytes(f));
        w.AddRange(BitConverter.GetBytes(-1));
        w.AddRange(between);
        w.AddRange(BitConverter.GetBytes(data));
        return w;
    }

    [Fact]
    public void ControllersAreReadToTheirEnd()
    {
        // A clamped (flags 12) fade, a 4.x colour controller whose flags name the emissive colour (3 << 4), a UV scroll.
        foreach ((string type, List<byte> body, string kind, int target) in new[]
        {
            ("NiAlphaController", TimeController(12, 7), NifMaterialAnim.Alpha, 0),
            ("NiMaterialColorController", TimeController(8 | (3 << 4), 7), NifMaterialAnim.Color, 3),
            ("NiUVController", TimeController(8, 7, 0, 0), NifMaterialAnim.Uv, 0),
        })
        {
            var c = new NifCursor([.. body]);
            var ctl = Assert.IsType<NifMaterialAnimReader.Controller>(NifMaterialAnimReader.Read(ref c, type, gamebryo10: false));
            Assert.Equal(body.Count, c.Pos);
            Assert.Equal((kind, target, 7, 2f), (ctl.Kind, ctl.Target, ctl.Data, ctl.StopTime));
        }
    }

    [Fact]
    public void UvDataHasFourKeyGroups()
    {
        var w = new List<byte>();
        void U(uint v) => w.AddRange(BitConverter.GetBytes(v));
        void F(float v) => w.AddRange(BitConverter.GetBytes(v));
        U(0);                                   // U offset: no keys
        U(2); U(1); F(0); F(0); F(2); F(1);     // V offset: 0 → 1 over 2 s, linear
        U(0); U(0);                             // tiling: none
        var c = new NifCursor([.. w]);
        var data = Assert.IsType<NifMaterialAnimReader.KeyData>(NifMaterialAnimReader.Read(ref c, "NiUVData", false));
        Assert.Equal(w.Count, c.Pos);
        Assert.Equal(4, data.Groups.Length);
        Assert.Empty(data.Groups[0]);
        Assert.Equal(1f, data.Groups[1][1].Value[0]);
    }

    [LiveFact("live-nif/arrow_cold01_strike.nif")]
    public void AnEffectsMeshesFadeOrBlink()
    {
        NifModel model = NifFile.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "live-nif", "arrow_cold01_strike.nif")));
        Assert.NotEmpty(model.MaterialAnims);
        Assert.All(model.MaterialAnims, a => Assert.Contains(model.Meshes, m => m.Name == a.Mesh));
        string json = NifMaterialAnimJson.ToJson(model);
        Assert.Contains("-anim-mesh", json);
    }
}
