// SPDX-License-Identifier: GPL-3.0-or-later
//
// Patch files: a model "<name>.nif" may have "<name>_p00.nif", "_p01", … beside it (packed in "<name>_p00.npk" when the
// base is), 276 of them on 198 models in the owner's install: keep and frontier pieces, dungeon rooms, zone objects and
// the capital cities' pieces. The retail client probes _p00 to _p09 after loading a model, stops at the first number that
// is missing, and merges each in order (daoc-re, 2026-11-16; read statically from the client, so marked as such):
// - A patch with a node named "__patch_<target>" (the first, depth-first): its children go into the base's <target>
//   node ("a,b,c" = the first node named a, then its direct child b, then c). A child replaces the target's direct child
//   of the same name (case-insensitive) and keeps its own transform; otherwise it is added. Nothing else in the patch
//   file is used. A target the base doesn't have: the patch does nothing.
// - A patch without one: its "pickee", "collidee", "bounding" and "visible" subtrees merge by name into the base's,
//   the same way, except that a replacing child takes the transform of the node it replaces.
// The merged tree is then read with the usual node rules (collidee hidden and solid, …). Here the base's tree loses
// the replaced children; the target's world transform and hidden/solid state are worked out as the walk would reach
// it; and each donated child is walked by its own file's reader from there (a patch may be 10.1 on a 4.x base).
// A later patch may target a node an earlier one brought in (2-outpost_tower_p05 patches the door002 that _p00 adds).

using System.Numerics;

namespace DaocAssets.Nif;

/// <summary>What one patch file does to its base model (<see cref="NifFile.DescribePatches"/>).</summary>
/// <param name="Kind">"__patch_&lt;target&gt;", "subtrees" (pickee/collidee/bounding/visible), "nothing" or "unreadable".</param>
/// <param name="Targets">The base nodes it merges into; a missing one is marked "(missing)".</param>
public sealed record NifPatchEffect(string Kind, IReadOnlyList<string> Targets, IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Added, string? Error = null);

public static partial class NifFile
{
    private static readonly string[] StandardSubtrees = ["pickee", "collidee", "bounding", "visible"];

    /// <summary>Reads a model with its patch files (<c>_p00</c>, <c>_p01</c>, … in order) merged in, as the retail
    /// client does. A patch that doesn't parse is skipped.</summary>
    public static NifModel Read(ReadOnlySpan<byte> bytes, IReadOnlyList<byte[]> patches) =>
        patches.Count == 0 ? Read(bytes) : Merge(bytes.ToArray(), patches, out _);

    /// <summary>What each patch does to the base: for the converter's report and the patch audit.</summary>
    public static IReadOnlyList<NifPatchEffect> DescribePatches(byte[] bytes, IReadOnlyList<byte[]> patches)
    {
        Merge(bytes, patches, out List<NifPatchEffect> effects, build: false);
        return effects;
    }

    private static NifModel Merge(byte[] bytes, IReadOnlyList<byte[]> patches, out List<NifPatchEffect> effects, bool build = true)
    {
        Tree baseTree = Tree.Parse(bytes);
        effects = [];
        // The donated children by the node they go into (in the base, or in an earlier patch's donated subtree), and
        // every donor still in the model, in the order applied.
        var hosts = new Dictionary<(Tree Tree, int Node), List<Donor>>();
        var active = new List<Donor>();
        var detached = new List<int>();
        for (int n = 0; n < patches.Count; n++)
        {
            Tree patch;
            try { patch = Tree.Parse(patches[n]); }
            catch (NifFormatException e)
            {
                effects.Add(new NifPatchEffect("unreadable", [], [], [], e.Message));
                continue;
            }
            var targets = new List<string>();
            var replaced = new List<string>();
            var added = new List<string>();
            int marker = patch.FindFirst(name => name.StartsWith("__patch_", StringComparison.OrdinalIgnoreCase));
            var merges = new List<(string[] Path, int Node, bool TakeTransform)>();
            string kind;
            if (marker >= 0)
            {
                kind = patch.Name(marker);
                merges.Add((kind["__patch_".Length..].Split(',', StringSplitOptions.TrimEntries), marker, false));
            }
            else
            {
                foreach (string sub in StandardSubtrees)
                    if (patch.FindFirst(name => name.Equals(sub, StringComparison.OrdinalIgnoreCase)) is int s and >= 0)
                        merges.Add(([sub], s, true));
                kind = merges.Count > 0 ? "subtrees" : "nothing";
            }
            foreach ((string[] path, int node, bool take) in merges)
            {
                // The merged model's node: the base's first, then (inferred order) the earlier patches' donated subtrees.
                (Tree? host, int target) = baseTree.Resolve(path, null) is int b and >= 0 ? (baseTree, b) : (null, -1);
                foreach (Donor d in active)
                {
                    if (host is not null)
                        break;
                    if (d.Tree.Resolve(path, d.Node) is int x and >= 0)
                        (host, target) = (d.Tree, x);
                }
                targets.Add(string.Join(",", path) + (host is null ? " (missing)" : ""));
                if (host is null)
                    continue;
                List<Donor> donors = hosts.TryGetValue((host, target), out List<Donor>? list) ? list : hosts[(host, target)] = [];
                foreach (int child in patch.Children(node).TakeWhile(c => c >= 0 && c < patch.Count))
                {
                    string name = patch.Name(child);
                    Matrix4x4? local = null;
                    int[] kids = host.Children(target);
                    int k = Array.FindIndex(kids, c => c >= 0 && c < host.Count && host.Name(c).Equals(name, StringComparison.OrdinalIgnoreCase));
                    int earlier = donors.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (k >= 0)
                    {
                        local = host.Local(kids[k]);
                        if (host == baseTree)
                            detached.Add(kids[k]);
                        host.SetChildren(target, [.. kids[..k], .. kids[(k + 1)..]]);
                        replaced.Add(name);
                    }
                    else if (earlier >= 0)
                    {
                        local = donors[earlier].Tree.Local(donors[earlier].Node);
                        active.Remove(donors[earlier]);
                        donors.RemoveAt(earlier);
                        replaced.Add(name);
                    }
                    else
                        added.Add(name);
                    if (take && local is { } l)
                        patch.SetLocal(child, l);   // a subtree merge: the replacing child sits where the old one did
                    var donor = new Donor(patch, child, name, n);
                    donors.Add(donor);
                    active.Add(donor);
                }
            }
            effects.Add(new NifPatchEffect(kind, targets, replaced, added));
        }
        if (!build)
            return null!;

        NifModel model = baseTree.Build(detached);
        var meshes = new List<NifMesh>(model.Meshes);
        // Where each host node is, as the walk reaches it: the base's nodes, then each donated subtree's from its host.
        var states = new Dictionary<(Tree, int), State>();
        foreach ((int i, State st) in baseTree.States(null, default))
            states[(baseTree, i)] = st;
        var placed = new HashSet<Donor>();
        for (bool progress = true; progress;)
        {
            progress = false;
            foreach (((Tree tree, int node), List<Donor> donors) in hosts)
            {
                if (!states.TryGetValue((tree, node), out State at))
                    continue;   // not reached: under an LOD level the walk leaves out, or a replaced subtree
                foreach (Donor donor in donors.Where(placed.Add))
                {
                    progress = true;
                    foreach ((int i, State st) in donor.Tree.States(donor.Node, at))
                        states[(donor.Tree, i)] = st;
                    meshes.AddRange(donor.Tree.WalkFrom(donor.Node, at)
                        .Select(m => m with { Name = $"p{donor.Patch:00}_{m.Name}" }));
                }
            }
        }
        if (meshes.Count == 0)
            throw new NifFormatException("No geometry in the model or its patches.");
        return new NifModel { Version = model.Version, Meshes = meshes, Joints = model.Joints, NodeWorlds = model.NodeWorlds };
    }

    private sealed record Donor(Tree Tree, int Node, string Name, int Patch);

    // A node's world transform and what it inherits from its ancestors, as the walk reaches it.
    private readonly record struct State(Matrix4x4 World, bool Helper, bool Numbered, bool Collision, bool KeepsMaterial = false);

    // A parsed file of either format, seen as a node tree.
    private sealed class Tree
    {
        private readonly byte[] _bytes;
        private readonly NifVersion _version;
        private readonly Block[]? _b4;
        private readonly Gb10File? _g;

        private Tree(byte[] bytes, NifVersion version, Block[]? b4, Gb10File? g) => (_bytes, _version, _b4, _g) = (bytes, version, b4, g);

        public static Tree Parse(byte[] bytes)
        {
            var c = new NifCursor(bytes);
            string header = c.LineString();
            if (!header.Contains("File Format", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(header, @"Version [23]\."))
                throw new NifFormatException($"Not a supported NIF file (header: '{header}').");
            uint packed = c.U32();
            var version = new NifVersion((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
            if (version >= V1020 || version <= V4000)
                throw new NifFormatException($"NIF version {version} is not supported.");
            if (version >= V1010)
            {
                Gb10File g = Gamebryo1010.Parse(bytes);
                if (!g.Complete)
                    throw new NifFormatException($"10.1 NIF stops at {g.StopType ?? "an undecoded block"}.");
                return new Tree(bytes, version, null, g);
            }
            return new Tree(bytes, version, Parse4x(bytes, version, checked((int)c.U32())), null);
        }

        public int Count => _b4?.Length ?? _g!.Blocks.Length;
        private int[] Roots => _b4 is not null ? [0] : _g!.Roots.Length > 0 ? _g.Roots : [0];
        public string Name(int i) => (_b4 is not null ? _b4[i].Name : _g!.Blocks[i]!.Name) ?? "";
        private string Type(int i) => _b4 is not null ? _b4[i].Type : _g!.Blocks[i]!.Type;
        private ushort Flags(int i) => _b4 is not null ? _b4[i].Flags : _g!.Blocks[i]!.Flags;
        public int[] Children(int i) => _b4 is not null ? _b4[i].Children : _g!.Blocks[i]!.Children;

        public void SetChildren(int i, int[] children)
        {
            if (_b4 is not null) _b4[i].Children = children;
            else _g!.Blocks[i]!.Children = children;
        }

        public Matrix4x4 Local(int i)
        {
            if (_g is not null)
                return ToMatrix(_g.Blocks[i]!.Local);
            Xform x = _b4![i].Local;
            return ToMatrix(new NifTransform(x.Rot, x.Trans, x.Scale));
        }

        public void SetLocal(int i, Matrix4x4 m)
        {
            NifTransform t = FromMatrix(m);
            if (_g is not null) _g.Blocks[i]!.Local = t;
            else _b4![i].Local = new Xform(t.Rot, t.Scale, t.Trans);
        }

        private bool IsNode(int i) => Type(i) is "NiNode" or "NiLODNode" or "NiBillboardNode";

        // The first node (depth-first, children in order, from the roots or `root`) whose name matches.
        public int FindFirst(Func<string, bool> match, int? root = null)
        {
            var seen = new HashSet<int>();
            int Visit(int i)
            {
                if (i < 0 || i >= Count || _g?.Blocks[i] is null && _g is not null || !seen.Add(i))
                    return -1;
                if (match(Name(i)))
                    return i;
                if (IsNode(i))
                    foreach (int c in Children(i))
                        if (Visit(c) is int hit and >= 0)
                            return hit;
                return -1;
            }
            foreach (int r in root is int one ? [one] : Roots)
                if (Visit(r) is int hit and >= 0)
                    return hit;
            return -1;
        }

        // "a,b,c": the first node named a, then its direct child b, then c's.
        public int Resolve(string[] path, int? root)
        {
            int at = FindFirst(n => n.Equals(path[0], StringComparison.OrdinalIgnoreCase), root);
            foreach (string step in path.Skip(1))
            {
                if (at < 0)
                    return -1;
                int parent = at;
                at = Array.FindIndex(Children(parent), c => c >= 0 && c < Count && Name(c).Equals(step, StringComparison.OrdinalIgnoreCase)) is int k and >= 0
                    ? Children(parent)[k] : -1;
            }
            return at;
        }

        // Each node the walk reaches from the roots, or from `root` under a parent at `parent` (LOD levels it leaves out
        // excluded), with its world transform and inherited state.
        public Dictionary<int, State> States(int? root, State parent)
        {
            var states = new Dictionary<int, State>();
            void Visit(int i, State above, string aboveName)
            {
                if (i < 0 || i >= Count || states.ContainsKey(i) || !IsNode(i))
                    return;
                string name = Name(i);
                var s = new State(Local(i) * above.World, above.Helper || IsHelperNodeName(name) || IsHidden(Flags(i)) || IsDungeonHelper(aboveName, name),
                    above.Numbered || IsNumberedHelperName(name), above.Collision || IsCollisionName(name),
                    above.KeepsMaterial || KeepsMaterialName(name));
                states[i] = s;
                int[] children = Type(i) != "NiLODNode" ? Children(i)
                    : _b4 is not null ? SelectLodChildren(_b4, _b4[i]) : new Gb10Builder(_g!.Blocks!).NearLevel(_g.Blocks[i]!);
                foreach (int c in children)
                    Visit(c, s, name);
            }
            State top = root is null ? new State(Matrix4x4.Identity, false, false, false) : parent;
            foreach (int r in root is int one ? [one] : Roots)
                Visit(r, top, "");
            return states;
        }

        public NifModel Build(IEnumerable<int> detached) =>
            _b4 is not null ? Build4x(_bytes, _version, _b4, detached: detached) : BuildGamebryo(_g!, _version);

        // The meshes of one node's subtree, walked from a parent at `at` (the node it was donated to).
        public List<NifMesh> WalkFrom(int node, State at)
        {
            if (_g is not null)
            {
                var b = new Gb10Builder(_g.Blocks!);
                b.Walk(node, -1, at.World, at.Helper, -1, -1, at.Numbered, -1, at.Collision, at.KeepsMaterial);
                return b.Meshes;
            }
            var meshes = new List<NifMesh>();
            NifTransform w = FromMatrix(at.World);
            Walk4x(_bytes, _b4!, node, new Xform(w.Rot, w.Scale, w.Trans), [], meshes, at.Helper,
                numberedHelper: at.Numbered, collision: at.Collision, keepsMaterial: at.KeepsMaterial);
            return meshes;
        }
    }

    // The inverse of ToMatrix, for a rotation with uniform scale.
    private static NifTransform FromMatrix(Matrix4x4 m)
    {
        float s = new Vector3(m.M11, m.M12, m.M13).Length();
        if (s == 0)
            s = 1;
        float[] r =
        [
            m.M11 / s, m.M21 / s, m.M31 / s,
            m.M12 / s, m.M22 / s, m.M32 / s,
            m.M13 / s, m.M23 / s, m.M33 / s,
        ];
        return new NifTransform(r, new Vector3(m.M41, m.M42, m.M43), s);
    }
}
