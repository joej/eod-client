// SPDX-License-Identifier: GPL-3.0-or-later
//
// NifFile: the particle systems of a model, from either reader's blocks (NifParticles). A particle shape takes its texture
// and blend from its own properties or, failing those, its nearest parent's (properties apply to a whole subtree), and is
// skipped when it or a parent is hidden.

using System.Numerics;

namespace DaocAssets.Nif;

public static partial class NifFile
{
    // 4.x: every block's world transform from the root, its parent, and the shapes found.
    private static List<NifParticleSystem> Particles4x(Block[] blocks)
    {
        if (!blocks.Any(b => NifParticleReader.IsParticleShape(b.Type)))
            return [];
        var world = new Dictionary<int, Matrix4x4>();
        var parent = new Dictionary<int, int>();
        var stack = new Stack<(int, Matrix4x4, int)>();
        stack.Push((0, Matrix4x4.Identity, -1));
        while (stack.Count > 0)
        {
            (int i, Matrix4x4 up, int from) = stack.Pop();
            if (i < 0 || i >= blocks.Length || world.ContainsKey(i))
                continue;
            Matrix4x4 w = ToMatrix(blocks[i].Local) * up;
            world[i] = w;
            parent[i] = from;
            foreach (int c in blocks[i].Children)
                stack.Push((c, w, i));
        }

        (string? Texture, ushort Alpha) Look(int i)
        {
            string? texture = null;
            ushort alpha = 0;
            for (int n = i, guard = 0; n >= 0 && guard++ < 256; n = parent.GetValueOrDefault(n, -1))
                foreach (int p in blocks[n].Props)
                {
                    if (p < 0 || p >= blocks.Length) continue;
                    Block prop = blocks[p];
                    if (texture is null && prop.Type == "NiTexturingProperty" && prop.BaseTextureRef >= 0 && prop.BaseTextureRef < blocks.Length)
                        texture = blocks[prop.BaseTextureRef].TextureFile ?? blocks[prop.BaseTextureRef].Embedded;
                    if (alpha == 0 && prop.Type == "NiAlphaProperty")
                        alpha = (ushort)(prop.AlphaFlags | 0x8000);   // marked present, even with blending off
                }
            return (texture, (ushort)(alpha & 0x7FFF));
        }

        bool Hidden(int i)
        {
            for (int n = i, guard = 0; n >= 0 && guard++ < 256; n = parent.GetValueOrDefault(n, -1))
                if (IsHidden(blocks[n].Flags))
                    return true;
            return false;
        }

        var shapes = new List<NifParticleReader.Shape>();
        for (int i = 0; i < blocks.Length; i++)
            if (NifParticleReader.IsParticleShape(blocks[i].Type) && world.TryGetValue(i, out Matrix4x4 w))
            {
                (string? texture, ushort alpha) = Look(i);
                shapes.Add(new NifParticleReader.Shape(blocks[i].Name, w, blocks[i].Controller, Hidden(i), texture, alpha));
            }
        return NifParticleReader.Assemble(shapes,
            i => i >= 0 && i < blocks.Length ? blocks[i].Particle : null,
            i => i >= 0 && i < blocks.Length ? blocks[i].NextRef : -1,
            i => world.TryGetValue(i, out Matrix4x4 m) ? m : null);
    }

    // 10.1: the same from the decoded tree.
    private static List<NifParticleSystem> ParticlesGamebryo(Gb10File g)
    {
        Gb10Block[] blocks = g.Blocks!;
        if (!blocks.Any(b => b is not null && NifParticleReader.IsParticleShape(b.Type)))
            return [];
        Dictionary<int, Matrix4x4> world = WorldTransforms(g);
        var parent = new Dictionary<int, int>();
        for (int i = 0; i < blocks.Length; i++)
            if (blocks[i] is not null)
                foreach (int c in blocks[i].Children)
                    if (c >= 0 && c < blocks.Length)
                        parent.TryAdd(c, i);

        (string? Texture, ushort Alpha) Look(int i)
        {
            string? texture = null;
            ushort alpha = 0;
            for (int n = i, guard = 0; n >= 0 && guard++ < 256; n = parent.GetValueOrDefault(n, -1))
                foreach (int p in blocks[n].Props)
                {
                    if (p < 0 || p >= blocks.Length || blocks[p] is null) continue;
                    Gb10Block prop = blocks[p];
                    if (texture is null && prop.Type == "NiTexturingProperty" && prop.BaseSourceRef >= 0 && prop.BaseSourceRef < blocks.Length)
                        texture = blocks[prop.BaseSourceRef]?.TextureFile;
                    if (alpha == 0 && prop.Type == "NiAlphaProperty")
                        alpha = (ushort)(prop.AlphaFlags | 0x8000);
                }
            return (texture, (ushort)(alpha & 0x7FFF));
        }

        bool Hidden(int i)
        {
            for (int n = i, guard = 0; n >= 0 && guard++ < 256; n = parent.GetValueOrDefault(n, -1))
                if ((blocks[n].Flags & 1) != 0)
                    return true;
            return false;
        }

        var shapes = new List<NifParticleReader.Shape>();
        for (int i = 0; i < blocks.Length; i++)
            if (blocks[i] is { } b && NifParticleReader.IsParticleShape(b.Type) && world.TryGetValue(i, out Matrix4x4 w))
            {
                (string? texture, ushort alpha) = Look(i);
                shapes.Add(new NifParticleReader.Shape(b.Name, w, b.Controller, Hidden(i), texture, alpha));
            }
        return NifParticleReader.Assemble(shapes,
            i => i >= 0 && i < blocks.Length ? blocks[i]?.Particle : null,
            i => i >= 0 && i < blocks.Length && blocks[i] is { } b ? b.NextController : -1,
            i => world.TryGetValue(i, out Matrix4x4 m) ? m : null);
    }
}
