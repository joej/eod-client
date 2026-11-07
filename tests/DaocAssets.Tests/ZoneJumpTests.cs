// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Zone;

namespace DaocAssets.Tests;

/// <summary>Jump points: zonejump.csv rows (the layout of Camelot Hills' own, numbers kept, checked against
/// OpenDAoC's zonepoint table) and the walk-through test the client uses to send the zone point.</summary>
public sealed class ZoneJumpTests
{
    // Camelot Hills' first rows: the gate into Camelot City (zone point 6) and the Caerwent row with its two
    // extra columns. A row with no zone point is skipped.
    private const string Csv =
        "1,CamCity,3200,28712,3053,28712,2912,3072,6,,\r\n" +
        "6,Caerwent,6033,41154,6043,40857,2515,2782,111,0,1\r\n" +
        "7,Broken,1,2,3,4,5,6,,,\r\n";

    [Fact]
    public void ReadsTheDoorwaysInRegionCoordinatesWithTheirZonePoints()
    {
        List<ZoneJump> jumps = ZoneJumps.Parse(Csv, offsetX: 548864, offsetY: 483328);

        Assert.Equal(2, jumps.Count);
        Assert.Equal(new ZoneJump(6, "CamCity", 552064, 512040, 551917, 512040, 2912, 3072), jumps[0]);
        Assert.Equal(111, jumps[1].ZonePoint);
        // The CSV the converter writes reads back the same.
        Assert.Equal(jumps, ZoneJumps.ReadCsv(ZoneJumps.ToCsv(jumps)));
    }

    [Fact]
    public void AStepThroughTheDoorwayCrossesItAndOthersDoNot()
    {
        ZoneJump gate = ZoneJumps.Parse(Csv, 548864, 483328)[0];   // y = 512040, x 551917..552064, z 2912..3072

        Assert.True(ZoneJumps.Crosses(gate, 552000, 512000, 552000, 512080, z: 2950));    // through, southwards
        Assert.True(ZoneJumps.Crosses(gate, 552000, 512080, 552000, 512000, z: 2950));    // and back
        Assert.False(ZoneJumps.Crosses(gate, 552000, 511900, 552000, 512000, z: 2950));   // short of it
        Assert.False(ZoneJumps.Crosses(gate, 552200, 512000, 552200, 512080, z: 2950));   // beside the doorway
        Assert.False(ZoneJumps.Crosses(gate, 552000, 512000, 552000, 512080, z: 3500));   // over it
        Assert.True(ZoneJumps.Crosses(gate, 552000, 512000, 552000, 512080, z: 2880));    // a little under its floor
    }
}
