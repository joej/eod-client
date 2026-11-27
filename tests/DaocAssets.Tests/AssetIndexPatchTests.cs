// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Library;
using DaocAssets.Nif;

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

    // Kobold Undercity's room 0 piece (zone 243): six doors, and three patch files. Merged with its patches it must keep
    // its doors (they were lost there until 2026-11-17, so its doors.csv missed 243000001 … 243000006).
    [LiveFact("live-mpk/koboldroom01.npk", "live-mpk/koboldroom01_p00.npk", "live-mpk/koboldroom01_p01.npk", "live-mpk/koboldroom01_p02.npk")]
    public void APatchedModelKeepsItsDoors()
    {
        string dnifs = Folder("zones/Dnifs");
        foreach (string f in new[] { "koboldroom01.npk", "koboldroom01_p00.npk", "koboldroom01_p01.npk", "koboldroom01_p02.npk" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "live-mpk", f), Path.Combine(dnifs, f));
        AssetIndex index = AssetIndex.Build(_client);
        IReadOnlyList<byte[]> patches = index.ReadPatches("KoboldRoom01.NIF", null);
        Assert.Equal(3, patches.Count);
        NifModel model = NifFile.Read(index.Read("KoboldRoom01.NIF", null)!, patches);
        Assert.Equal(["door001", "door002", "door003", "door004", "door005", "door006"], model.Doors.Select(d => d.Name).Order());
        Assert.Equal(12, model.Movers.Count(m => m.Door.Length > 0));   // others move outside the doors (NifAnimation.All)
        Assert.All(model.Meshes.Where(m => m.Mover >= 0), m => Assert.InRange(m.Mover, 0, model.Movers.Count - 1));
    }
}
