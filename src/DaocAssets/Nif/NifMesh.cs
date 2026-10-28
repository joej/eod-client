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

    public override string ToString() =>
        $"{Name}: {Vertices.Length} verts, {Triangles.Length} tris" +
        (TextureFile is null ? "" : $", tex {TextureFile}") +
        (BaseColor is { } c ? $", rgba({c.X:0.##},{c.Y:0.##},{c.Z:0.##},{c.W:0.##})" : "");
}

/// <summary>A whole NIF file reduced to its static meshes.</summary>
public sealed class NifModel
{
    public required NifVersion Version { get; init; }
    public required IReadOnlyList<NifMesh> Meshes { get; init; }
}
