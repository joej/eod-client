// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets;

namespace DaocAssets.Tests;

/// <summary>
/// Texture gathering: a texture is resolved loose-on-disk first, then from any .mpk under the
/// client directory, else reported missing. Uses a crafted temp client tree and a crafted archive
/// (MpkBuilder), so it needs no real client files.
/// </summary>
public sealed class TextureResolverTests : IDisposable
{
    private readonly string _client = Path.Combine(Path.GetTempPath(), "daoc-tex-" + Guid.NewGuid().ToString("N"));
    private readonly string _out = Path.Combine(Path.GetTempPath(), "daoc-texout-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TryDelete(_client);
        TryDelete(_out);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void ResolvesALooseTextureByCopyingIt()
    {
        byte[] bytes = "DDS loose payload"u8.ToArray();
        string loose = Path.Combine(_client, "items");
        Directory.CreateDirectory(loose);
        File.WriteAllBytes(Path.Combine(loose, "ItemSkin.dds"), bytes);

        var r = new TextureResolver(_client);
        TextureResult result = r.Resolve("ItemSkin.dds", _out);

        Assert.Equal(TextureSource.Loose, result.Source);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_out, "ItemSkin.dds")));
    }

    [Fact]
    public void ResolvesLooseCaseInsensitivelyAndIgnoresAnyDirectoryInTheName()
    {
        Directory.CreateDirectory(Path.Combine(_client, "zones"));
        File.WriteAllBytes(Path.Combine(_client, "zones", "GrassTex.dds"), "x"u8.ToArray());

        var r = new TextureResolver(_client);
        // Different case, and a path-qualified name: only the file name should matter.
        TextureResult result = r.Resolve(@"textures\grasstex.DDS", _out);

        Assert.Equal(TextureSource.Loose, result.Source);
        Assert.True(File.Exists(Path.Combine(_out, "GrassTex.dds")));
    }

    [Fact]
    public void ExtractsATextureFromAnMpkWhenNotLoose()
    {
        byte[] skin = "DDS packed creature skin"u8.ToArray();
        byte[] archive = MpkBuilder.Build(("AmbientCreaturesTexture.dds", skin), ("other.dat", "junk"u8.ToArray()));
        string skins = Path.Combine(_client, "figures", "skins");
        Directory.CreateDirectory(skins);
        File.WriteAllBytes(Path.Combine(skins, "skin174.mpk"), archive);

        var r = new TextureResolver(_client);
        TextureResult result = r.Resolve("AmbientCreaturesTexture.dds", _out);

        Assert.Equal(TextureSource.Archive, result.Source);
        Assert.Equal("skin174.mpk", Path.GetFileName(result.FoundIn));
        Assert.Equal(skin, File.ReadAllBytes(Path.Combine(_out, "AmbientCreaturesTexture.dds")));
    }

    [Fact]
    public void PrefersALooseFileOverAnArchiveCopy()
    {
        byte[] looseBytes = "the loose one"u8.ToArray();
        Directory.CreateDirectory(Path.Combine(_client, "loosedir"));
        File.WriteAllBytes(Path.Combine(_client, "loosedir", "shared.dds"), looseBytes);
        byte[] archive = MpkBuilder.Build(("shared.dds", "the packed one"u8.ToArray()));
        Directory.CreateDirectory(Path.Combine(_client, "packdir"));
        File.WriteAllBytes(Path.Combine(_client, "packdir", "a.mpk"), archive);

        var r = new TextureResolver(_client);
        TextureResult result = r.Resolve("shared.dds", _out);

        Assert.Equal(TextureSource.Loose, result.Source);
        Assert.Equal(looseBytes, File.ReadAllBytes(Path.Combine(_out, "shared.dds")));
    }

    [Fact]
    public void ReportsMissingWhenNowhereToBeFound()
    {
        Directory.CreateDirectory(_client);
        var r = new TextureResolver(_client);
        TextureResult result = r.Resolve("nope.dds", _out);

        Assert.Equal(TextureSource.Missing, result.Source);
        Assert.Null(result.FoundIn);
        Assert.False(File.Exists(Path.Combine(_out, "nope.dds")));
    }

    [Fact]
    public void ThrowsWhenTheClientDirectoryDoesNotExist()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => new TextureResolver(Path.Combine(_client, "does-not-exist")));
    }
}
