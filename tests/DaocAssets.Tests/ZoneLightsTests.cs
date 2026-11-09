// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>A zone's fixed lights: lights.csv rows coloured and sized by LIGHTDEF.CSV (daoc-re RE/69). Rows in the files' layouts.</summary>
public sealed class ZoneLightsTests
{
    private const string Defs = "Blowoff text line 1,,,,,,,\r\nBlowoff text line 2,,,,,,,\r\n" +
                                "0,default light,255,255,255,10,256,1\r\n9,fire-light,255,100,0,10,256,1\r\n" +
                                "19,BIGfire-light,255,100,0,10,512,1\r\n30,odd light,10,20,30,10,,1\r\n";

    [Fact]
    public void ARowTakesItsDefinitionsColourAndRange()
    {
        var defs = ZoneLights.ParseDefinitions(Defs);
        const string rows = "36462,4578,3044,9\r\n9266,52384,2393,19\r\n1,2,3,-1\r\n4,5,6,77\r\n7,8,9,30\r\n";
        List<ZoneLight> lights = ZoneLights.Parse(rows, defs, 1000, 2000);
        Assert.Equal(3, lights.Count);   // -1 and an unknown type are skipped
        Assert.Equal(new ZoneLight(37462, 6578, 3044, 255, 100, 0, 256), lights[0]);
        Assert.Equal(512, lights[1].Range);                    // a BIG light
        Assert.Equal(ZoneLights.DefaultRange, lights[2].Range); // no range given: 256
        Assert.Equal(lights, ZoneLights.ReadCsv(ZoneLights.ToCsv(lights)));
    }
}
