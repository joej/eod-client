// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Library;

namespace DaocAssets.Tests;

/// <summary>
/// How a model's patch files are found (AssetIndex.ReadPatches), as the retail client probes them: <c>_p00</c>, <c>_p01</c>,
/// … beside the model, stopping at the first file that is missing; a patch archive without its <c>&lt;stem&gt;_pNN.nif</c>
/// does nothing.
/// </summary>
public sealed class AssetIndexPatchTests : IDisposable
{
    private readonly string _client = Path.Combine(Path.GetTempPath(), "daoc-patch-src-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_client))
            Directory.Delete(_client, recursive: true);
    }

    private string Folder(string rel)
    {
        string dir = Path.Combine(_client, rel);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ALooseModelsPatchesStopAtTheFirstMissingNumber()
    {
        string nifs = Folder("zones/Nifs");
        File.WriteAllBytes(Path.Combine(nifs, "rock.nif"), [0]);
        File.WriteAllBytes(Path.Combine(nifs, "ROCK_P00.NIF"), [1]);
        File.WriteAllBytes(Path.Combine(nifs, "rock_p02.nif"), [3]);   // after the gap: never reached
        File.WriteAllBytes(Path.Combine(Folder("items"), "rock_p01.nif"), [2]);   // another folder: not beside it
        IReadOnlyList<byte[]> patches = AssetIndex.Build(_client).ReadPatches("rock.nif", null);
        Assert.Equal([1], Assert.Single(patches));
    }

    // City of Camelot's cam32: its patch archive holds only "statue.nif", not cam32_p00.nif, so the patch does nothing.
    [LiveFact("live-mpk/cam32.npk", "live-mpk/cam32_p00.npk")]
    public void APatchArchiveWithoutItsModelDoesNothing()
    {
        string nifs = Folder("zones/zone026/nifs");
        foreach (string f in new[] { "cam32.npk", "cam32_p00.npk" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "live-mpk", f), Path.Combine(nifs, f));
        AssetIndex index = AssetIndex.Build(_client);
        Assert.NotNull(index.Read("cam32.nif", null));
        Assert.Empty(index.ReadPatches("cam32.nif", null));
    }
}
