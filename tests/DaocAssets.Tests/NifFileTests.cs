// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using DaocAssets.Nif;

namespace DaocAssets.Tests;

/// <summary>
/// NIF reader tests. The live tests run against real client NIFs in git-ignored
/// private/nif-fixtures (copied to output when present) and assert the golden geometry decoded
/// by hand and cross-checked against nif.xml. They skip when the fixtures are absent.
/// </summary>
public sealed class NifFileTests
{
    // A single case when the fixture is present, none otherwise, so the test skips cleanly.
    public static IEnumerable<object[]> Bowsprit()
    {
        string p = Path.Combine(AppContext.BaseDirectory, "live-nif", "sample-422.nif");
        if (File.Exists(p))
            yield return [p];
    }

    [Theory]
    [MemberData(nameof(Bowsprit))]
    public void ReadsTheBowspritGoldenGeometry(string path)
    {
        // items/bowsprit_alb_01.NIF, NetImmerse 4.2.2.0.
        NifModel model = NifFile.Read(File.ReadAllBytes(path));
        Assert.Equal(new NifVersion(4, 2, 2, 0), model.Version);
        NifMesh mesh = Assert.Single(model.Meshes);

        Assert.Equal(22, mesh.Vertices.Length);
        Assert.Equal(12, mesh.Triangles.Length);
        Assert.NotNull(mesh.Normals);
        Assert.NotNull(mesh.TexCoords);
        Assert.Equal((ushort)0, mesh.Triangles[0].A);
        Assert.Equal((ushort)2, mesh.Triangles[0].C);
        Assert.All(mesh.Triangles, t => Assert.True(t.A < 22 && t.B < 22 && t.C < 22));

        // Byte-for-byte golden hashes from the verified hand-decode.
        Assert.Equal("2d5b083725f5f978", VertHash(mesh));
        Assert.Equal("a29c9f18d2b00746", TriHash(mesh));
    }

    [Fact]
    public void RejectsNonNif()
    {
        Assert.Throws<NifFormatException>(() => NifFile.Read("not a nif at all"u8.ToArray()));
    }

    private static string VertHash(NifMesh m)
    {
        byte[] buf = new byte[m.Vertices.Length * 12];
        int o = 0;
        foreach (var v in m.Vertices)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o), v.X); o += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o), v.Y); o += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(o), v.Z); o += 4;
        }
        return Convert.ToHexStringLower(SHA256.HashData(buf))[..16];
    }

    private static string TriHash(NifMesh m)
    {
        byte[] buf = new byte[m.Triangles.Length * 6];
        int o = 0;
        foreach (var (a, b, c) in m.Triangles)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), a); o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), b); o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), c); o += 2;
        }
        return Convert.ToHexStringLower(SHA256.HashData(buf))[..16];
    }
}
