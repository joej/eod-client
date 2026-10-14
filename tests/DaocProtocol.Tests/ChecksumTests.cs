// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;

namespace DaocProtocol.Tests;

public class ChecksumTests
{
    // Hand-worked vectors from RustDAoC (GPL-3.0-or-later), crates/caer-protocol/src/checksum.rs:
    // [0x01]: val1 = 0x7F, val2 = 0xFD, 0xFD - (0x17C << 8) = 0x84FD (mod 2^16)
    // []:     val1 = val2 = 0x7E,      0x7E - (0xFC << 8)  = 0x047E (mod 2^16)
    [Theory]
    [InlineData(new byte[] { 0x01 }, 0x84FD)]
    [InlineData(new byte[0], 0x047E)]
    public void KnownVectors(byte[] data, int expected) =>
        Assert.Equal((ushort)expected, Checksum.Calculate(data));

    [Fact]
    public void WriteThenVerifyRoundTrips()
    {
        byte[] packet = [0x10, 0x20, 0x30, 0xAB, 0xCD, 0, 0];
        Checksum.Write(packet);
        Assert.True(Checksum.Verify(packet));
    }

    [Fact]
    public void EverySingleBitFlipIsCaught()
    {
        byte[] packet = [0x10, 0x20, 0x30, 0xAB, 0xCD, 0, 0];
        Checksum.Write(packet);
        for (int i = 0; i < packet.Length * 8; i++)
        {
            packet[i / 8] ^= (byte)(1 << (i % 8));
            Assert.False(Checksum.Verify(packet), $"bit {i} flipped but checksum still verified");
            packet[i / 8] ^= (byte)(1 << (i % 8));
        }
    }

    [Fact]
    public void TooShortToHoldAChecksumDoesNotVerify() =>
        Assert.False(Checksum.Verify([0x84]));
}
