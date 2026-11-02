// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using DaocAssets.Zone;
using Xunit;

namespace DaocAssets.Tests;

/// <summary>The sky: zones/sky/sky_*.dat, the zones.dat choice of file, and the INI reader under both.</summary>
public class SkyTests
{
    private const string Sky = """
        ;--- comment
        [main]
        sun_texture   =sundisk.bmp ; trailing comment
        sun_scale     =15.0
        ecliptic_angle =15
        sundisk_color_noon =255,255,255,255
        cloud_top_texture  =cloudsB.dds
        cloud_top_tile     =5.0
        cloud_top_speed    =3.0
        cloud_bot_texture  =cloudsE.dds
        cloud_bot_tile     =4.0
        cloud_bot_speed    =5.0
        [lights_and_fog_clear]
        day_ambient_light =178,208,255
        day_ambient_light_amount =0.5
        day_dynamic_light =255,225,178
        day_dynamic_light_amount =0.6
        day_distance_fog   =141,170,217
        [canopy_color_clear]
        day_zenith=82,134,217,255
        day_nadir=0,0,0,0
        day_east1=82,134,217,255
        day_east2=108,150,217,255
        day_east3=152,177,217,255
        day_east4=217,217,217,255
        day_west1=82,134,217,255
        day_west2=108,150,217,255
        day_west3=152,177,217,255
        day_west4=217,217,217,255
        [cloud_color_clear]
        day_zenith=255,255,255,255
        day_east4=255,255,255,0
        """;

    private static Vector4 C(float r, float g, float b, float a = 255) => new Vector4(r, g, b, a) / 255f;

    [Fact]
    public void IniReaderHandlesCommentsCaseAndColours()
    {
        var ini = IniFile.Parse(Sky);
        Assert.Equal("sundisk.bmp", ini.Get("MAIN", "Sun_Texture"));
        Assert.Equal(C(178, 208, 255), ini.Color("lights_and_fog_clear", "day_ambient_light"));
        Assert.Null(ini.Color("main", "missing"));
    }

    [Fact]
    public void ReadsCloudLayersSunAndTheDayState()
    {
        var sky = SkyDefinition.Parse(Sky);
        Assert.Equal([new CloudLayer("cloudsB", 5, 3), new CloudLayer("cloudsE", 4, 5)], sky.CloudLayers);
        Assert.Equal(("sundisk", 15f, 15f), (sky.SunTexture, sky.SunScale, sky.EclipticAngle));

        SkyState day = sky.State("clear", "day");
        Assert.Equal(C(82, 134, 217), day.Canopy.Zenith);
        Assert.Equal(C(217, 217, 217), day.Canopy.East[3]);
        Assert.Equal(C(152, 177, 217), day.Canopy.West[2]);
        Assert.Equal(new Vector3(178, 208, 255) / 255f, day.AmbientLight);
        Assert.Equal((0.5f, 0.6f), (day.AmbientAmount, day.SunAmount));
        Assert.Equal(new Vector3(141, 170, 217) / 255f, day.DistanceFog);
        Assert.Equal(0f, day.Clouds.East[3].W);   // clouds fade out at the horizon
    }

    [Fact]
    public void MissingConditionsFallBackToClearDay()
    {
        SkyState storm = SkyDefinition.Parse(Sky).State("stormy", "dusk");
        Assert.Equal(C(82, 134, 217), storm.Canopy.Zenith);
        Assert.Equal(0.5f, storm.AmbientAmount);
    }

    [Fact]
    public void ZoneSkyFileComesFromTheZoneThenItsRegionThenTheDefault()
    {
        var zones = IniFile.Parse("""
            [region001]
            skydome=sky_albion.dat
            [region002]
            [zone000]
            region=1
            [zone050]
            region=1
            skydome=sky_volcanus.dat    
            [zone100]
            region=2
            """);
        Assert.Equal("sky_albion.dat", ZoneSky.SkyFile(zones, 0));     // inherited from region 1
        Assert.Equal("sky_volcanus.dat", ZoneSky.SkyFile(zones, 50));  // its own, trailing spaces trimmed
        Assert.Equal("sky_default.dat", ZoneSky.SkyFile(zones, 100));  // region without one
        Assert.Equal("sky_default.dat", ZoneSky.SkyFile(zones, 999));  // unknown zone
    }
}
