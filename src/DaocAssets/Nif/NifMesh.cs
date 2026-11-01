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

    /// <summary>Transparency from the shape's (or an ancestor's) <c>NiAlphaProperty</c>.</summary>
    public SurfaceAlpha Alpha { get; init; }

    /// <summary>Alpha below which a <see cref="SurfaceAlpha.Cutout"/> surface is discarded (0..1).</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>Draw both faces: alpha surfaces (crossed-plane foliage) and <c>NiStencilProperty</c>
    /// draw mode 3.</summary>
    public bool DoubleSided { get; init; }

    public override string ToString() =>
        $"{Name}: {Vertices.Length} verts, {Triangles.Length} tris" +
        (TextureFile is null ? "" : $", tex {TextureFile}") +
        (BaseColor is { } c ? $", rgba({c.X:0.##},{c.Y:0.##},{c.Z:0.##},{c.W:0.##})" : "") +
        (Alpha == SurfaceAlpha.Opaque ? "" : $", {Alpha.ToString().ToLowerInvariant()} {AlphaCutoff:0.###}") +
        (DoubleSided ? ", two-sided" : "") + (Helper ? ", helper" : "");
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
}
