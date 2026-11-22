// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

public sealed class TreeMapTests
{
    [Fact]
    public void TheNamesAreTheFirstColumnsNifsLowerCaseWithoutTheExtension()
    {
        // treemap.csv's header and row shape (columns after the first two shortened).
        const string csv = "NIF Name,Replacement Tree,Bark Texture,Leaf Texture,zoffset,scale\r\n" +
                           "aeris_oak.NIF,aeris_oak.spt,RiverBirchBark.dds,Aeris_Oak_CMAP.dds,-64,1500\r\n" +
                           "BigHibTree.nif,,,,0,0\r\n" +
                           ",,,,,\r\n" +
                           ".nif,x\r\n";
        Assert.Equal(["aeris_oak", "bighibtree"], TreeMap.Names(csv));
    }
}
