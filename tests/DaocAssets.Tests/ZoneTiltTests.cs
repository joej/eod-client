// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>
/// fixtures.csv's Collide column and 3D Angle/Axis (daoc-re RE/69): read into the layout, carried through zone.csv,
/// and turned into the placement's rotation. Crafted rows in the file's own layout.
/// </summary>
public sealed class ZoneTiltTests
{
    private const string Nifs = "Grid Nifs,,,,\r\nNIF #,Textual Name,Filename,\r\n1,Rock,rock.nif,\r\n";
    private const string Header = "Fixtures,,\r\nID,NIF #,Textual Name,X,Y,Z,A,Scale,Collide,Radius,Animate,Ground,Flip,Cave,Unique ID,3D Angle,3D Axis X,3D Axis Y,3D Axis Z\r\n";

    [Fact]
    public void CollideAndATiltAreRead()
    {
        string fixtures = Header +
            "1,1,Rock,100,200,300,0,100,0,0,0,0,0,0,1,0.000000,0.000000,0.000000,0.000000\r\n" +      // not solid, no axis
            "2,1,Rock,100,200,300,315,100,2048,0,0,0,0,0,2,0.785398,0.000000,0.000000,1.000000\r\n" + // vertical: A kept
            "3,1,Rock,100,200,300,90,100,1,0,0,0,0,0,3,0.500000,1.000000,0.000000,0.000000\r\n";      // tilted about x
        IReadOnlyList<ZoneFixture> f = ZoneLayout.Parse(Nifs, fixtures, 0, 0).Fixtures;
        Assert.Equal((0, 0f), (f[0].Collide, f[0].Tilt));
        Assert.Equal((2048, 315, 0f), (f[1].Collide, f[1].AngleDeg, f[1].Tilt));
        Assert.Equal((0.5f, 1f, 0f, 0f), (f[2].Tilt, f[2].AxisX, f[2].AxisY, f[2].AxisZ));
    }

    [Fact]
    public void AVerticalTiltTurnsAsTheAngleDoes()
    {
        // The sign rests on this: for a vertical axis the 3D Angle is -A (9,984 rows), so both must give one turn.
        var byAngle = new ZonePlacement("rock.glb", 0, 0, 0, 315, 1).Rotation;
        var byTilt = new ZonePlacement("rock.glb", 0, 0, 0, 0, 1, 1, 45 * MathF.PI / 180, 0, 0, 1).Rotation;
        Vector3 v = new(1, 0, 0);
        Vector3 a = Vector3.Transform(v, byAngle), b = Vector3.Transform(v, byTilt);
        Assert.Equal(a.X, b.X, 4);
        Assert.Equal(a.Z, b.Z, 4);
    }

    [Fact]
    public void ATiltLeansTheModelOffTheVertical()
    {
        // 30° about the zone's x axis: the model's up (glTF +Y) leans by 30°.
        var p = new ZonePlacement("rock.glb", 0, 0, 0, 0, 1, 1, 30 * MathF.PI / 180, 1, 0, 0);
        Vector3 up = Vector3.Transform(Vector3.UnitY, p.Rotation);
        Assert.Equal(MathF.Cos(30 * MathF.PI / 180), up.Y, 4);
    }

    [Fact]
    public void TheManifestCarriesCollideAndTilt()
    {
        const string csv = "glb,x,y,z,angle,scale,collide,tilt,axisx,axisy,axisz\n" +
                           "rock.glb,1,2,3,90,1.5,0,0.5,1,0,0\n" +
                           "old.glb,1,2,3,90,1.5\n";                       // an older manifest: solid, no tilt
        List<ZonePlacement> p = ZoneCheck.ReadManifest(csv);
        Assert.Equal((0, 0.5f, 1f), (p[0].Collide, p[0].Tilt, p[0].AxisX));
        Assert.Equal((1, 0f), (p[1].Collide, p[1].Tilt));
    }
}

/// <summary>Water types as the client reads them (daoc-re RE/69): by the first letter, lava being "l" with "v" third.</summary>
public sealed class WaterKindTests
{
    [Theory]
    [InlineData("RIVER", WaterKind.River)]
    [InlineData("LAKE", WaterKind.Lake)]
    [InlineData("LAVA", WaterKind.Lava)]
    [InlineData("lava", WaterKind.Lava)]
    [InlineData("OCEAN", WaterKind.Ocean)]
    [InlineData("SWAMP", WaterKind.Swamp)]
    [InlineData("DEEPOCEAN", WaterKind.DeepOcean)]
    [InlineData("", WaterKind.None)]
    public void ATypeIsReadByItsLetters(string type, WaterKind kind) => Assert.Equal(kind, WaterBody.Classify(type));

    [Fact]
    public void TheClientsRowCarriesTheType()
    {
        string csv = WaterBodyCsvHeader + "\nwater3,\"Fire, Lake\",LAVA,1200,lava,,10,0\n";
        Assert.Equal(WaterKind.Lava, WaterBody.Classify(ZoneWater.ReadCsv(csv)["water3"].Type));
    }

    private const string WaterBodyCsvHeader = ZoneWater.CsvHeader;
}
