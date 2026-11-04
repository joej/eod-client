// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using DaocAssets.Figures;
using DaocAssets.Nif;

namespace DaocAssets.Tests;

/// <summary>
/// Catacombs figures: a model id's Catacombs version resolved through monsters.csv, monnifs.csv,
/// fig3map.csv, fig3parts.csv and fig3haircolormap.csv (rows shaped like the install's, cut down), and
/// parts bound to a skeleton by bone name. The live tests read real files from git-ignored
/// private/nif-fixtures and skip when they are absent.
/// </summary>
public sealed class CatacombsFigureTests
{
    // A monsters.csv row with the Catacombs columns filled: Cata Nif (67), CatSkin Body (68), Head (70),
    // Hair (76, empty here), Cata Scale (81).
    private static string MonsterRow(int id, string name, string cataNif, string body, string head, string hair)
    {
        var r = Enumerable.Repeat("", 82).ToArray();
        (r[0], r[1], r[2]) = (id.ToString(), name, "206");
        (r[67], r[68], r[70], r[76], r[81]) = (cataNif, body, head, hair, "110");
        return string.Join(',', r);
    }

    private static MonsterTables Tables() =>
        MonsterTables.Parse(
            "Information,,NIF\nID,name,#\n" + MonsterRow(32, "Briton Male", "582", "3233", "3238", "") + "\n"
                + MonsterRow(33, "No Catacombs", "", "", "", ""),
            // monnifs: ..., Figure Version (12), Cata Index (13), Cata Anim Set (14), Skeleton Archive (15).
            "Information,,NIF\nID,Textual Name,Name\n582,Cata Briton Male,bri_m,233,178,70,70,70,0,4,1,,3,1,,1",
            "Information,,Skin\nID,Textual Name,Name\n3233,Briton ss Body,CthBody02_ss_bri_M.dds\n"
                + "3238,briton head1,Bri_m_Head01.dds\n3778,head2,Bri_m_Head02.dds\n3804,hair,Bri_m_Hair01_blonde.dds\n"
                + "3808,hair,bri_m_hair02_blonde.dds\n6275,eyes,eyecolor_1.dds\n6276,eyes,eyecolor_2.dds")
        .WithFig3(
            "id,,\n#,part,desc\n1,0,Briton Male Head,1306,,\n1,1,Briton Male Body,291,292,\n1,8,Briton Male Hair,5,,7,",
            "ID,Text,Part,Expansion,Test,Archive,Double\n1306,bri_m_head01,bri_m_head01,4,1,15,\n"
                + "291,Body,Body01_BC_m,4,1,2,\n292,Body,Body02_BC_m,4,1,3,\n5,Briton Male Hair,bri_m_Hair01,4,1,15,1\n"
                + "7,Briton Male Hair,bri_m_Hair03,4,1,15,1",
            ",hair,\n#,style #,Description,1S,1T,1M\n1,1,Cata Briton Male,3804,188,,3804,125,\n1,2,Cata Briton Male,3808,188,,,,\n"
                + "1,3,Cata Briton Male,3804,188,,,,")
        .WithFig3Looks(
            "id,,\n#,Description,1,2\n1, Briton Male,3238,3778,,",
            "id,,\n#,Description,1,2\n1,Cata Briton Male,6275,6276",
            "id,,\n#,,1,2\n1,Cata Briton Male,1,8",
            "Information,,\nID,Textual Name,Red,Green,Blue\n1,Norse Skin 1,255,255,255\n8,Norse Skin 8,119,60,14\n"
                + "125,Hair Tint 29,255,243,176\n188,No tint,255,255,255");

    [Fact]
    public void AModelIdResolvesToItsSkeletonAndOnePartPerSkinnedSlot()
    {
        CataSpec spec = Tables().ResolveCatacombs(32)!;
        Assert.Equal(("Cata Briton Male", 1, "bri_m_skeleton.nif", 1.1f), (spec.Name, spec.SkeletonArchive, spec.Skeleton, spec.Scale));
        Assert.Equal(
            [
                new CataPart(1, 3, "Body02_BC_m.nif", "CthBody02_ss_bri_M.dds"),   // the skin's 02: body variant 2
                new CataPart(0, 15, "bri_m_head01.nif", "Bri_m_Head01.dds"),
                new CataPart(8, 15, "bri_m_Hair01.nif", "Bri_m_Hair01_blonde.dds"), // no hair skin: style 1, colour 1
            ],
            spec.Parts);
    }

    [Fact]
    public void EveryHairStyleOfTheRaceIsListedAndAnEmptyOneIsBald()
    {
        CataSpec spec = Tables().ResolveCatacombs(32)!;
        Assert.Equal(1, spec.Figure);
        Assert.Equal([new CataPart(8, 15, "bri_m_Hair01.nif", null), null, new CataPart(8, 15, "bri_m_Hair03.nif", null)], spec.HairStyles);
        Assert.Equal(0, spec.DefaultHair);
    }

    [Fact]
    public void ARacesAppearanceOptionsComeFromTheFig3Tables()
    {
        CataLooks looks = Tables().Looks(1)!;
        Assert.Equal(["Bri_m_Head01.dds", "Bri_m_Head02.dds"], looks.Faces);
        Assert.Equal(["eyecolor_1.dds", "eyecolor_2.dds"], looks.Eyes);
        Assert.Equal([Vector3.One, new Vector3(119, 60, 14) / 255f], looks.SkinTints);
        Assert.Equal(3, looks.HairColours.Count);                   // styles 1-3
        Assert.Equal([("Bri_m_Hair01_blonde.dds", Vector3.One), ("Bri_m_Hair01_blonde.dds", new Vector3(255, 243, 176) / 255f)],
            looks.HairColours[0]);
        Assert.Equal([("bri_m_hair02_blonde.dds", Vector3.One)], looks.HairColours[1]);
        Assert.Null(Tables().Looks(99));
    }

    [Fact]
    public void LabelledPartsNameTheirMeshesForTheClient()
    {
        NifMesh Shape(string n) => new() { Name = n, ShapeName = n, Vertices = [Vector3.Zero], Triangles = [(0, 0, 0)] };
        var head = new NifModel { Version = default, Meshes = [Shape("Bri_m_head01")] };
        var hair = new NifModel { Version = default, Meshes = [Shape("Editable Mesh"), Shape("Editable Mesh")] };
        NifModel figure = CatacombsFigure.Assemble(Skeleton, [(head, "face.dds", "head"), (hair, "hair.dds", "hair2")]);
        Assert.Equal(["head", "hair2", "hair2_1"], figure.Meshes.Select(m => m.ShapeName));
    }

    [Fact]
    public void EachSliderTakesTheHeadsMorphPairForItsFeature()
    {
        // bri_m_head01's codes in file order: eyes, nose, lips, jaw, then four moods.
        int[] briton = [3, 4, 1, 2, 5, 6, 7, 8, 21, 22, 23, 24];
        Assert.Equal(
            [new CataSlider("Nose", 2, 3), new CataSlider("Eyes", 0, 1), new CataSlider("Lips", 4, 5), new CataSlider("Jaw", 6, 7)],
            CatacombsFigure.Sliders(["Nose", "Eyes", "Lips", "Jaw"], briton));
        // dwa_m_head01: nose width (11/12) before ears (9/10), as its slider list has them.
        int[] dwarf = [3, 4, 1, 2, 11, 12, 9, 10, 21, 22, 23, 24];
        Assert.Equal([4, 6], CatacombsFigure.Sliders(["Nose Length", "Eyes", "Nose Width", "Ears"], dwarf).Skip(2).Select(x => x.Low));
        // elf_m_head01 says "Jaw" but carries lips (5/6): the head's unused pair stands in.
        int[] elf = [3, 4, 1, 2, 5, 6, 9, 10, 21, 22, 23, 24];
        Assert.Equal(new CataSlider("Jaw", 4, 5), CatacombsFigure.Sliders(["Nose", "Eyes", "Jaw", "Ears"], elf)[2]);
        // Unlabelled head: no slider reaches a target.
        Assert.All(CatacombsFigure.Sliders(["Nose", "Eyes"], new int[12]), sl => Assert.Equal((-1, -1), (sl.Low, sl.High)));
    }

    public static IEnumerable<object[]> BritonHead()
    {
        if (File.Exists(Live("bri_m_head01-1010.nif")))
            yield return [Live("bri_m_head01-1010.nif")];
    }

    [Theory]
    [MemberData(nameof(BritonHead))]
    public void AHeadsMorphTargetsAndCodesReachTheGltf(string path)
    {
        NifModel head = NifFile.Read(File.ReadAllBytes(path));
        NifMesh mesh = Assert.Single(head.Meshes);
        Assert.Equal([3, 4, 1, 2, 5, 6, 7, 8, 21, 22, 23, 24], mesh.MorphCodes!);
        Assert.Equal(12, mesh.MorphTargets!.Length);
        Assert.All(mesh.MorphTargets, t => Assert.Equal(mesh.Vertices.Length, t.Length));
        Assert.All(mesh.MorphTargets, t => Assert.Contains(t, d => d.Length() > 0.01f));   // every target moves something
        Assert.All(mesh.MorphTargets, t => Assert.All(t, d => Assert.True(d.Length() < 5f))); // offsets, not positions

        var skeleton = new NifJoint[] { new() { Name = "Bip01", Parent = -1, Local = Matrix4x4.Identity } };
        NifModel figure = CatacombsFigure.Assemble(skeleton, [(head, "face.dds", "head")]);
        var gltf = GltfExporter.BuildScene(figure).ToGltf2();
        Assert.Equal(12, gltf.LogicalMeshes.Single().Primitives.Single().MorphTargetsCount);
    }

    [Fact]
    public void AModelWithoutACatacombsVersionResolvesToNull()
    {
        Assert.Null(Tables().ResolveCatacombs(33));
        Assert.Null(Tables().ResolveCatacombs(999));
    }

    [Theory]
    [InlineData("CthBody01_ss_bri_M.dds", 1)]
    [InlineData("chnBody05_x.dds", 5)]
    [InlineData("cthboot03_ss.dds", 3)]
    [InlineData("cthGloves02_SS_Dwa.dds", 2)]
    [InlineData("Bri_m_Head01.dds", 1)]
    public void AClothingSkinNamesTheMeshVariantItIsDrawnOn(string skin, int variant) =>
        Assert.Equal(variant, CatacombsFigure.VariantOf(skin));

    // Scene Root, Bip01 at the origin, Bip01 Head 10 units up.
    private static readonly NifJoint[] Skeleton =
    [
        new() { Name = "Scene Root", Parent = -1, Local = Matrix4x4.Identity },
        new() { Name = "Bip01", Parent = 0, Local = Matrix4x4.Identity },
        new() { Name = "Bip01 Head", Parent = 1, Local = Matrix4x4.CreateTranslation(0, 0, 10) },
    ];

    [Fact]
    public void ANamedBonePartBindsToTheSkeletonsJointsByNameWhateverTheirCase()
    {
        var part = new NifModel
        {
            Version = default,
            Meshes =
            [
                new NifMesh
                {
                    Name = "hair", Vertices = [new(0, 0, 10), new(1, 0, 0)], Triangles = [(0, 1, 0)],
                    SkinBoneNames = ["BIP01 HEAD", "No Such Bone"], Skin = [[(0, 1f)], [(1, 1f)]],
                },
            ],
        };
        NifModel figure = CatacombsFigure.Assemble(Skeleton, [(part, "hair.dds")]);
        NifMesh m = Assert.Single(figure.Meshes);
        Assert.Equal("hair.dds", m.TextureFile);
        Assert.Equal(2, m.SkinJoints![m.Skin![0][0].Bone].Joint);    // Bip01 Head
        Assert.Equal(1, m.SkinJoints[m.Skin[1][0].Bone].Joint);      // an unknown bone: follows Bip01
        Assert.Equal(Matrix4x4.CreateTranslation(0, 0, -10), m.SkinJoints[m.Skin[0][0].Bone].InverseBind);
        Assert.Equal(part.Meshes[0].Vertices, figure.Posed(m));      // the rest pose is the part as stored
    }

    [Fact]
    public void ASelfContainedPartIsPosedInItsOwnFileThenBoundByName()
    {
        // Stored head-local at the origin; its own copy of the head bone puts it 10 up.
        var part = new NifModel
        {
            Version = default,
            Joints = Skeleton,
            Meshes =
            [
                new NifMesh
                {
                    Name = "hair", Vertices = [new(0, 0, 0), new(0, 1, 0)], Triangles = [(0, 1, 0)],
                    SkinJoints = [(2, Matrix4x4.Identity)], Skin = [[(0, 1f)], [(0, 1f)]],
                },
            ],
        };
        NifMesh m = Assert.Single(CatacombsFigure.Assemble(Skeleton, [(part, null)]).Meshes);
        Assert.Equal([new Vector3(0, 0, 10), new Vector3(0, 1, 10)], m.Vertices);
        Assert.All(m.Skin!, w => Assert.Equal(2, m.SkinJoints![w[0].Bone].Joint));
    }

    [Fact]
    public void ACatacombsClipKeepsTranslationsOnTheRootOnly()
    {
        var clip = new KfaClip(1f,
        [
            new KfaTrack("Bip01", [(0f, Quaternion.Identity)], [(0f, Vector3.One)], []),
            new KfaTrack("Bip01 Pelvis", [(0f, Quaternion.Identity)], [(0f, Vector3.One)], []),
        ]);
        KfaClip kept = CatacombsFigure.RotationsOnly(clip);
        Assert.Single(kept.Tracks[0].Translations);
        Assert.Empty(kept.Tracks[1].Translations);
        Assert.Single(kept.Tracks[1].Rotations);
    }

    // ----- live: real Catacombs files from the owner's install -----

    private static string Live(string name) => Path.Combine(AppContext.BaseDirectory, "live-nif", name);

    public static IEnumerable<object[]> BritonSkeleton()
    {
        if (File.Exists(Live("bri_m_skeleton-1010.nif")) && File.Exists(Live("body01_bc_m-1010.nif"))
            && File.Exists(Live("dwa_f_hair01-1010.nif")))
            yield return [Live("bri_m_skeleton-1010.nif")];
    }

    [Theory]
    [MemberData(nameof(BritonSkeleton))]
    public void ASkeletonFileReadsAsJoints(string path)
    {
        IReadOnlyList<NifJoint> joints = NifFile.ReadSkeleton(File.ReadAllBytes(path))!;
        Assert.Equal(129, joints.Count);
        Assert.Contains(joints, j => j.Name == "Bip01 Head");
        Assert.All(joints.Skip(1), j => Assert.InRange(j.Parent, 0, joints.Count - 1));
    }

    [Theory]
    [MemberData(nameof(BritonSkeleton))]
    public void AHairWhoseOneBonePointsAtItsOwnRootIsANamedBonePart(string _)
    {
        // dwa_f_Hair01: its single bone pointer reaches the file's "Scene Root"; the bone list names
        // the real bone. Read as self-contained it was posed at the feet.
        NifMesh hair = Assert.Single(NifFile.Read(File.ReadAllBytes(Live("dwa_f_hair01-1010.nif"))).Meshes);
        Assert.Null(hair.SkinJoints);
        Assert.NotNull(hair.SkinBoneNames);
        Assert.DoesNotContain("Scene Root", hair.SkinBoneNames!);
        Assert.All(hair.Vertices, v => Assert.InRange(v.Z, 55f, 85f));   // head height, not the feet
    }

    [Theory]
    [MemberData(nameof(BritonSkeleton))]
    public void ABodyPartBindsToEveryBoneItNamesAndRestsAsStored(string path)
    {
        IReadOnlyList<NifJoint> skeleton = NifFile.ReadSkeleton(File.ReadAllBytes(path))!;
        NifModel body = NifFile.Read(File.ReadAllBytes(Live("body01_bc_m-1010.nif")));
        NifMesh part = Assert.Single(body.Meshes);
        var names = skeleton.Select(j => j.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(part.SkinBoneNames!, n => Assert.Contains(n.Trim(), names));
        NifModel figure = CatacombsFigure.Assemble(skeleton, [(body, "skin.dds")]);
        NifMesh m = Assert.Single(figure.Meshes);
        Vector3[] posed = figure.Posed(m);
        for (int v = 0; v < posed.Length; v++)
            Assert.True(Vector3.Distance(posed[v], part.Vertices[v]) < 1e-3f, $"vertex {v}: {posed[v]} vs {part.Vertices[v]}");
    }

    public static IEnumerable<object[]> CatacombsClip()
    {
        if (File.Exists(Live("cat_i_hm-1010.kfa")))
            yield return [Live("cat_i_hm-1010.kfa")];
    }

    [Theory]
    [MemberData(nameof(CatacombsClip))]
    public void ACatacombsClipReadsAs10_1(string path)
    {
        // animnode.dat stand-in: bone i is named "b<i>".
        string[] names = Enumerable.Range(0, 200).Select(i => $"b{i}").ToArray();
        KfaClip clip = KfaClip.Read(File.ReadAllBytes(path), names);
        Assert.True(clip.Duration > 0);
        Assert.True(clip.Tracks.Count > 20);
        Assert.Contains(clip.Tracks, t => t.Bone == "b0");                // Bip01
        Assert.All(clip.Tracks, t => Assert.Matches("^b\\d+$", t.Bone));
        Assert.Contains(clip.Tracks, t => t.Rotations.Length > 1);
    }
}
