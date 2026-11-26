// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using System.Text.Json;
using DaocAssets.Nif;

namespace DaocAssets.Tests;

/// <summary>Particle systems in a model: read from both NIF versions and written for the client in its frame.</summary>
public sealed class NifParticlesTests
{
    private static byte[] Controller(int modifier)
    {
        var w = new List<byte>();
        void I(int v) => w.AddRange(BitConverter.GetBytes(v));
        void F(float v) => w.AddRange(BitConverter.GetBytes(v));
        void S(ushort v) => w.AddRange(BitConverter.GetBytes(v));
        I(-1); S(8); F(1); F(0); F(0); F(3); I(-1);          // NiTimeController: next, flags, freq, phase, start, stop, target
        F(52.5f); F(5.25f); F(0.1f); F(0.2f); F(0.3f); F(0.4f);   // speed, random, declination (+var), planar (+var)
        F(0); F(0); F(1); F(1); F(1); F(1); F(1);             // normal, colour
        F(10); F(0.03f); F(3.03f); w.Add(0);                  // size, emit start, stop, reset byte
        F(30); F(0.4f); F(0.33f); S(1);                       // rate, lifetime, random, emit flags
        F(16); F(16); F(0); I(5);                             // start random box, emitter
        S(0); F(0); I(0); I(0); S(0);                         // unknowns
        S(2); S(2); w.AddRange(new byte[80]);                 // two particles as saved
        I(-1); I(modifier); I(-1); w.Add(0);                  // link, first modifier, link 2, trailer
        return [.. w];
    }

    [Fact]
    public void TheControllerIsReadToItsEnd()
    {
        byte[] bytes = Controller(7);
        var c = new NifCursor(bytes);
        var ctl = Assert.IsType<NifParticleReader.Controller>(NifParticleReader.Read(ref c, "NiParticleSystemController"));
        Assert.Equal(bytes.Length, c.Pos);
        Assert.Equal((52.5f, 10f, 30f, 0.4f, 5, 7, 2), (ctl.Speed, ctl.Size, ctl.EmitRate, ctl.Lifetime, ctl.Emitter, ctl.Modifier, ctl.Particles));
        Assert.Equal((0.1f, 0.2f, 0.3f, 0.4f), (ctl.Declination, ctl.DeclinationVariation, ctl.PlanarAngle, ctl.PlanarAngleVariation));
        Assert.Equal(new Vector3(16, 16, 0), ctl.StartRandom);
    }

    [Fact]
    public void ColourKeysSkipTheirTangents()
    {
        var w = new List<byte>();
        void U(uint v) => w.AddRange(BitConverter.GetBytes(v));
        void F(float v) => w.AddRange(BitConverter.GetBytes(v));
        U(2); U(2);                                            // two quadratic keys
        foreach (float t in new[] { 0f, 1f })
        {
            F(t); F(1); F(0.5f); F(0); F(t == 0 ? 1 : 0);      // time, RGBA
            for (int k = 0; k < 8; k++) F(9);                  // forward and backward tangents
        }
        var c = new NifCursor([.. w]);
        var data = Assert.IsType<NifParticleReader.ColorData>(NifParticleReader.Read(ref c, "NiColorData"));
        Assert.Equal(w.Count, c.Pos);
        Assert.Equal(new Vector4(1, 0.5f, 0, 0), data.Keys[1].Color);
    }

    [Fact]
    public void TheClientGetsMetresAndYUp()
    {
        // An emitter 40 units up (NIF z) sending particles up at 40 units/s, pulled down by gravity.
        var s = new NifParticleSystem
        {
            Name = "fire", Emitter = Matrix4x4.CreateTranslation(0, 0, 40), Texture = "dimflame.dds", Size = 10, Speed = 40,
            StartRandom = new Vector3(16, 8, 0), Modifiers = [new NifParticleGravity(0, 40, false, Vector3.Zero, new Vector3(0, 0, -1))],
        };
        JsonElement j = JsonDocument.Parse(NifParticleJson.ToJson([s])).RootElement.GetProperty("systems")[0];
        float[] e = j.GetProperty("emitter").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        Assert.Equal(1f, e[10], 4);                            // origin y: 40 units = 1 m, up
        Assert.Equal(new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1 }, e[..9].Select(x => MathF.Round(x, 4) + 0f).ToArray());   // no turn
        Assert.Equal((1f, 0.25f), (j.GetProperty("speed").GetSingle(), j.GetProperty("size").GetSingle()));
        float[] box = j.GetProperty("box").EnumerateArray().Select(x => x.GetSingle()).ToArray();
        Assert.Equal(new[] { 0.4f, 0, 0.2f }, box.Select(x => MathF.Round(x, 4)).ToArray());   // NIF x, y -> x, z; flat in y
        JsonElement g = j.GetProperty("modifiers")[0];
        Assert.Equal(-1f, g.GetProperty("direction")[1].GetSingle(), 4);
        Assert.Equal(1f, g.GetProperty("force").GetSingle(), 4);
    }

    [LiveFact("live-nif/campfire_sfx.nif")]
    public void ACampfireHasFlamesSmokeAndEmbers()
    {
        NifModel model = NifFile.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "live-nif", "campfire_sfx.nif")));
        Assert.Equal(new[] { "dimflame.dds", "grysmoke.dds", "hotglow.dds" }, model.Particles.Select(p => p.Texture).ToArray());
        NifParticleSystem flames = model.Particles[0];
        Assert.Equal((30f, 0.4f, 30), (flames.EmitRate, flames.Lifetime, flames.MaxParticles));
        Assert.Contains(flames.Modifiers, m => m is NifParticleColors { Keys.Length: > 1 });
        Assert.Contains(flames.Modifiers, m => m is NifParticleGrowFade);
    }

    [LiveFact("live-nif/vfx_air-1010.nif")]
    public void AModelOfParticlesOnlyIsNotRefused()
    {
        // 10.1, no geometry at all: it used to be refused as "no geometry" and its effect drew nothing.
        NifModel model = NifFile.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "live-nif", "vfx_air-1010.nif")));
        Assert.Empty(model.Meshes);
        Assert.Equal(2, model.Particles.Count);
        Assert.All(model.Particles, p => Assert.True(p.EmitRate > 0 && p.Lifetime > 0 && p.Texture is not null));
    }
}
