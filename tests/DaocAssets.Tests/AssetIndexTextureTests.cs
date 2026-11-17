// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using DaocAssets.Library;

namespace DaocAssets.Tests;

/// <summary>
/// How a model's texture names resolve (AssetIndex.LocateModelTexture), as the retail client looks them up: a loose file
/// in the model's own folder, where a <c>.tga</c> name takes a <c>.dds</c> of the same name if there is one, then
/// <c>zones/Nifs</c>, <c>.dds</c> first, then the name as given (W63).
/// </summary>
public sealed class AssetIndexTextureTests : IDisposable
{
    private readonly string _client = Path.Combine(Path.GetTempPath(), "daoc-tex-src-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_client))
            Directory.Delete(_client, recursive: true);
    }

    private void Put(string rel, string text)
    {
        string path = Path.Combine(_client, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private (string Text, string FileName) Lookup(string name, string modelFolder)
    {
        AssetIndex index = AssetIndex.Build(_client);
        var found = index.Read(index.LocateModelTexture(name, modelFolder));
        Assert.NotNull(found);
        return (Encoding.UTF8.GetString(found.Value.Bytes), found.Value.FileName);
    }

    private const string City = "zones/zone026/nifs/";

    [Fact]
    public void ATgaNameTakesADdsOfTheSameNameBesideTheModel()
    {
        Put(City + "wall.tga", "own tga");
        Put(City + "wall.dds", "own dds");
        Put("zones/Nifs/wall.dds", "shared dds");
        Assert.Equal(("own dds", "wall.dds"), Lookup("wall.tga", City));
    }

    [Fact]
    public void TheModelsOwnTgaComesBeforeZonesNifs()
    {
        Put(City + "wall.tga", "own tga");
        Put("zones/Nifs/wall.dds", "shared dds");
        Assert.Equal(("own tga", "wall.tga"), Lookup("wall.tga", City));
    }

    [Fact]
    public void AnotherExtensionIsTakenAsNamedBesideTheModel()
    {
        Put(City + "sign.bmp", "own bmp");
        Put(City + "sign.dds", "own dds");
        Assert.Equal(("own bmp", "sign.bmp"), Lookup("sign.bmp", City));
    }

    [Fact]
    public void InZonesNifsADdsComesFirstWhateverTheExtension()
    {
        Put("zones/Nifs/door.bmp", "shared bmp");
        Put("zones/Nifs/door.dds", "shared dds");
        Assert.Equal(("shared dds", "door.dds"), Lookup("door.bmp", "zones/Nifs/"));
        Assert.Equal(("shared dds", "door.dds"), Lookup("door.bmp", City));
    }

    [Fact]
    public void TheNamedFileElsewhereInTheInstallDoesNotBeatZonesNifs()
    {
        // The same name in another folder is not where the client looks; zones/Nifs' .dds is.
        Put("effects/rock.tga", "effects tga");
        Put("zones/Nifs/rock.dds", "shared dds");
        Assert.Equal(("shared dds", "rock.dds"), Lookup("rock.tga", "zones/Nifs/"));
    }

    [Fact]
    public void NotWhereTheClientLooksItFallsBackToTheWholeInstall()
    {
        // Ours: the client would draw the model untextured.
        Put("effects/rock.tga", "effects tga");
        Assert.Equal(("effects tga", "rock.tga"), Lookup("rock.tga", "zones/Nifs/"));
    }

    [Fact]
    public void ADungeonsCopyIsNotTheFallback()
    {
        Put("zones/Dnifs/stone.dds", "dungeon dds");
        Put("zones/Nifs/stone.tga", "shared tga");
        Assert.Equal(("shared tga", "stone.tga"), Lookup("stone.tga", City));
    }
}
