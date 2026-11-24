// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>Crafting: C→S 0xED.</summary>
public sealed class CraftPacketTests
{
    [Fact]
    public void ARecipeIdIsReadAsOpenDaocReadsIt()
    {
        byte[] payload = CraftRequest.Payload(4154);
        Assert.Equal(new byte[] { 0x10, 0x3A }, payload);
        // MakeProductHandler: packet.ReadShort() = first byte << 8 | second byte.
        Assert.Equal(4154, payload[0] << 8 | payload[1]);
        Assert.Equal(0xED, CraftRequest.Code);
    }
}
