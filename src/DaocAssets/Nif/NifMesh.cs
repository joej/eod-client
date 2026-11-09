// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;

namespace DaocAssets.Nif;

/// <summary>Static geometry pulled from a NIF: one mesh per NiTriShape. Coordinates are in the
/// NIF's own space (Z-up); the glTF emitter applies the axis/scale conversion to Godot.</summary>
public sealed record NifMesh
{
    public required string Name { get; init; }
    public required Vector3[] Vertices { get; init; }
    public Vector3[]? Normals { get; init; }
    public Vector2[]? TexCoords { get; init; }
    /// <summary>Triangle vertex indices, three per face.</summary>
    public required (ushort A, ushort B, ushort C)[] Triangles { get; init; }
    /// <summary>Base texture file name referenced by the shape, if any (e.g. <c>foo.dds</c>).</summary>
    public string? TextureFile { get; init; }

    /// <summary>The shape's <c>NiMaterialProperty</c> diffuse colour and alpha (RGBA, 0..1), if it has
    /// one. Many keep shapes carry NO texture (no <c>NiTexturingProperty</c>) and are meant to render
    /// flat in this colour — see open question W3a. When <see cref="TextureFile"/> is null this is the
    /// surface colour; alongside a texture it tints it (white = no tint).</summary>
    public Vector4? BaseColor { get; init; }

    /// <summary>True when this shape sits under a collision/climb/shadow-caster helper node (by node
    /// name) that the retail client never draws as a visible surface. The exporter drops these — they
    /// are the placeholder-coloured proxies that otherwise z-fight the real textured walls.</summary>
    public bool Helper { get; init; }

    /// <summary>True when this shape sits under a collision node ("collidee…", "collider…", "collision…"): the
    /// player can't walk through it. Usually also <see cref="Helper"/> (never drawn); a textured one is both.</summary>
    public bool Collision { get; init; }

    /// <summary>Transparency from the shape's (or an ancestor's) <c>NiAlphaProperty</c>.</summary>
    public SurfaceAlpha Alpha { get; init; }

    /// <summary>Blended additively (its NiAlphaProperty blends onto ONE or DESTALPHA): glows and effects.</summary>
    public bool Additive { get; init; }

    /// <summary>Alpha below which a <see cref="SurfaceAlpha.Cutout"/> surface is discarded (0..1).</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>Draw both faces: alpha surfaces (crossed-plane foliage) and <c>NiStencilProperty</c>
    /// draw mode 3.</summary>
    public bool DoubleSided { get; init; }

    /// <summary>Skinned shapes: the skin's bones, as (joint index into <see cref="NifModel.Joints"/>,
    /// inverse bind matrix in NIF space, row-vector convention). Null for rigid shapes. A skinned
    /// shape's vertices are in its skin's bind space (not baked to world): the joints move them.</summary>
    public (int Joint, System.Numerics.Matrix4x4 InverseBind)[]? SkinJoints { get; init; }

    /// <summary>Skinned shapes: per vertex, up to four (index into <see cref="SkinJoints"/>, weight)
    /// influences, strongest first, weights summing to 1 (empty if no bone weights the vertex).</summary>
    public (int Bone, float Weight)[][]? Skin { get; init; }

    /// <summary>A Catacombs body part (skinned to a skeleton in another file): its bones by name, in the
    /// order <see cref="Skin"/> indexes them. Its vertices are baked to the part's world space, which is
    /// the skeleton's rest pose; <c>CatacombsFigure.Assemble</c> binds it to a skeleton's joints.</summary>
    public string[]? SkinBoneNames { get; init; }

    /// <summary>Morph targets (NiGeomMorpherController + NiMorphData) as per-vertex offsets from the base
    /// shape, in the same space as <see cref="Vertices"/>; target 0 (the base) is left out, so entry i is the
    /// file's morph i + 1. Null when the shape has none. A Catacombs head's face sliders and moods.</summary>
    public Vector3[][]? MorphTargets { get; init; }

    /// <summary>Each morph target's feature code from the file's label string ("FM1=3", one line per morph:
    /// FM = face, HM = hair under a helm), 0 when unlabelled; same order as <see cref="MorphTargets"/>.</summary>
    public int[]? MorphCodes { get; init; }

    /// <summary>Every UV set the shape's data carries (set 0 is <see cref="TexCoords"/>); the extra
    /// texture slots name which set they use.</summary>
    public Vector2[][]? UvSets { get; init; }
    /// <summary>Per-vertex RGBA (0..1), only when the shape's (or an ancestor's)
    /// <c>NiVertexColorProperty</c> says they give the ambient and diffuse colour (vertex mode 2):
    /// baked shading, e.g. darker leaves inside a tree's crown. Null when they're ignored or absent.</summary>
    public Vector4[]? VertexColors { get; init; }
    /// <summary>The dark map (a texture multiplied over the base, mostly baked lighting) and its UV set,
    /// from the shape's <c>NiTexturingProperty</c> dark slot.</summary>
    public string? DarkTextureFile { get; init; }
    public Vector2[]? DarkTexCoords { get; init; }
    /// <summary>The detail map (a finely repeating texture layered over the base) and its UV set.</summary>
    /// <summary>The glow map (NiTexturingProperty slot 4), on the base texture's coordinates: light added
    /// on top of the surface (an effect's bright line, the teleporter ring's plasma_ring02).</summary>
    public string? GlowTextureFile { get; init; }

    public string? DetailTextureFile { get; init; }
    public Vector2[]? DetailTexCoords { get; init; }
    /// <summary>A decal laid over the base through its own alpha, on its own UV set: a classic figure's
    /// face skin over the hair skin on the head parts (see MonsterFigure).</summary>
    public string? DecalTextureFile { get; init; }
    public Vector2[]? DecalTexCoords { get; init; }

    /// <summary>More UV sets for the exporter to keep, written after the layer ones (TEXCOORD_2 on, which Godot
    /// reads as CUSTOM0): a Catacombs cloak's emblem pattern and logo sets (its cloth is on set 1).</summary>
    public Vector2[][]? ExtraTexCoords { get; init; }

    /// <summary>The NIF shape (NiTriShape) name, e.g. a body-part variant in a classic figure.</summary>
    public string ShapeName { get; init; } = "";

    // " (set k)" when a layer's UVs are one of the shape's sets, for nifinfo.
    private string SetOf(Vector2[]? uv) => UvSets is { Length: > 1 } sets && uv is not null && Array.IndexOf(sets, uv) is >= 0 and var k
        ? $" (set {k})" : "";

    public override string ToString() =>
        $"{Name}: {Vertices.Length} verts, {Triangles.Length} tris" +
        (TextureFile is null ? "" : $", tex {TextureFile}") +
        (DarkTextureFile is null ? "" : $", dark {DarkTextureFile}{SetOf(DarkTexCoords)}") +
        (DetailTextureFile is null ? "" : $", detail {DetailTextureFile}{SetOf(DetailTexCoords)}") +
        (GlowTextureFile is null ? "" : $", glow {GlowTextureFile}") +
        (DecalTextureFile is null ? "" : $", decal {DecalTextureFile}{SetOf(DecalTexCoords)}") +
        (UvSets is { Length: > 1 } sets ? $", {sets.Length} UV sets" : "") +
        (VertexColors is null ? "" : ", vertex colours") +
        (BaseColor is { } c ? $", rgba({c.X:0.##},{c.Y:0.##},{c.Z:0.##},{c.W:0.##})" : "") +
        (Alpha == SurfaceAlpha.Opaque ? "" : $", {Alpha.ToString().ToLowerInvariant()} {AlphaCutoff:0.###}") +
        (DoubleSided ? ", two-sided" : "") + (Helper ? ", helper" : "") + (Skin is null ? "" : ", skinned") +
        (MorphTargets is null ? "" : $", {MorphTargets.Length} morphs (codes {string.Join(" ", MorphCodes ?? [])})");
}

/// <summary>One skeleton node of a skinned model, in NIF space (Z-up, NIF units).</summary>
public sealed record NifJoint
{
    public required string Name { get; init; }
    /// <summary>Index of the parent joint in <see cref="NifModel.Joints"/>, or -1 for a root.</summary>
    public required int Parent { get; init; }
    /// <summary>Local transform relative to the parent (row-vector convention: p' = p * M).</summary>
    public required System.Numerics.Matrix4x4 Local { get; init; }
}

/// <summary>How a surface uses its texture's alpha.</summary>
public enum SurfaceAlpha
{
    /// <summary>Alpha ignored.</summary>
    Opaque,
    /// <summary>Alpha-tested: pixels under <see cref="NifMesh.AlphaCutoff"/> are not drawn (foliage).</summary>
    Cutout,
    /// <summary>Alpha-blended (glows and similar effects).</summary>
    Blend,
}

/// <summary>A whole NIF file reduced to its static meshes.</summary>
public sealed class NifModel
{
    public required NifVersion Version { get; init; }
    public required IReadOnlyList<NifMesh> Meshes { get; init; }
    /// <summary>The skeleton, parents before children; empty for a static model.</summary>
    public IReadOnlyList<NifJoint> Joints { get; init; } = [];

    /// <summary>Every named node's world transform in the file's rest pose (NIF space, row vectors), by
    /// lower-case name (the first of a repeated name): an item's grip and sheath frames ("held", "belt",
    /// "back") for hanging it on a figure's bone.</summary>
    public IReadOnlyDictionary<string, System.Numerics.Matrix4x4> NodeWorlds { get; init; } =
        new Dictionary<string, System.Numerics.Matrix4x4>();

    /// <summary>Each joint's world transform in the file's rest pose (NIF space, row vectors).</summary>
    public System.Numerics.Matrix4x4[] JointWorlds()
    {
        var world = new System.Numerics.Matrix4x4[Joints.Count];
        for (int j = 0; j < world.Length; j++)
            world[j] = Joints[j].Parent < 0 ? Joints[j].Local : Joints[j].Local * world[Joints[j].Parent];
        return world;
    }

    /// <summary>A mesh's vertices placed by its skin in the rest pose (NIF space): what the glTF
    /// skin will draw before any animation. A rigid mesh's vertices are returned as they are.</summary>
    public System.Numerics.Vector3[] Posed(NifMesh mesh)
    {
        if (mesh.SkinJoints is null || mesh.Skin is null)
            return mesh.Vertices;
        System.Numerics.Matrix4x4[] world = JointWorlds();
        var posed = new System.Numerics.Vector3[mesh.Vertices.Length];
        for (int v = 0; v < posed.Length; v++)
            foreach ((int bone, float w) in mesh.Skin[v])
                posed[v] += System.Numerics.Vector3.Transform(mesh.Vertices[v],
                    mesh.SkinJoints[bone].InverseBind * world[mesh.SkinJoints[bone].Joint]) * w;
        return posed;
    }
}
