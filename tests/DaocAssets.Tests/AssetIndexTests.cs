// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Library;

namespace DaocAssets.Tests;

/// <summary>
/// Asset index: build a name → location index over a client tree (loose files + archive members),
/// resolve bytes through it, and round-trip it through JSON. Uses a crafted tree and a crafted
/// archive (MpkBuilder), so no real client files are needed.
/// </summary>
public sealed class AssetIndexTests : IDisposable
{
    private readonly string _client = Path.Combine(Path.GetTempPath(), "daoc-idx-" + Guid.NewGuid().ToString("N"));
    private readonly string _json = Path.Combine(Path.GetTempPath(), "daoc-idx-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { if (Directory.Exists(_client)) Directory.Delete(_client, true); } catch { }
        try { if (File.Exists(_json)) File.Delete(_json); } catch { }
    }

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(_client, "items"));
        File.WriteAllBytes(Path.Combine(_client, "items", "Loose.dds"), "DDS loose"u8.ToArray());
        File.WriteAllText(Path.Combine(_client, "items", "sword.nif"), "nif-bytes");

        // A packed model + texture in an .npk.
        byte[] npk = MpkBuilder.Build(
            ("keep.nif", "packed-keep-nif"u8.ToArray()),
            ("canvas1.dds", "packed-canvas"u8.ToArray()));
        Directory.CreateDirectory(Path.Combine(_client, "zones", "Nifs"));
        File.WriteAllBytes(Path.Combine(_client, "zones", "Nifs", "keep.npk"), npk);
    }

    [Fact]
    public void BuildsIndexesLooseAndArchivedAssets()
    {
        Seed();
        AssetIndex index = AssetIndex.Build(_client);

        Assert.True(index.Contains("Loose.dds"));
        Assert.True(index.Contains("sword.nif"));
        Assert.True(index.Contains("keep.nif"));     // from inside the .npk
        Assert.True(index.Contains("canvas1.dds"));  // from inside the .npk
        Assert.False(index.Contains("nope.nif"));

        (int loose, int archived) = index.Breakdown();
        Assert.Equal(2, loose);     // Loose.dds, sword.nif
        Assert.Equal(2, archived);  // keep.nif, canvas1.dds
    }

    [Fact]
    public void ReadsBytesFromLooseAndFromArchive()
    {
        Seed();
        AssetIndex index = AssetIndex.Build(_client);

        Assert.Equal("DDS loose"u8.ToArray(), index.Read("Loose.dds"));
        Assert.Equal("packed-keep-nif"u8.ToArray(), index.Read("keep.nif"));
        Assert.Null(index.Read("missing.nif"));
    }

    [Fact]
    public void LookupIsCaseInsensitiveAndIgnoresPath()
    {
        Seed();
        AssetIndex index = AssetIndex.Build(_client);
        Assert.Equal("packed-keep-nif"u8.ToArray(), index.Read(@"some\path\KEEP.NIF"));
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        Seed();
        AssetIndex.Build(_client).Save(_json);
        AssetIndex reloaded = AssetIndex.Load(_json);

        Assert.True(reloaded.Contains("keep.nif"));
        Assert.Equal("packed-canvas"u8.ToArray(), reloaded.Read("canvas1.dds"));
        Assert.Equal("nif-bytes"u8.ToArray(), reloaded.Read("sword.nif"));
    }

    [Fact]
    public void ReadTextureResolvesAcrossExtensionsAndReturnsTheRealName()
    {
        Directory.CreateDirectory(_client);
        File.WriteAllBytes(Path.Combine(_client, "BPALDOR1.dds"), "dds-bytes"u8.ToArray());
        AssetIndex index = AssetIndex.Build(_client);

        // A NIF names the texture .tga though only .dds ships: resolve to the .dds, by its real name.
        var hit = index.ReadTexture("bpaldor1.tga");
        Assert.NotNull(hit);
        Assert.Equal("dds-bytes"u8.ToArray(), hit!.Value.Bytes);
        Assert.Equal("BPALDOR1.dds", hit.Value.FileName); // on-disk casing, not the .tga reference

        // Exact reference still works and keeps its name; unknown stem is null.
        Assert.Equal("dds-bytes"u8.ToArray(), index.ReadTexture("bpaldor1.dds")!.Value.Bytes);
        Assert.Null(index.ReadTexture("nosuch.tga"));
    }

    [Fact]
    public void ReadTextureDoesNotTurnAModelIntoATexture()
    {
        Directory.CreateDirectory(_client);
        // A stem that exists only as a .nif must not be returned as a texture.
        File.WriteAllText(Path.Combine(_client, "thing.nif"), "nif");
        AssetIndex index = AssetIndex.Build(_client);
        Assert.Null(index.ReadTexture("thing.dds"));
    }

    [Fact]
    public void BuildThrowsOnMissingDirectory()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => AssetIndex.Build(Path.Combine(_client, "does-not-exist")));
    }
}
