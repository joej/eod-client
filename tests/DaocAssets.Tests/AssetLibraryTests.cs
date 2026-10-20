// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Library;

namespace DaocAssets.Tests;

/// <summary>
/// Asset library: convert a model once from the index (caching the .glb), gather its textures, and
/// let an override win. Uses a crafted install (loose NIF fixture + crafted archive) indexed in
/// place, so no real client files are needed.
/// </summary>
public sealed class AssetLibraryTests : IDisposable
{
    private readonly string _client = Path.Combine(Path.GetTempPath(), "daoc-lib-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _lib = Path.Combine(Path.GetTempPath(), "daoc-lib-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (string d in new[] { _client, _lib })
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    // A minimal real NIF: reuse the bowsprit fixture if present, else skip (the convert path needs a
    // genuine NIF; the override/texture paths below don't and always run).
    private static string? BowspritFixture()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "live-nif", "sample-422.nif");
        return File.Exists(p) ? p : null;
    }

    private AssetIndex IndexWith(params (string name, byte[] data)[] loose)
    {
        Directory.CreateDirectory(_client);
        foreach (var (name, data) in loose)
            File.WriteAllBytes(Path.Combine(_client, name), data);
        return AssetIndex.Build(_client);
    }

    [Fact]
    public void ConvertsAModelOnceAndCachesTheGlb()
    {
        string? fixture = BowspritFixture();
        if (fixture is null)
            return; // no fixture available; the other tests cover override/texture logic

        // Put the real NIF in the crafted client under a known name, index it.
        Directory.CreateDirectory(_client);
        File.Copy(fixture, Path.Combine(_client, "thing.nif"));
        var library = new AssetLibrary(AssetIndex.Build(_client), _lib);

        ModelResult first = library.GetModel("thing.nif");
        Assert.Equal(AssetOrigin.Converted, first.Origin);
        Assert.NotNull(first.GlbPath);
        Assert.True(File.Exists(first.GlbPath!));

        // Second call is a cache hit (same path, still Converted) and the file is unchanged.
        DateTime written = File.GetLastWriteTimeUtc(first.GlbPath!);
        ModelResult second = library.GetModel("thing.nif");
        Assert.Equal(first.GlbPath, second.GlbPath);
        Assert.Equal(written, File.GetLastWriteTimeUtc(second.GlbPath!));
    }

    [Fact]
    public void AnOverrideWinsOverConversion()
    {
        var library = new AssetLibrary(IndexWith(("x.nif", "whatever"u8.ToArray())), _lib);
        string overrides = Path.Combine(_lib, "overrides");
        Directory.CreateDirectory(overrides);
        File.WriteAllText(Path.Combine(overrides, "x.glb"), "authored glb");

        ModelResult r = library.GetModel("x.nif");
        Assert.Equal(AssetOrigin.Override, r.Origin);
        Assert.Equal(Path.Combine(overrides, "x.glb"), r.GlbPath);
    }

    [Fact]
    public void MissingModelIsReported()
    {
        var library = new AssetLibrary(IndexWith(("present.dds", "x"u8.ToArray())), _lib);
        ModelResult r = library.GetModel("absent.nif");
        Assert.Equal(AssetOrigin.Missing, r.Origin);
        Assert.Null(r.GlbPath);
    }

    [Fact]
    public void GathersATextureFromTheIndexAndPrefersAnOverride()
    {
        byte[] client = "client-dds"u8.ToArray();
        var library = new AssetLibrary(IndexWith(("skin.dds", client)), _lib);

        string gathered = library.GatherTexture("skin.dds")!;
        Assert.Equal(client, File.ReadAllBytes(gathered));
        Assert.Equal(Path.Combine(_lib, "textures", "skin.dds"), gathered);

        // Now add an override; it wins.
        string overrides = Path.Combine(_lib, "overrides");
        Directory.CreateDirectory(overrides);
        File.WriteAllBytes(Path.Combine(overrides, "skin.dds"), "authored"u8.ToArray());
        Assert.Equal(Path.Combine(overrides, "skin.dds"), library.GatherTexture("skin.dds"));
    }
}
