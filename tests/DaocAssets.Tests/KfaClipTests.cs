// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using System.Text;
using DaocAssets.Nif;

namespace DaocAssets.Tests;

/// <summary>
/// Animation clips (.kfa, 4.2.1.0): the root's string chain names bones by their index in
/// figures/animnode.dat, and its controller chain, in the same order, holds each bone's keys.
/// </summary>
public sealed class KfaClipTests
{
    private static readonly string[] BoneNames = ["Bip01", "Bip01 Pelvis", "Bip01 Spine"];

    // A clip of two bones: "1" (Pelvis: two rotation keys) and "0" (Bip01: a quadratic translation key
    // and a scale key), shaped like the install's I_hm.kfa.
    private static byte[] Clip()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("NetImmerse File Format, Version 4.2.1.0\n"));
        w.Write(0x04020100u);
        w.Write(7u);
        void Sized(string s) { w.Write((uint)s.Length); w.Write(Encoding.ASCII.GetBytes(s)); }

        Sized("NiNode"); Sized(""); w.Write(1); w.Write(3);                  // 0: root, extra 1, controller 3
        w.Write((ushort)0x000C);
        for (int k = 0; k < 3 + 9; k++) w.Write(k is 3 or 7 or 11 ? 1f : 0f);
        w.Write(1f); for (int k = 0; k < 3; k++) w.Write(0f);
        w.Write(0u); w.Write((byte)0); w.Write(0u); w.Write(0u);
        Sized("NiStringExtraData"); w.Write(2); w.Write(5u); Sized("1");     // 1: bone 1 (Pelvis)
        Sized("NiStringExtraData"); w.Write(-1); w.Write(5u); Sized("0");    // 2: bone 0 (Bip01)
        void Controller(int next, float stop, int data)
        {
            Sized("NiKeyframeController"); w.Write(next); w.Write((ushort)0x000C);
            w.Write(1f); w.Write(0f); w.Write(0f); w.Write(stop); w.Write(-1); w.Write(data);
        }
        Controller(4, 2f, 5);                                                   // 3
        Controller(-1, 1.5f, 6);                                                // 4
        Sized("NiKeyframeData");                                                // 5: two LINEAR rotations
        w.Write(2u); w.Write(1u);
        w.Write(0f); w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f);       // t 0: identity (w x y z)
        w.Write(1f); w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);       // t 1: 180 degrees about Z
        w.Write(0u); w.Write(0u);                                               // no translations, scales
        Sized("NiKeyframeData");                                                // 6
        w.Write(0u);                                                            // no rotations
        w.Write(1u); w.Write(2u);                                               // one QUADRATIC translation
        w.Write(0.5f); w.Write(1f); w.Write(2f); w.Write(3f);                  // time, value
        for (int k = 0; k < 6; k++) w.Write(9f);                                // forward, backward tangents
        w.Write(1u); w.Write(1u); w.Write(0.25f); w.Write(1.5f);              // one LINEAR scale
        w.Write(1u); w.Write(0);                                                // footer
        return ms.ToArray();
    }

    [Fact]
    public void ReadsEachBonesKeysByTheStringChainsOrder()
    {
        KfaClip clip = KfaClip.Read(Clip(), BoneNames);
        Assert.Equal(2f, clip.Duration);
        Assert.Equal(["Bip01 Pelvis", "Bip01"], clip.Tracks.Select(t => t.Bone));

        KfaTrack pelvis = clip.Tracks[0];
        Assert.Equal([0f, 1f], pelvis.Rotations.Select(r => r.Time));
        Assert.Equal(Quaternion.Identity, pelvis.Rotations[0].Value);
        Assert.Equal(new Quaternion(0, 0, 1, 0), pelvis.Rotations[1].Value);   // file order w x y z
        Assert.Empty(pelvis.Translations);

        KfaTrack root = clip.Tracks[1];
        Assert.Equal((0.5f, new Vector3(1, 2, 3)), root.Translations.Single());  // tangents skipped
        Assert.Equal((0.25f, 1.5f), root.Scales.Single());
    }

    [Fact]
    public void ClipsBecomeGltfAnimationsOnTheJointsTheyName()
    {
        // A two-joint skeleton with one skinned triangle; the clip animates both joints.
        var model = new NifModel
        {
            Version = new NifVersion(10, 1, 0, 0),
            Joints =
            [
                new NifJoint { Name = "Bip01", Parent = -1, Local = Matrix4x4.Identity },
                new NifJoint { Name = "Bip01 Pelvis", Parent = 0, Local = Matrix4x4.Identity },
            ],
            Meshes =
            [
                new NifMesh
                {
                    Name = "body", Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], Triangles = [(0, 1, 2)],
                    SkinJoints = [(1, Matrix4x4.Identity)], Skin = [[(0, 1f)], [(0, 1f)], [(0, 1f)]],
                },
            ],
        };
        var gltf = GltfExporter.BuildScene(model, new Dictionary<string, KfaClip> { ["idle"] = KfaClip.Read(Clip(), BoneNames) })
            .ToGltf2();
        var idle = Assert.Single(gltf.LogicalAnimations);
        Assert.Equal("idle", idle.Name);
        Assert.Contains(idle.Channels, c => c.TargetNode.Name == "Bip01 Pelvis" && c.TargetNodePath == SharpGLTF.Schema2.PropertyPath.rotation);
        Assert.Contains(idle.Channels, c => c.TargetNode.Name == "Bip01" && c.TargetNodePath == SharpGLTF.Schema2.PropertyPath.translation);
    }

    [Fact]
    public void AClipWhoseChainsDisagreeIsRefused()
    {
        // Three bone names but two controllers: the pairing by order would be wrong, so the clip fails.
        Assert.Throws<NifFormatException>(() => KfaClip.Read(WithExtraString(), BoneNames));
    }

    // The clip with a third bone name in the string chain and no controller for it.
    private static byte[] WithExtraString()
    {
        byte[] clip = Clip();
        string text = Encoding.Latin1.GetString(clip);
        // Point the last string's Next at a new string appended as block 7, and bump the block count.
        int last = text.IndexOf("NiStringExtraData", text.IndexOf("NiStringExtraData", StringComparison.Ordinal) + 1, StringComparison.Ordinal)
                   + "NiStringExtraData".Length;
        BitConverter.GetBytes(7).CopyTo(clip, last);
        int footer = clip.Length - 8;
        var ms = new MemoryStream();
        ms.Write(clip, 0, footer);
        var w = new BinaryWriter(ms);
        w.Write((uint)"NiStringExtraData".Length); w.Write(Encoding.ASCII.GetBytes("NiStringExtraData"));
        w.Write(-1); w.Write(5u); w.Write(1u); w.Write(Encoding.ASCII.GetBytes("2"));
        w.Write(1u); w.Write(0);
        byte[] result = ms.ToArray();
        int nl = Array.IndexOf(result, (byte)'\n');
        BitConverter.GetBytes(8u).CopyTo(result, nl + 1 + 4);
        return result;
    }

    [Fact]
    public void KeysThatAreNotFiniteNumbersAreDropped()
    {
        var track = new KfaTrack("Bip01",
            [(0f, Quaternion.Identity), (0.5f, new Quaternion(float.NaN, 0, 0, 1)), (1f, Quaternion.Identity)],
            [(0f, Vector3.Zero), (0.5f, new Vector3(1, float.PositiveInfinity, 0)), (float.NaN, Vector3.One), (1f, Vector3.One)],
            [(0f, 1f), (1f, float.NaN)]);
        KfaTrack finite = KfaClip.Finite(track);
        Assert.Equal([0f, 1f], finite.Rotations.Select(k => k.Time));
        Assert.Equal([0f, 1f], finite.Translations.Select(k => k.Time));
        Assert.Equal([0f], finite.Scales.Select(k => k.Time));
    }
}
