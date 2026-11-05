// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>
/// An item's one number for its dye or guild emblem (OpenDAoC SendLivingEquipmentUpdate: the emblem when the
/// item has one), the emblem's layout (EmblemDialogReponseHandler), and its 17th bit riding in the slot byte.
/// </summary>
public class EmblemTests
{
    [Fact]
    public void AnEmblemNumberIsLogoPatternAndTwoColours()
    {
        // Logo 5 (Castle Tower), pattern 1, primary 4 (royal blue), secondary 3 (yellow).
        Assert.Equal(5 << 9 | 1 << 7 | 4 << 3 | 3, new Emblem(5, 1, 4, 3).Number);
        Assert.Equal(new Emblem(5, 1, 4, 3), Emblem.FromNumber(2723));
        Assert.Equal(new Emblem(255, 3, 15, 7), Emblem.FromNumber(new Emblem(255, 3, 15, 7).Number));
        Assert.Null(Emblem.FromNumber(0x1FF));       // no logo: not an emblem
    }

    [Fact]
    public void ADyeIsANumberBelowTheFirstEmblem()
    {
        var dyed = new WornItem(25, 0x3A, 0, 0x25);
        Assert.Equal((0x25, (Emblem?)null), (dyed.Dye, dyed.Emblem));
        var crested = new WornItem(26, 57, 1, 2723);
        Assert.Equal((0, (Emblem?)new Emblem(5, 1, 4, 3)), (crested.Dye, crested.Emblem));
        Assert.Equal(0, new WornItem(26, 57).Dye);
    }

    [Fact]
    public void TheRecordedDyedTorsoIsADye()
    {
        // S→C packet 46 of the recording: a torso with u8 colour 0x25.
        var server = CaptureFile.Login1127e().ServerPackets();
        WornItem torso = Assert.Single(EquipmentUpdate.Parse(server[46].Payload).Items);
        Assert.Equal((0x25, (Emblem?)null), (torso.Dye, torso.Emblem));
    }

    [Fact]
    public void ALogoAbove127CarriesItsTopBitInTheSlotByte()
    {
        // A cloak (26) with logo 200: the number needs 17 bits; the server writes its low 16 as a u16 and sets
        // the slot byte's 0x80 (written as SendLivingEquipmentUpdate does).
        int number = new Emblem(200, 2, 9, 5).Number;
        Assert.True(number > 0xFFFF);
        byte[] payload = new PayloadWriter()
            .U16BE(0x1234).U8(0xFF).U8(0).U8(0).U8(0).U8(1)   // object, active, speed, visibility, hood, count
            .U8(26 | 0x80).U16BE(57 | 0x8000).U8(1).U16BE((ushort)number)
            .ToArray();
        WornItem cloak = Assert.Single(EquipmentUpdate.Parse(payload).Items);
        Assert.Equal((26, 57, 1, true), (cloak.Slot, cloak.Model, cloak.Extension, cloak.EmblemHigh));
        Assert.Equal(new Emblem(200, 2, 9, 5), cloak.Emblem);
    }
}
