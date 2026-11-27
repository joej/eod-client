// SPDX-License-Identifier: GPL-3.0-or-later
//
// NifFile: each mesh's material animations (NifMaterialAnims), from either reader's blocks. A mesh "mesh<i>" is block i;
// its material and texturing properties are its own or its nearest parent's (properties apply to a subtree), and its
// visibility is the visibility controller on it or the nearest node above it.

using System.Globalization;

namespace DaocAssets.Nif;

public static partial class NifFile
{
    private static int MeshBlock(NifMesh m) =>
        m.Name.StartsWith("mesh", StringComparison.Ordinal) && int.TryParse(m.Name.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : -1;

    // The tracks of every mesh, given how to walk a block's parents, properties and controllers.
    private static List<NifMaterialAnim> MaterialAnims(IEnumerable<NifMesh> meshes, int count, Func<int, string> type,
        Func<int, int[]> props, Func<int, int> controller, Func<int, int> parent, Func<int, object?> raw, Func<int, int> next)
    {
        var tracks = new List<NifMaterialAnim>();
        foreach (NifMesh mesh in meshes)
        {
            int i = MeshBlock(mesh);
            if (i < 0 || i >= count || mesh.Helper)
                continue;
            int material = -1, texturing = -1, vis = -1;
            for (int n = i, guard = 0; n >= 0 && guard++ < 256; n = parent(n))
            {
                foreach (int p in props(n))
                {
                    if (p < 0 || p >= count) continue;
                    if (material < 0 && type(p) == "NiMaterialProperty") material = p;
                    if (texturing < 0 && type(p) == "NiTexturingProperty") texturing = p;
                }
                if (vis < 0)
                    vis = NifParticleReader.FindVisController(controller(n), raw, next);
            }
            var chains = new List<int> { controller(i) };
            if (material >= 0) chains.Add(controller(material));
            if (texturing >= 0) chains.Add(controller(texturing));
            tracks.AddRange(NifMaterialAnimReader.Tracks(mesh.Name, chains, vis, raw, next));
        }
        return tracks;
    }

    private static List<NifMaterialAnim> MaterialAnims4x(Block[] blocks, IReadOnlyList<NifMesh> meshes)
    {
        if (!blocks.Any(b => b.Particle is NifMaterialAnimReader.Controller or NifParticleReader.VisController))
            return [];
        var parent = new Dictionary<int, int>();
        for (int i = 0; i < blocks.Length; i++)
            foreach (int c in blocks[i].Children)
                if (c >= 0 && c < blocks.Length)
                    parent.TryAdd(c, i);
        return MaterialAnims(meshes, blocks.Length, i => blocks[i].Type, i => blocks[i].Props, i => blocks[i].Controller,
            i => parent.GetValueOrDefault(i, -1), i => i >= 0 && i < blocks.Length ? blocks[i].Particle : null,
            i => i >= 0 && i < blocks.Length ? blocks[i].NextRef : -1);
    }

    private static List<NifMaterialAnim> MaterialAnimsGamebryo(Gb10File g, IReadOnlyList<NifMesh> meshes)
    {
        Gb10Block[] blocks = g.Blocks!;
        if (!blocks.Any(b => b?.Particle is NifMaterialAnimReader.Controller or NifParticleReader.VisController))
            return [];
        var parent = new Dictionary<int, int>();
        for (int i = 0; i < blocks.Length; i++)
            if (blocks[i] is not null)
                foreach (int c in blocks[i].Children)
                    if (c >= 0 && c < blocks.Length)
                        parent.TryAdd(c, i);
        return MaterialAnims(meshes, blocks.Length, i => blocks[i]?.Type ?? "", i => blocks[i]?.Props ?? [],
            i => blocks[i]?.Controller ?? -1, i => parent.GetValueOrDefault(i, -1),
            i => i >= 0 && i < blocks.Length ? blocks[i]?.Particle : null,
            i => i >= 0 && i < blocks.Length && blocks[i] is { } b ? b.NextController : -1);
    }
}
