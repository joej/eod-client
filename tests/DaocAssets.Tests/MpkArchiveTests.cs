// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using DaocAssets.Mpk;

namespace DaocAssets.Tests;

public sealed class MpkArchiveTests
{
    private static readonly (string, byte[])[] Sample =
    [
        ("hello.txt", "Hello World !"u8.ToArray()),
        ("empty.txt", []),
        ("big.bin", Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray()),
    ];

    [Fact]
    public void RoundTripsEveryMember()
    {
        byte[] bytes = MpkBuilder.Build(Sample);
        using var mpk = MpkArchive.Read(bytes);

        Assert.Equal(3, mpk.Entries.Count);
        Assert.Equal(["hello.txt", "empty.txt", "big.bin"], mpk.Entries.Select(e => e.Name));
        foreach (var (name, data) in Sample)
            Assert.Equal(data, mpk.Extract(name));
    }

    [Fact]
    public void LooksUpNameCaseInsensitively()
    {
        using var mpk = MpkArchive.Read(MpkBuilder.Build(Sample));
        Assert.Equal("Hello World !", Encoding.UTF8.GetString(mpk.Extract("HELLO.TXT")));
        Assert.True(mpk.TryGetEntry("Hello.Txt", out _));
    }

    [Fact]
    public void ReportsUncompressedSizeInTheDirectory()
    {
        using var mpk = MpkArchive.Read(MpkBuilder.Build(Sample));
        Assert.True(mpk.TryGetEntry("big.bin", out MpkEntry big));
        Assert.Equal(5000, big.UncompressedSize);
    }

    [Fact]
    public void MissingMemberThrows()
    {
        using var mpk = MpkArchive.Read(MpkBuilder.Build(Sample));
        Assert.False(mpk.TryGetEntry("nope.txt", out _));
        Assert.Throws<KeyNotFoundException>(() => mpk.Extract("nope.txt"));
    }

    // --- corruption cases: all build on a valid archive and damage one thing ---

    [Fact]
    public void RejectsBadMagic()
    {
        byte[] bytes = MpkBuilder.Build(Sample);
        bytes[0] = (byte)'X';
        Assert.Throws<MpkFormatException>(() => MpkArchive.Read(bytes));
    }

    [Fact]
    public void RejectsWrongSkipByte()
    {
        byte[] bytes = MpkBuilder.Build(Sample);
        bytes[4] = 0x03;
        Assert.Throws<MpkFormatException>(() => MpkArchive.Read(bytes));
    }

    [Fact]
    public void RejectsTruncatedFile()
    {
        byte[] bytes = MpkBuilder.Build(Sample);
        Assert.Throws<MpkFormatException>(() => MpkArchive.Read(bytes.AsSpan(0, bytes.Length - 10).ToArray()));
    }

    [Fact]
    public void RejectsTooShortForAHeader()
    {
        Assert.Throws<MpkFormatException>(() => MpkArchive.Read("MPAK"u8.ToArray()));
    }

    [Fact]
    public void RejectsCorruptMemberData()
    {
        byte[] bytes = MpkBuilder.Build(Sample);
        // Flip a byte in the last member's compressed data (near the end of the file).
        bytes[^3] ^= 0xFF;
        using var mpk = MpkArchive.Read(bytes);
        var ex = Assert.Throws<MpkFormatException>(() => mpk.Extract("big.bin"));
        Assert.Contains("CRC", ex.Message);
    }

    [Fact]
    public void RejectsDuplicateNames()
    {
        byte[] bytes = MpkBuilder.Build([("dup.txt", "a"u8.ToArray()), ("dup.txt", "b"u8.ToArray())]);
        Assert.Throws<MpkFormatException>(() => MpkArchive.Read(bytes));
    }

    // --- live tests over real OpenDAoC 1.127 client archives, when present ---

    public static IEnumerable<object[]> LiveArchives()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "live-mpk");
        if (Directory.Exists(dir))
            foreach (string path in Directory.EnumerateFiles(dir, "*.mpk"))
                yield return [path];
    }

    [Theory]
    [MemberData(nameof(LiveArchives))]
    public void ExtractsEveryMemberOfARealArchive(string path)
    {
        using var mpk = MpkArchive.Open(path);
        Assert.NotEmpty(mpk.Entries);
        foreach (MpkEntry e in mpk.Entries)
        {
            byte[] data = mpk.Extract(e); // verifies CRC + size for every member
            Assert.Equal(e.UncompressedSize, data.Length);
        }
    }

    [Theory]
    [MemberData(nameof(LiveArchives))]
    public void RandomAccessReadsOneMemberWithoutReadingTheRest(string path)
    {
        using var mpk = MpkArchive.Open(path);
        MpkEntry last = mpk.Entries[^1]; // the member furthest into the file
        byte[] data = mpk.Extract(last);
        Assert.Equal(last.UncompressedSize, data.Length);
    }
}
