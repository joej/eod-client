// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;

namespace DaocAssets.Nif;

/// <summary>
/// Parses a NIF file into its static meshes. Entry point: <see cref="Read(ReadOnlySpan{byte})"/>.
/// Implements the NetImmerse 4.2.x path (items); Gamebryo 10.1.0.0 (creatures) is handled
/// separately once decoded. Unknown block types are skipped by walking to the next recognised
/// block, so a file we don't fully model still yields the geometry we do understand.
/// </summary>
public static partial class NifFile
{
    private static readonly NifVersion V4000 = new(4, 0, 0, 2);
    private static readonly NifVersion V1010 = new(10, 1, 0, 0);
    private static readonly NifVersion V1020 = new(10, 2, 0, 0);
    private static readonly NifVersion V41012 = new(4, 1, 0, 12);   // TexDesc has Unknown1 up to here

    public static NifModel Read(ReadOnlySpan<byte> bytes)
    {
        var c = new NifCursor(bytes);
        string header = c.LineString();
        if (!header.Contains("File Format", StringComparison.Ordinal))
            throw new NifFormatException($"Not a NIF file (header: '{header}').");
        // NetImmerse 2.x/3.x have copyright lines after this one and no binary version field; reading
        // one as a version gave a garbage "newer than supported" error (open question W6).
        if (System.Text.RegularExpressions.Regex.IsMatch(header, @"Version [23]\."))
            throw new NifFormatException($"NetImmerse 2.x/3.x NIF is not supported ({header}).");

        // Version u32 follows the header line in both layouts. The layout after it differs:
        //  - 4.2.x: num-blocks u32, then each block is an inline sized type-name string + body.
        //  - 10.1.0.0 (Gamebryo): user-version u32, num-blocks u32, a block-type table (count +
        //    sized strings) and a per-block type index; block bodies carry NO inline type string.
        uint packed = c.U32();
        var version = new NifVersion((byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        if (version >= V1020)
            throw new NifFormatException($"NIF version {version} is newer than supported (4.2.x and 10.1.0.0 only).");
        if (version >= V1010)
            return ReadGamebryoTree(bytes, version) ?? ReadGamebryo1010(bytes, c, version);
        if (version <= V4000)
            throw new NifFormatException($"NIF version {version} is older than supported (4.1.0.1+).");

        int blockCount = checked((int)c.U32());
        return ReadNetImmerse4x(bytes, version, blockCount);
    }

    // A rigid transform (rotation 3x3 + uniform scale + translation), applied to a point.
    private readonly struct Xform(float[] rot, float scale, Vector3 trans)
    {
        public static readonly Xform Identity = new([1, 0, 0, 0, 1, 0, 0, 0, 1], 1f, Vector3.Zero);
        public readonly float[] Rot = rot;
        public readonly float Scale = scale;
        public readonly Vector3 Trans = trans;

        public Vector3 Apply(Vector3 v)
        {
            float x = (Rot[0] * v.X + Rot[1] * v.Y + Rot[2] * v.Z) * Scale;
            float y = (Rot[3] * v.X + Rot[4] * v.Y + Rot[5] * v.Z) * Scale;
            float z = (Rot[6] * v.X + Rot[7] * v.Y + Rot[8] * v.Z) * Scale;
            return new Vector3(x + Trans.X, y + Trans.Y, z + Trans.Z);
        }

        // Rotate a direction (normals): rotation only, no translation, no scale.
        public Vector3 ApplyDir(Vector3 v) => new(
            Rot[0] * v.X + Rot[1] * v.Y + Rot[2] * v.Z,
            Rot[3] * v.X + Rot[4] * v.Y + Rot[5] * v.Z,
            Rot[6] * v.X + Rot[7] * v.Y + Rot[8] * v.Z);

        // world = parent ∘ local.
        public Xform Compose(Xform local)
        {
            float[] r = new float[9];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    r[row * 3 + col] =
                        Rot[row * 3 + 0] * local.Rot[0 * 3 + col] +
                        Rot[row * 3 + 1] * local.Rot[1 * 3 + col] +
                        Rot[row * 3 + 2] * local.Rot[2 * 3 + col];
            return new Xform(r, Scale * local.Scale, Apply(local.Trans));
        }
    }

    // A parsed NiNode or NiTriShape: its local transform and either its children (node) or its
    // geometry-data block index (shape). Other block types are recorded by type only.
    private sealed class Block
    {
        public required string Type;
        public required int Offset;
        public string Name = "";      // NiObjectNET.Name — used to drop collidee/climb/shadow helpers
        public ushort Flags;          // NiAVObject.Flags; bit 0 = Hidden (app-culled, drop subtree)
        public Xform Local = Xform.Identity;
        public int[] Children = [];
        public float[] LodNear = [];  // NiLODNode -> each level's near extent (same order as Children)
        public int DataRef = -1;      // NiTriShape -> its NiTriShapeData/NiTriStripsData block
        public int[] Props = [];      // NiTriShape -> its property refs (one is a NiTexturingProperty)
        public int BaseTextureRef = -1; // NiTexturingProperty -> its base NiSourceTexture block
        public int DarkTextureRef = -1, DarkUvSet;     // NiTexturingProperty dark slot (baked shading)
        public int DetailTextureRef = -1, DetailUvSet; // NiTexturingProperty detail slot
        public int VertexMode = -1;   // NiVertexColorProperty: 0 ignore, 1 emissive, 2 ambient + diffuse
        public string? TextureFile;   // for NiSourceTexture blocks
        public Vector4? MaterialColor; // NiMaterialProperty -> diffuse RGB + alpha (0..1)
        public ushort AlphaFlags;     // NiAlphaProperty -> flags (bit 0 blend, bits 5-8 dest, bit 9 test)
        public byte AlphaThreshold;   // NiAlphaProperty -> alpha-test reference value (0..255)
        public uint StencilDrawMode;  // NiStencilProperty -> face draw mode (3 = both sides)
    }

    // NetImmerse 4.x. Blocks reference each other by index and carry transforms in a NiNode/NiTriShape
    // hierarchy, so a multi-part model (a keep, a tree) only assembles correctly once each shape's
    // accumulated world transform is baked into its vertices. We index every block by scanning for
    // its type string (bodies aren't self-sized), parse the NiNode/NiTriShape transforms and refs,
    // walk from the scene root composing transforms, and bake them into the geometry each shape
    // points at. Verified against a 57-node/118-shape keep.
    private static NifModel ReadNetImmerse4x(ReadOnlySpan<byte> bytes, NifVersion version, int blockCount)
    {
        Block[] blocks = IndexBlocks4x(bytes, blockCount);

        // Parse the blocks we model (nodes, shapes, source textures). Geometry-data blocks are read
        // later, only for shapes that reference them.
        foreach (Block b in blocks)
        {
            var c = new NifCursor(bytes);
            c.SeekTo(b.Offset);
            try { c.SizedString(); } catch (NifFormatException) { continue; } // skip its type string
            switch (b.Type)
            {
                case "NiNode":
                case "NiLODNode":
                case "NiBillboardNode":   // a NiNode whose layout differs only after the children
                                          // (billboard mode); until 2026-11-03 unread, so the far-level
                                          // picture cards under it lay flat on the ground (BSpanMoss)
                    if (TryReadAvObject(ref c, out Xform nx, out int[] nprops, out string nname, out ushort nflags))
                    {
                        b.Name = nname;
                        b.Flags = nflags;
                        b.Local = nx;
                        b.Props = nprops;   // a node's properties apply to its whole subtree
                        int nch = (int)c.U32();
                        var ch = new int[Math.Clamp(nch, 0, 100000)];
                        for (int k = 0; k < ch.Length; k++) ch[k] = c.I32();
                        b.Children = ch;
                        if (b.Type == "NiLODNode")
                            b.LodNear = ReadLodRanges(ref c);
                    }
                    break;
                case "NiTriShape":
                case "NiTriStrips":
                    if (TryReadAvObject(ref c, out Xform sx, out int[] props, out string sname, out ushort sflags))
                    {
                        b.Name = sname;
                        b.Flags = sflags;
                        b.Local = sx;
                        b.Props = props;
                        b.DataRef = c.I32();   // NiGeometry: Data ref (the geometry-data block)
                    }
                    break;
                case "NiTexturingProperty":
                    ReadTexturingProperty(ref c, b, version);
                    break;
                case "NiVertexColorProperty":
                    ReadVertexColorProperty(ref c, b);
                    break;
                case "NiSourceTexture":
                    b.TextureFile = ReadSourceTextureFileName(ref c, bytes);
                    break;
                case "NiMaterialProperty":
                    {
                        // Its name: on shapes whose texture is embedded, the texture's file name.
                        var nc = c;
                        try { b.Name = nc.SizedString(); } catch (NifFormatException) { }
                        b.MaterialColor = ReadMaterialPropertyColor(ref c);
                    }
                    break;
                case "NiAlphaProperty":
                    ReadAlphaProperty(ref c, b);
                    break;
                case "NiStencilProperty":
                    ReadStencilProperty(ref c, b);
                    break;
            }
        }

        // Walk from the scene root (block 0) composing transforms; bake each shape's world transform
        // into the geometry it references.
        var meshes = new List<NifMesh>();
        var seen = new HashSet<int>();
        Walk4x(bytes, blocks, 0, Xform.Identity, seen, meshes);

        // Fallback: bake any shape that is not in the root's hierarchy at all (a file whose root isn't
        // block 0, or a shape outside the hierarchy) at its own local transform, so we still get its
        // geometry. "In the hierarchy" counts every child, including the LOD levels the walk skips on
        // purpose: treating a skipped far level as unreached re-added it here without its parents'
        // transforms, which drew a second, shifted copy of Prydwen Keep's far model through the near
        // one (walls cutting through the inner keep, stairs into a wall).
        HashSet<int> inHierarchy = Descendants(blocks, 0);
        for (int i = 0; i < blocks.Length; i++)
        {
            Block b = blocks[i];
            if ((b.Type is "NiTriShape" or "NiTriStrips") && !seen.Contains(i) && !inHierarchy.Contains(i))
            {
                NifMesh? geom = ReadGeometryAt(bytes, blocks, b.DataRef);
                if (geom is not null)
                    meshes.Add(WithSurface(BakeTransform(geom, b.Local, $"mesh{i}",
                        ShapeTexture(blocks, b), ShapeMaterialColor(blocks, b)), blocks, b, -1, -1)
                        with { Helper = IsHelperNodeName(b.Name) || IsHidden(b.Flags) });
            }
        }

        if (meshes.Count == 0)
            throw new NifFormatException("No NiTriShapeData geometry found in NIF.");

        return new NifModel { Version = version, Meshes = meshes, NodeWorlds = NodeWorlds4x(blocks) };
    }

    // Every named node's world transform, walking every child from the root (LOD levels and helpers
    // included): row-vector matrices, p' = p * M, as the 10.1 path's ToMatrix.
    private static Dictionary<string, Matrix4x4> NodeWorlds4x(Block[] blocks)
    {
        var worlds = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var stack = new Stack<(int, Xform)>();
        var seen = new HashSet<int>();
        stack.Push((0, Xform.Identity));
        while (stack.Count > 0)
        {
            (int i, Xform parent) = stack.Pop();
            if (i < 0 || i >= blocks.Length || !seen.Add(i))
                continue;
            Block b = blocks[i];
            if (b.Type is not ("NiNode" or "NiLODNode" or "NiBillboardNode" or "NiTriShape" or "NiTriStrips"))
                continue;
            Xform w = parent.Compose(b.Local);
            if (!string.IsNullOrEmpty(b.Name))
                worlds.TryAdd(b.Name.Trim().ToLowerInvariant(), new Matrix4x4(
                    w.Rot[0] * w.Scale, w.Rot[3] * w.Scale, w.Rot[6] * w.Scale, 0,
                    w.Rot[1] * w.Scale, w.Rot[4] * w.Scale, w.Rot[7] * w.Scale, 0,
                    w.Rot[2] * w.Scale, w.Rot[5] * w.Scale, w.Rot[8] * w.Scale, 0,
                    w.Trans.X, w.Trans.Y, w.Trans.Z, 1));
            foreach (int c in b.Children)
                stack.Push((c, w));
        }
        return worlds;
    }

    // Every block reachable from `root` through node children, regardless of LOD selection or
    // helper/hidden rules.
    private static HashSet<int> Descendants(Block[] blocks, int root)
    {
        var found = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (i < 0 || i >= blocks.Length || !found.Add(i))
                continue;
            foreach (int c in blocks[i].Children)
                stack.Push(c);
        }
        return found;
    }

    // Recursively walks the node hierarchy, composing transforms and baking each reached shape's
    // geometry at its world transform. A static method (not a local function) because `bytes` is a
    // ref-like span that can't be captured in a closure.
    // `alpha`/`stencil` are the NiAlphaProperty/NiStencilProperty block indices inherited from the
    // ancestors (-1 = none): a property on a node applies to its subtree unless a descendant carries
    // its own of the same type.
    private static void Walk4x(ReadOnlySpan<byte> bytes, Block[] blocks, int index, Xform world,
        HashSet<int> seen, List<NifMesh> meshes, bool helper = false, int alpha = -1, int stencil = -1,
        bool numberedHelper = false, int vertexColor = -1)
    {
        if (index < 0 || index >= blocks.Length || !seen.Add(index))
            return;
        Block b = blocks[index];
        Xform w = world.Compose(b.Local);
        bool underHelper = helper || IsHelperNodeName(b.Name) || IsHidden(b.Flags);
        bool underNumbered = numberedHelper || IsNumberedHelperName(b.Name);
        if (b.Type is "NiNode" or "NiLODNode" or "NiBillboardNode")
        {
            // A billboard node is walked like a plain node: it keeps its stored orientation instead of
            // turning to the camera. In DAoC's trees it holds the far LOD level's picture card, which
            // the LOD selection then leaves out.
            // A NiLODNode holds alternative detail levels of the same object; the engine draws exactly
            // ONE child, by distance. We bake a single static mesh, so drawing every level stacks the
            // near and far geometry and they z-fight. Keep only the nearest level (SelectLodChildren:
            // by the node's distance ranges, by name when it has none).
            int[] children = b.Type == "NiLODNode" ? SelectLodChildren(blocks, b) : b.Children;
            int nodeAlpha = FindProperty(blocks, b.Props, "NiAlphaProperty", alpha);
            int nodeStencil = FindProperty(blocks, b.Props, "NiStencilProperty", stencil);
            int nodeVertexColor = FindProperty(blocks, b.Props, "NiVertexColorProperty", vertexColor);
            foreach (int child in children)
                Walk4x(bytes, blocks, child, w, seen, meshes, underHelper, nodeAlpha, nodeStencil, underNumbered,
                    nodeVertexColor);
        }
        else if (b.Type is "NiTriShape" or "NiTriStrips")
        {
            NifMesh? geom = ReadGeometryAt(bytes, blocks, b.DataRef);
            if (geom is not null)
            {
                string? texture = ShapeTexture(blocks, b);
                meshes.Add(WithSurface(BakeTransform(geom, w, $"mesh{index}",
                    texture, ShapeMaterialColor(blocks, b)), blocks, b, alpha, stencil, vertexColor)
                    with { Helper = underHelper || (underNumbered && texture is null) });
            }
        }
    }

    // DAoC NIFs carry helper geometry the retail client never draws as a visible surface: collision
    // hulls, pick/climb/ladder volumes and shadow-caster proxies. The client's visibility rule hides a
    // node and its whole subtree when its name is an EXACT, case-insensitive match for one of these — NOT a
    // prefix. So `collidee` is hidden but `collidee016`/`collideeH`/"coll tower low" are not (they hide
    // only under a `collidee` parent or via their own file Hidden flag); `climb`/`sclimb` are exact too,
    // and `climb001` is not hidden by its own name. Matching by prefix over-drops vs. retail — it was
    // what wrongly removed the keep's `collision tower`/`collision wall` tan stone. The separate Hidden
    // file flag (NiAVObject flags bit 0, Gamebryo app-culled) is handled in the walk, and is the more
    // likely source of the placeholder-coloured (purple/aqua) shapes.
    private static readonly string[] HiddenNodeNames =
        ["collidee", "pickee", "ladder", "climb", "sclimb", "shadowcaster"];

    // A helper word plus digits only ("climb001", "collidee01", "ladder2"): numbered collision and climb
    // volumes. The exact-name rule above doesn't hide them, and in retail a ladder shows its rungs, not
    // the flat green climb panel in front of them (owner, Llyn Barfog, 2026-11-03; open question W3f).
    // Untextured shapes under such a node are hidden; textured ones are kept, in case they are art.
    // Also footprints and colliders: a capital city's pieces carry "collisionswitch / footprints /
    // ext00 (footprint)" and "collider ..." nodes, untextured ground outlines and collision volumes
    // that drew as a lime-green and purple layer under the streets (owner, City of Camelot,
    // 2026-11-03: walking into a wall dropped him onto it). Textured shapes under such nodes stay:
    // Prydwen Keep's "collision tower" stone is real.
    internal static bool IsNumberedHelperName(string name) =>
        !string.IsNullOrEmpty(name) && (NumberedHelper.IsMatch(name)
            || name.Contains("footprint", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("collider", StringComparison.OrdinalIgnoreCase)
            // "collisionswitch" is not one: it holds a model's visible parts too (avpier's pier).
            || (name.StartsWith("collision", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("collisionswitch", StringComparison.OrdinalIgnoreCase)));

    private static readonly System.Text.RegularExpressions.Regex NumberedHelper = new(
        "^(collidee|pickee|ladder|climb|sclimb|shadowcaster)[0-9]+$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsHelperNodeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (string h in HiddenNodeNames)
            if (string.Equals(name, h, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // NiAVObject flags bit 0 = Hidden: Gamebryo app-culls the node and its subtree, so the client
    // never draws it. The likely source of the placeholder-coloured (purple/aqua) shapes that the
    // artists left in the file but flagged hidden. The name rule above sets this same bit.
    private static bool IsHidden(ushort flags) => (flags & 0x1) != 0;

    // Chooses which NiLODNode children to walk: the near / full-detail level only. The engine draws a
    // single level by camera distance; baking one static mesh means we pick the closest. DAoC names the
    // far level with "far" (BarnLODfar/keepFar/BTavernFar); the near level is "near"/"visible". Prefer a
    // child whose name marks it near; otherwise keep every child that is NOT a far level (so a model
    // without the naming convention is unchanged); if that leaves nothing, keep the first child.
    // NiLODNode after the children (4.x, as NifSurvey decodes it): effects list, NiSwitchNode's u32,
    // LOD Center, then each level's near/far extent. Empty if it doesn't read cleanly.
    private static float[] ReadLodRanges(ref NifCursor c)
    {
        try
        {
            uint effects = c.U32();
            if (effects > 1000) return [];
            for (int k = 0; k < effects; k++) c.I32();
            c.I32();
            c.Vec3();
            uint levels = c.U32();
            if (levels > 64) return [];
            var near = new float[levels];
            for (int l = 0; l < levels; l++) { near[l] = c.F32(); c.F32(); }
            return near;
        }
        catch (NifFormatException)
        {
            return [];
        }
    }

    // An LOD node draws one child at a time; we keep the nearest. With LOD ranges (every 4.x LOD node
    // in the install has them) that is the child with the smallest near extent. Until 2026-11-03 we
    // went by name only, and children named neither near nor far (HBirchsingle, HOldGrove: the tree
    // and a flat far-distance card picture of it) were all kept, drawing the card over the tree.
    private static int[] SelectLodChildren(Block[] blocks, Block lod)
    {
        int[] children = lod.Children;
        if (lod.LodNear.Length == children.Length && children.Length > 1)
        {
            int best = 0;
            for (int i = 1; i < children.Length; i++)
                if (lod.LodNear[i] < lod.LodNear[best]) best = i;
            return [children[best]];
        }
        return SelectLodChildrenByName(blocks, children);
    }

    private static int[] SelectLodChildrenByName(Block[] blocks, int[] children)
    {
        static bool IsFar(string n) => n.Contains("far", StringComparison.OrdinalIgnoreCase);
        static bool IsNear(string n) =>
            n.Contains("near", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("visible", StringComparison.OrdinalIgnoreCase);
        static string NameOf(Block[] bs, int i) => i >= 0 && i < bs.Length ? bs[i].Name : "";

        var near = children.Where(i => IsNear(NameOf(blocks, i))).ToArray();
        if (near.Length > 0)
            return near;
        var notFar = children.Where(i => !IsFar(NameOf(blocks, i))).ToArray();
        if (notFar.Length > 0)
            return notFar;
        return children.Length > 0 ? [children[0]] : children;
    }

    // Resolves a shape's own base texture: its property list holds a NiTexturingProperty whose base
    // TexDesc points at a NiSourceTexture. This is what makes a multi-material model (a keep uses ten
    // skins across its parts) wear the right texture on each shape, instead of one shared skin.
    // Returns null when the shape has no NiTexturingProperty — such shapes are NOT textured; they are
    // flat surfaces coloured by their NiMaterialProperty (open question W3a). We deliberately do NOT
    // fall back to the model's first texture: that mislabels every untextured shape with an unrelated
    // skin and, with no UVs, samples it at texel (0,0) — the "flat tan towers" bug. Verified across
    // keep.NIF + 34 other NIFs: a shape has either its own texturing property or none, so no shape
    // relies on an ancestor's property that this per-shape lookup would miss.
    private static string? ShapeTexture(Block[] blocks, Block shape)
    {
        foreach (int p in shape.Props)
        {
            if (p < 0 || p >= blocks.Length || blocks[p].Type != "NiTexturingProperty")
                continue;
            int src = blocks[p].BaseTextureRef;
            if (src >= 0 && src < blocks.Length && blocks[src].TextureFile is { } name)
                return name;
            // An embedded texture (pixels in the file, no name; the capital cities' pieces): the
            // shape's material is named after the texture file, which the install also ships
            // (cam36's "bpalwall.tga" as bpalwall.dds). Owner, City of Camelot, 2026-11-03.
            if (src >= 0 && src < blocks.Length && blocks[src].Type == "NiSourceTexture")
                foreach (int m in shape.Props)
                    if (m >= 0 && m < blocks.Length && blocks[m].Type == "NiMaterialProperty"
                        && System.IO.Path.GetExtension(blocks[m].Name).ToLowerInvariant() is ".tga" or ".dds" or ".bmp")
                        return blocks[m].Name;
        }
        return null;
    }

    // Resolves a shape's flat surface colour from its NiMaterialProperty (diffuse RGB + alpha). Used
    // on untextured shapes (no NiTexturingProperty) so they render in the right colour instead of a
    // mis-sampled texture; on a textured shape it tints the texture (DAoC's keep skins are white, so
    // no visible tint). Null when the shape names no material property.
    private static Vector4? ShapeMaterialColor(Block[] blocks, Block shape)
    {
        foreach (int p in shape.Props)
        {
            if (p >= 0 && p < blocks.Length && blocks[p].Type == "NiMaterialProperty"
                && blocks[p].MaterialColor is { } color)
                return color;
        }
        return null;
    }

    // The first property of `type` in `props`, else `inherited` (-1 = none).
    private static int FindProperty(Block[] blocks, int[] props, string type, int inherited)
    {
        foreach (int p in props)
            if (p >= 0 && p < blocks.Length && blocks[p].Type == type)
                return p;
        return inherited;
    }

    // Attaches the shape's transparency and sidedness, from its own NiAlphaProperty/NiStencilProperty
    // or the nearest ancestor's.
    // Also attaches the shape's dark and detail maps (its own NiTexturingProperty) and its vertex
    // colours, kept only when the shape's or an ancestor's NiVertexColorProperty uses them (vertex
    // mode 2: they give the ambient and diffuse colour).
    private static NifMesh WithSurface(NifMesh mesh, Block[] blocks, Block shape, int inheritedAlpha,
        int inheritedStencil, int inheritedVertexColor = -1)
    {
        int a = FindProperty(blocks, shape.Props, "NiAlphaProperty", inheritedAlpha);
        int st = FindProperty(blocks, shape.Props, "NiStencilProperty", inheritedStencil);
        (SurfaceAlpha mode, float cutoff) = a < 0
            ? (SurfaceAlpha.Opaque, 0.5f)
            : AlphaMode(blocks[a].AlphaFlags, blocks[a].AlphaThreshold);
        bool bothSides = mode != SurfaceAlpha.Opaque || (st >= 0 && blocks[st].StencilDrawMode == 3);

        int vc = FindProperty(blocks, shape.Props, "NiVertexColorProperty", inheritedVertexColor);
        Vector4[]? colors = vc >= 0 && blocks[vc].VertexMode == 2 ? mesh.VertexColors : null;

        int tp = FindProperty(blocks, shape.Props, "NiTexturingProperty", -1);
        (string? dark, Vector2[]? darkUv) = tp < 0 ? (null, null) : Slot(blocks, mesh, blocks[tp].DarkTextureRef, blocks[tp].DarkUvSet);
        (string? detail, Vector2[]? detailUv) = tp < 0 ? (null, null) : Slot(blocks, mesh, blocks[tp].DetailTextureRef, blocks[tp].DetailUvSet);
        return mesh with
        {
            Alpha = mode, AlphaCutoff = cutoff, DoubleSided = bothSides, VertexColors = colors,
            DarkTextureFile = dark, DarkTexCoords = darkUv, DetailTextureFile = detail, DetailTexCoords = detailUv,
        };
    }

    // A texture slot's file name and the UV set it uses, if both exist.
    private static (string?, Vector2[]?) Slot(Block[] blocks, NifMesh mesh, int sourceRef, int uvSet)
    {
        if (sourceRef < 0 || sourceRef >= blocks.Length || blocks[sourceRef].TextureFile is not { } file)
            return (null, null);
        if (mesh.UvSets is not { } sets || uvSet < 0 || uvSet >= sets.Length)
            return (null, null);
        return (file, sets[uvSet]);
    }

    /// <summary>
    /// How a NiAlphaProperty draws, as a glTF alpha mode and cut-off. Flags (nif.xml): bit 0 alpha
    /// blend, bits 5-8 destination blend factor, bit 9 alpha test, threshold = the test's reference
    /// (0..255). The rules follow OfflineDAoC's `alpha_mode` (rambodolphin13/OfflineDAoC,
    /// `pipeline/nif_static.py`, GPL-3.0): an alpha test is a cut-out at its threshold (DAoC's trees:
    /// flags 0x1221, test GREATER 75 → 0.29); a plain blend reads best as a cut-out at 0.5 for foliage
    /// and decals; a blend onto ONE/DESTALPHA (additive glow) or INVSRCCOLOR (screen) stays blended,
    /// the nearest glTF can express.
    /// </summary>
    public static (SurfaceAlpha Mode, float Cutoff) AlphaMode(ushort flags, byte threshold)
    {
        bool blend = (flags & 0x1) != 0;
        int dest = (flags >> 5) & 0xF;
        if (blend && dest is 0 or 8 or 3)
            return (SurfaceAlpha.Blend, 0.5f);
        if ((flags & 0x200) != 0)
            return (SurfaceAlpha.Cutout, Math.Max(threshold / 255f, 0.05f));
        if (blend)
            return (SurfaceAlpha.Cutout, 0.5f);
        return (SurfaceAlpha.Opaque, 0.5f);
    }

    // Bakes a world transform into a mesh's vertices and normals, attaching the texture name and the
    // shape's material colour.
    private static NifMesh BakeTransform(NifMesh mesh, Xform w, string name, string? texture,
        Vector4? materialColor)
    {
        var verts = new Vector3[mesh.Vertices.Length];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = w.Apply(mesh.Vertices[i]);
        Vector3[]? normals = null;
        if (mesh.Normals is not null)
        {
            normals = new Vector3[mesh.Normals.Length];
            for (int i = 0; i < normals.Length; i++)
                normals[i] = w.ApplyDir(mesh.Normals[i]);
        }
        return mesh with
        {
            Name = name,
            Vertices = verts,
            Normals = normals,
            TextureFile = mesh.TextureFile ?? texture,
            BaseColor = mesh.BaseColor ?? materialColor,
        };
    }

    // Reads the geometry-data block at `dataRef` (NiTriShapeData or NiTriStripsData), or null.
    private static NifMesh? ReadGeometryAt(ReadOnlySpan<byte> bytes, Block[] blocks, int dataRef)
    {
        if (dataRef < 0 || dataRef >= blocks.Length)
            return null;
        Block d = blocks[dataRef];
        var c = new NifCursor(bytes);
        c.SeekTo(d.Offset);
        try
        {
            c.SizedString(); // skip the type string
            return d.Type switch
            {
                "NiTriShapeData" => ReadTriShapeData(ref c, default, "geom"),
                "NiTriStripsData" => ReadTriStripsData4x(ref c, "geom"),
                _ => null,
            };
        }
        catch (NifFormatException)
        {
            return null;
        }
    }

    /// <summary>The next sized "Ni…" type string at or after <paramref name="from"/>, or -1.</summary>
    internal static int NextTypeStringAt(ReadOnlySpan<byte> bytes, int from) => NextTypeString(bytes, from);

    /// <summary>The "Ni…" type string at <paramref name="q"/>, or null.</summary>
    internal static string? TypeStringAtOffset(ReadOnlySpan<byte> bytes, int q) => TypeStringAt(bytes, q);

    /// <summary>The 4.x block index as (type, offset of its type string), for <see cref="NifSurvey"/>.</summary>
    internal static (string Type, int Offset)[] IndexBlocks(ReadOnlySpan<byte> bytes, int blockCount) =>
        IndexBlocks4x(bytes, blockCount).Select(b => (b.Type, b.Offset)).ToArray();

    // Builds the block index by scanning for each block's type string: bodies aren't self-sized, so
    // a block ends where the next "Ni*" sized type string begins. Returns blocks[index] = (type,
    // offset-of-type-string). Verified to recover all blocks on real files.
    private static Block[] IndexBlocks4x(ReadOnlySpan<byte> bytes, int blockCount)
    {
        var list = new List<Block>(Math.Min(blockCount, 4096));
        int q = FindFirstTypeString(bytes);
        while (list.Count < blockCount && q >= 0 && q < bytes.Length)
        {
            string? type = TypeStringAt(bytes, q);
            if (type is null)
                break;
            list.Add(new Block { Type = type, Offset = q });
            int after = q + 4 + type.Length;
            q = NextTypeString(bytes, after);
        }
        return [.. list];
    }

    private static int FindFirstTypeString(ReadOnlySpan<byte> bytes)
    {
        // After the header line + version u32 + block-count u32, the first block's type string begins.
        int nl = bytes.IndexOf((byte)0x0A);
        if (nl < 0 || nl + 1 + 8 > bytes.Length)
            return -1;
        return nl + 1 + 8;
    }

    private static int NextTypeString(ReadOnlySpan<byte> bytes, int from)
    {
        for (int q = from; q + 6 <= bytes.Length; q++)
            if (TypeStringAt(bytes, q) is not null)
                return q;
        return -1;
    }

    // A valid block type string at q: u32 length in [3,40], then that many printable bytes starting "Ni".
    private static string? TypeStringAt(ReadOnlySpan<byte> bytes, int q)
    {
        if (q + 6 > bytes.Length)
            return null;
        uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(q, 4));
        if (len is < 3 or > 40 || q + 4 + (int)len > bytes.Length)
            return null;
        if (bytes[q + 4] != (byte)'N' || bytes[q + 5] != (byte)'i')
            return null;
        // A class name is a plain identifier. Text inside a block body can also start with "Ni" — 3ds
        // Max's exporter writes "NiMAXPSysConverter flag" into string extra data, texture names like
        // "Nicicle.tga" occur — and accepting those split the real block, used up the header's block
        // count and dropped the file's last real block (496 files in the coverage audit).
        ReadOnlySpan<byte> s = bytes.Slice(q + 4, (int)len);
        foreach (byte ch in s)
            if (!(char.IsAsciiLetterOrDigit((char)ch) || ch == (byte)'_'))
                return null;
        return System.Text.Encoding.Latin1.GetString(s);
    }

    // NiObjectNET + NiAVObject prefix shared by NiNode and NiTriShape at 4.x. Reads through the
    // transform and leaves the cursor at the first subclass field (children for NiNode, Data for
    // NiTriShape). Returns false if a bounding volume is present (we don't decode it) — the caller
    // then leaves the block's transform at identity rather than misparse.
    private static bool TryReadAvObject(ref NifCursor c, out Xform xform) =>
        TryReadAvObject(ref c, out xform, out _, out _, out _);

    private static bool TryReadAvObject(ref NifCursor c, out Xform xform, out string name, out ushort flags) =>
        TryReadAvObject(ref c, out xform, out _, out name, out flags);

    // Reads NiObjectNET + NiAVObject, yielding the local transform, the block's property refs
    // (one of which, on a shape, is the NiTexturingProperty that names its base texture), the block's
    // Name (a node name like "collidee"/"shadowcaster" marks helper geometry) and its NiAVObject
    // Flags (bit 0 = Hidden / app-culled; the client drops a hidden node and its whole subtree).
    private static bool TryReadAvObject(ref NifCursor c, out Xform xform, out int[] props, out string name, out ushort flags)
    {
        xform = Xform.Identity;
        props = [];
        flags = 0;
        name = c.SizedString();    // NiObjectNET.Name
        c.I32();                   // Extra Data ref (single, until 4.2.2.0)
        c.I32();                   // Controller ref
        flags = c.U16();           // NiAVObject.Flags (ushort at <= 4.2.2.0); bit 0 = Hidden
        Vector3 trans = c.Vec3();
        float[] rot = new float[9];
        for (int i = 0; i < 9; i++) rot[i] = c.F32();
        float scale = c.F32();
        c.Vec3();                  // Velocity (until 4.2.2.0)
        int numProps = (int)c.U32();
        int[] p = new int[Math.Clamp(numProps, 0, 100000)];
        for (int i = 0; i < p.Length; i++) p[i] = c.I32();   // Property refs
        if (c.Bool())              // Has Bounding Volume (until 4.2.2.0)
            return false;          // present only rarely; bail to identity rather than misparse
        (rot, scale, trans) = Sanitize(rot, scale, trans);
        xform = new Xform(rot, scale, trans);
        props = p;
        return true;
    }

    /// <summary>
    /// A node transform with NaN in it becomes "no transform" for the broken part: translation 0,
    /// rotation identity, scale 1. Some files carry such nodes (zones/Nifs Ncarcass.nif: every visible
    /// mesh under it came out NaN, and the glTF writer silently dropped all of it, leaving an empty
    /// model); OfflineDAoC reads NaN as 0 the same way (nif_character.py, node_trs).
    /// </summary>
    internal static (float[] Rot, float Scale, Vector3 Trans) Sanitize(float[] rot, float scale, Vector3 trans)
    {
        if (rot.Any(float.IsNaN)) rot = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        if (float.IsNaN(scale) || scale == 0) scale = 1;
        trans = new Vector3(float.IsNaN(trans.X) ? 0 : trans.X, float.IsNaN(trans.Y) ? 0 : trans.Y, float.IsNaN(trans.Z) ? 0 : trans.Z);
        return (rot, scale, trans);
    }

    // NiTexturingProperty body (4.x), positioned just past the type string. Layout (verified by
    // decode against a real multi-material building, cross-checked with nif.xml): NiObjectNET
    // (Name + Extra Data ref + Controller ref), Flags u16, Apply Mode u32, Texture Count u32,
    // then Has Base Texture (1-byte bool); if set, a base TexDesc whose first field is the Source
    // ref (i32 -> a NiSourceTexture). We take only that base source ref (the shape's main skin).
    // NiTexturingProperty body (4.x, as NifSurvey decodes it): NiObjectNET, Flags u16, Apply Mode u32,
    // Texture Count u32, then the slots in order (base, dark, detail, ...), each a bool and, if set, a
    // TexDesc: Source ref, Clamp u32, Filter u32, UV Set u32, PS2 L/K u16, and Unknown1 u16 up to
    // 4.1.0.12. Base, dark and detail are kept; reading stops after detail.
    private static void ReadTexturingProperty(ref NifCursor c, Block b, NifVersion version)
    {
        try
        {
            c.SizedString();   // Name
            c.I32(); c.I32();  // Extra Data ref, Controller ref
            c.U16();           // Flags
            c.U32();           // Apply Mode
            c.U32();           // Texture Count
            for (int slot = 0; slot < 3; slot++)
            {
                if (!c.Bool())
                    continue;
                int source = c.I32();
                c.U32(); c.U32();                       // Clamp, Filter
                int uvSet = (int)c.U32();
                c.U16(); c.U16();                       // PS2 L, PS2 K
                if (version <= V41012) c.U16();         // Unknown1
                if (slot == 0) b.BaseTextureRef = source;
                else if (slot == 1) (b.DarkTextureRef, b.DarkUvSet) = (source, uvSet);
                else (b.DetailTextureRef, b.DetailUvSet) = (source, uvSet);
            }
        }
        catch (NifFormatException)
        {
            // Keep whatever slots were read; a truncated property still names its base texture.
        }
    }

    // NiVertexColorProperty body (4.x): NiObjectNET, Flags u16, Vertex Mode u32, Lighting Mode u32.
    private static void ReadVertexColorProperty(ref NifCursor c, Block b)
    {
        try
        {
            c.SizedString();
            c.I32(); c.I32();
            c.U16();
            b.VertexMode = (int)c.U32();
        }
        catch (NifFormatException) { b.VertexMode = -1; }
    }

    // NiAlphaProperty body (4.x, nif.xml): NiObjectNET (Name, Extra Data ref, Controller ref), Flags
    // u16, Threshold u8. Checked on elm1/bpinea/bwillow (4.2.1.0) and oak1 (4.1.0.12): 0x1221 / 75.
    private static void ReadAlphaProperty(ref NifCursor c, Block b)
    {
        try
        {
            c.SizedString();
            c.I32(); c.I32();
            b.AlphaFlags = c.U16();
            b.AlphaThreshold = c.U8();
        }
        catch (NifFormatException) { b.AlphaFlags = 0; }
    }

    // NiStencilProperty body (4.x, nif.xml for <= 10.0.1.2): NiObjectNET, Flags u16, Stencil Enabled
    // u8, then Function, Ref, Mask, Fail, Z Fail, Pass and Draw Mode, all u32. Draw Mode 3 = both
    // faces; DAoC's crossed-plane trees use it.
    private static void ReadStencilProperty(ref NifCursor c, Block b)
    {
        try
        {
            c.SizedString();
            c.I32(); c.I32();
            c.U16();
            c.U8();
            for (int i = 0; i < 6; i++) c.U32();
            b.StencilDrawMode = c.U32();
        }
        catch (NifFormatException) { b.StencilDrawMode = 0; }
    }

    // NiMaterialProperty body (4.x), positioned just past the type string. Layout (verified by byte
    // dump against keep.NIF and 34 other building/item NIFs, matches nif.xml): NiObjectNET (Name +
    // Extra Data ref + Controller ref), Flags u16, then four Color3s — Ambient, Diffuse, Specular,
    // Emissive — then Glossiness f32 and Alpha f32. We keep the Diffuse colour and Alpha; a shape
    // with no NiTexturingProperty renders flat in this colour (open question W3a). Returns null on a
    // short/garbled read rather than throwing, so one odd block doesn't fail the whole file.
    private static Vector4? ReadMaterialPropertyColor(ref NifCursor c)
    {
        try
        {
            c.SizedString();            // Name
            c.I32(); c.I32();           // Extra Data ref, Controller ref
            c.U16();                    // Flags
            c.Vec3();                   // Ambient (discarded)
            Vector3 diffuse = c.Vec3(); // Diffuse
            c.Vec3();                   // Specular (discarded)
            c.Vec3();                   // Emissive (discarded)
            c.F32();                    // Glossiness
            float alpha = c.F32();      // Alpha
            return new Vector4(diffuse, alpha);
        }
        catch (NifFormatException)
        {
            return null;
        }
    }

    // Gamebryo 10.1.0.0 (creatures). The header after the Version u32 is: User Version u32,
    // Num Blocks u32, Num Block Types u16, [SizedString x Num Block Types], Block Type Index
    // (u16 x Num Blocks). Block bodies carry no inline type string — the type comes from the index
    // table — and the intermediate blocks (NiNode, NiKeyframeController/Data, NiSkin*) have large
    // version-gated layouts we don't need. Since the goal is geometry, we read the header to bound
    // and validate the file, then locate each NiTriShapeData body by a strict, self-validating scan
    // (every field consistency-checked), mirroring the 4.2.x approach of finding geometry without
    // modelling every intermediate block. Verified byte-for-byte against a real creature NIF.
    private static NifModel ReadGamebryo1010(ReadOnlySpan<byte> bytes, NifCursor c, NifVersion version)
    {
        c.U32();                                  // User Version
        int blockCount = checked((int)c.U32());   // Num Blocks
        int numTypes = c.U16();                    // Num Block Types
        if (blockCount is < 1 or > 100000 || numTypes is < 1 or > 10000)
            throw new NifFormatException($"Implausible 10.1.0.0 header (blocks {blockCount}, types {numTypes}).");

        var typeNames = new string[numTypes];
        for (int t = 0; t < numTypes; t++)
            typeNames[t] = c.SizedString();
        bool hasShape = false, hasStrips = false;
        foreach (string t in typeNames)
        {
            hasShape |= t == "NiTriShapeData";
            hasStrips |= t == "NiTriStripsData";
        }
        // Block Type Index (u16 per block) follows; skipping it lands the cursor at the first body.
        for (int b = 0; b < blockCount; b++)
            c.U16();
        int bodiesStart = c.Pos;

        if (!hasShape && !hasStrips)
            throw new NifFormatException("No NiTriShapeData/NiTriStripsData type in 10.1.0.0 NIF (no geometry we read).");

        // Scan for geometry bodies. A file holds one kind (shape or strips); try whichever the type
        // table says is present, each a strict self-validating parse so a wrong offset is rejected.
        var meshes = new List<NifMesh>();
        int scan = bodiesStart;
        while (scan < bytes.Length)
        {
            if (hasShape && TryReadTriShapeData1010(bytes, scan, $"mesh{meshes.Count}", out NifMesh mesh, out int next))
            {
                meshes.Add(mesh);
                scan = next;
            }
            else if (hasStrips && TryReadTriStripsData1010(bytes, scan, $"mesh{meshes.Count}", out NifMesh strip, out int nextS))
            {
                meshes.Add(strip);
                scan = nextS;
            }
            else
            {
                scan++;
            }
        }

        if (meshes.Count == 0)
            throw new NifFormatException("No NiTriShapeData/NiTriStripsData geometry decoded in 10.1.0.0 NIF.");

        string? textureFile = FindFirstTextureName(bytes);
        if (textureFile is not null)
            for (int m = 0; m < meshes.Count; m++)
                meshes[m] = meshes[m] with { TextureFile = meshes[m].TextureFile ?? textureFile };

        return new NifModel { Version = version, Meshes = meshes };
    }

    // The geometry data shared by NiTriShapeData and NiTriStripsData at 10.1.0.0 (the NiGeometryData
    // -> NiTriBasedGeomData part), read up to and including Num Triangles. Field order (gates
    // resolved for 10.1.0.0, verified against nif.xml):
    //   Num Vertices u16; Keep Flags u8; Compress Flags u8; Has Vertices bool(1) + Vertices;
    //   Data Flags u16 (low 6 bits = UV-set count, bit 4096 = tangents present); Has Normals bool(1)
    //   + Normals [+ Tangents + Bitangents if bit 4096]; Bounding Sphere (centre+radius);
    //   Has Vertex Colors bool(1) + Color4s; UV sets; Consistency Flags u16; Num Triangles u16.
    // Returns false on any inconsistency so it can drive a self-validating scan for the block.
    private static bool TryReadGeometryCommon1010(
        ref NifCursor c, out int numVertices, out Vector3[] vertices,
        out Vector3[]? normals, out Vector2[]? texCoords, out int numTriangles)
    {
        numVertices = 0; vertices = []; normals = null; texCoords = null; numTriangles = 0;

        int nv = c.U16();
        if (nv is < 1 or > 65535)
            return false;
        c.U8();                      // Keep Flags
        c.U8();                      // Compress Flags

        if (!TryBool(ref c, out bool hasVerts) || !hasVerts) // geometry we use always has vertices
            return false;
        vertices = ReadVec3Array(ref c, nv);

        int dataFlags = c.U16();
        int uvSets = dataFlags & 0x3F;
        bool hasTangents = (dataFlags & 0x1000) != 0;

        if (!TryBool(ref c, out bool hasNormals)) return false;
        normals = hasNormals ? ReadVec3Array(ref c, nv) : null;
        if (hasNormals && hasTangents)
            for (int k = 0; k < nv; k++) { c.Vec3(); c.Vec3(); } // tangents + bitangents

        c.Vec3(); c.F32();           // Bounding Sphere

        if (!TryBool(ref c, out bool hasColors)) return false;
        if (hasColors)
            for (int k = 0; k < nv * 4; k++) c.F32();

        for (int s = 0; s < uvSets; s++)
        {
            var set = new Vector2[nv];
            for (int v = 0; v < nv; v++)
                set[v] = new Vector2(c.F32(), c.F32());
            texCoords ??= set;
        }

        c.U16();                     // Consistency Flags
        numTriangles = c.U16();
        numVertices = nv;
        return true;
    }

    // NiTriShapeData body at 10.1.0.0 (self-validating). After the shared geometry:
    //   Num Triangle Points u32 (=3x); Has Triangles bool(1) + Triangles; Num Match Groups u16.
    private static bool TryReadTriShapeData1010(
        ReadOnlySpan<byte> bytes, int offset, string name, out NifMesh mesh, out int next)
    {
        mesh = null!;
        next = offset;
        try
        {
            var c = new NifCursor(bytes);
            c.SeekTo(offset);

            if (!TryReadGeometryCommon1010(ref c, out int nv, out Vector3[] vertices,
                    out Vector3[]? normals, out Vector2[]? texCoords, out int numTriangles))
                return false;
            if (numTriangles == 0)
                return false;

            uint numTrianglePoints = c.U32();
            if (numTrianglePoints != (uint)numTriangles * 3)
                return false;
            if (!TryBool(ref c, out bool hasTriangles) || !hasTriangles)
                return false;

            var triangles = new (ushort, ushort, ushort)[numTriangles];
            for (int t = 0; t < numTriangles; t++)
            {
                ushort a = c.U16(), b = c.U16(), cc = c.U16();
                if (a >= nv || b >= nv || cc >= nv)
                    return false;
                triangles[t] = (a, b, cc);
            }
            int matchGroups = c.U16();
            if (matchGroups is < 0 or > 70000)
                return false;

            mesh = new NifMesh
            {
                Name = name,
                Vertices = vertices,
                Normals = normals,
                TexCoords = texCoords,
                Triangles = triangles,
            };
            next = c.Pos;
            return true;
        }
        catch (NifFormatException)
        {
            return false;
        }
    }

    // NiTriStripsData body at 10.1.0.0 (self-validating). After the shared geometry:
    //   Num Strips u16; Strip Lengths u16 x Num Strips; Has Points bool(1); Points u16 (one array
    //   per strip, each Strip Lengths[i] long). We convert the strips to a triangle list, flipping
    //   winding on odd triangles and dropping degenerate (stitch) triangles.
    private static bool TryReadTriStripsData1010(
        ReadOnlySpan<byte> bytes, int offset, string name, out NifMesh mesh, out int next)
    {
        mesh = null!;
        next = offset;
        try
        {
            var c = new NifCursor(bytes);
            c.SeekTo(offset);

            if (!TryReadGeometryCommon1010(ref c, out int nv, out Vector3[] vertices,
                    out Vector3[]? normals, out Vector2[]? texCoords, out int numTriangles))
                return false;
            if (numTriangles == 0)
                return false;

            int numStrips = c.U16();
            if (numStrips is < 1 or > 10000)
                return false;
            var lengths = new int[numStrips];
            long totalPoints = 0;
            for (int s = 0; s < numStrips; s++)
            {
                lengths[s] = c.U16();
                totalPoints += lengths[s];
            }
            if (totalPoints is 0 or > 2_000_000)
                return false;
            if (!TryBool(ref c, out bool hasPoints) || !hasPoints)
                return false;

            var triangles = new List<(ushort, ushort, ushort)>();
            for (int s = 0; s < numStrips; s++)
            {
                int len = lengths[s];
                var strip = new ushort[len];
                for (int i = 0; i < len; i++)
                {
                    ushort idx = c.U16();
                    // 0xFFFF is a strip-restart sentinel in some data; otherwise indices must be valid.
                    if (idx != 0xFFFF && idx >= nv)
                        return false;
                    strip[i] = idx;
                }
                AppendStripTriangles(strip, triangles);
            }
            if (triangles.Count == 0)
                return false;

            mesh = new NifMesh
            {
                Name = name,
                Vertices = vertices,
                Normals = normals,
                TexCoords = texCoords,
                Triangles = triangles.ToArray(),
            };
            next = c.Pos;
            return true;
        }
        catch (NifFormatException)
        {
            return false;
        }
    }

    // Converts one triangle strip to triangles: triangle i is (p[i], p[i+1], p[i+2]) with winding
    // flipped on odd i to keep consistent facing. Degenerate triangles (a repeated index, used to
    // stitch strips together, plus the 0xFFFF restart sentinel) are dropped.
    internal static void AppendStrip(ushort[] strip, List<(ushort, ushort, ushort)> into) =>
        AppendStripTriangles(strip, into);

    private static void AppendStripTriangles(ushort[] strip, List<(ushort, ushort, ushort)> into)
    {
        for (int i = 0; i + 2 < strip.Length; i++)
        {
            ushort a = strip[i], b = strip[i + 1], cc = strip[i + 2];
            if (a == b || b == cc || a == cc || a == 0xFFFF || b == 0xFFFF || cc == 0xFFFF)
                continue;
            into.Add((i & 1) == 0 ? (a, b, cc) : (b, a, cc));
        }
    }

    // A 1-byte bool that must be exactly 0 or 1 (used to reject a wrong scan offset).
    private static bool TryBool(ref NifCursor c, out bool value)
    {
        byte b = c.U8();
        value = b != 0;
        return b is 0 or 1;
    }

    // Scans for the first external texture file name (a sized string ending in a known image
    // extension). Works for both the 4.2.x and 10.1.0.0 NiSourceTexture layouts.
    private static string? FindFirstTextureName(ReadOnlySpan<byte> bytes)
    {
        for (int q = 0; q + 4 <= bytes.Length; q++)
        {
            uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(q, 4));
            if (len is < 5 or > 255 || q + 4 + (int)len > bytes.Length)
                continue;
            ReadOnlySpan<byte> s = bytes.Slice(q + 4, (int)len);
            bool printable = true;
            foreach (byte ch in s)
                if (ch is < 0x20 or > 0x7E) { printable = false; break; }
            if (!printable)
                continue;
            string str = System.Text.Encoding.Latin1.GetString(s);
            if (str.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                || str.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                || str.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                return str;
        }
        return null;
    }

    // The geometry data shared by NiTriShapeData and NiTriStripsData at 4.x (NiGeometryData ->
    // NiTriBasedGeomData), read up to and including Num Triangles. Field order for 4.2.x (verified):
    //   Num Vertices u16; Has Vertices bool(1) + Vertices; Has Normals bool(1) + Normals; Bounding
    //   Sphere (centre+radius); Has Vertex Colors bool(1) + Color4s; Data Flags u16 (`until=4.2.2.0`,
    //   low 6 bits = UV-set count); UV sets; Num Triangles u16. No Keep/Compress bytes and no
    //   Consistency Flags (those are `since 10.x`); no Has-UV byte (that is `until=4.0.0.2`).
    private static void ReadGeometryCommon4x(
        ref NifCursor c, out int numVertices, out Vector3[] vertices,
        out Vector3[]? normals, out Vector2[][] uvSets, out Vector4[]? colors, out int numTriangles)
    {
        numVertices = c.U16();
        vertices = c.Bool() ? ReadVec3Array(ref c, numVertices) : [];
        normals = c.Bool() ? ReadVec3Array(ref c, numVertices) : null;
        c.Vec3(); c.F32();           // Bounding sphere: centre + radius
        colors = null;
        if (c.Bool())                // vertex colours (Color4); whether they're used is the property's call
        {
            colors = new Vector4[numVertices];
            for (int v = 0; v < numVertices; v++)
                colors[v] = new Vector4(c.F32(), c.F32(), c.F32(), c.F32());
        }

        int setCount = c.U16() & 0x3F; // Data Flags (until=4.2.2.0): low 6 bits = UV-set count
        uvSets = new Vector2[setCount][];
        for (int s = 0; s < setCount; s++)
        {
            Vector2[] set = new Vector2[numVertices];
            for (int v = 0; v < numVertices; v++)
                set[v] = new Vector2(c.F32(), c.F32());
            uvSets[s] = set;
        }

        numTriangles = c.U16();      // NiTriBasedGeomData
    }

    // NiTriShapeData at 4.2.x. After the shared geometry: Num Triangle Points u32 (=3x), triangles.
    // (`Has Triangles` is `since 10.1.0.0`, so at 4.2.x triangles follow directly.)
    private static NifMesh ReadTriShapeData(ref NifCursor c, NifVersion version, string name)
    {
        ReadGeometryCommon4x(ref c, out int numVertices, out Vector3[] vertices,
            out Vector3[]? normals, out Vector2[][] uvSets, out Vector4[]? colors, out int numTriangles);

        uint numTrianglePoints = c.U32();
        if (numTrianglePoints != (uint)numTriangles * 3)
            throw new NifFormatException($"Triangle point count {numTrianglePoints} != 3 x {numTriangles}.");

        var triangles = new (ushort, ushort, ushort)[numTriangles];
        for (int t = 0; t < numTriangles; t++)
        {
            ushort a = c.U16(), b = c.U16(), cc = c.U16();
            if (a >= numVertices || b >= numVertices || cc >= numVertices)
                throw new NifFormatException($"Triangle {t} indexes a vertex past {numVertices}.");
            triangles[t] = (a, b, cc);
        }

        return new NifMesh
        {
            Name = name,
            Vertices = vertices,
            Normals = normals,
            TexCoords = uvSets.Length > 0 ? uvSets[0] : null,
            UvSets = uvSets,
            VertexColors = colors,
            Triangles = triangles,
        };
    }

    // NiTriStripsData at 4.x. After the shared geometry: Num Strips u16; Strip Lengths u16 x strips;
    // Points u16 (one array per strip). `Has Points` is `since 10.0.1.3`, so there is NO Has-Points
    // byte at 4.x — points follow Strip Lengths directly. Strips are converted to a triangle list.
    private static NifMesh ReadTriStripsData4x(ref NifCursor c, string name)
    {
        ReadGeometryCommon4x(ref c, out int numVertices, out Vector3[] vertices,
            out Vector3[]? normals, out Vector2[][] uvSets, out Vector4[]? colors, out int _);

        int numStrips = c.U16();
        var lengths = new int[numStrips];
        for (int s = 0; s < numStrips; s++)
            lengths[s] = c.U16();

        var triangles = new List<(ushort, ushort, ushort)>();
        for (int s = 0; s < numStrips; s++)
        {
            int len = lengths[s];
            var strip = new ushort[len];
            for (int i = 0; i < len; i++)
            {
                ushort idx = c.U16();
                if (idx != 0xFFFF && idx >= numVertices)
                    throw new NifFormatException($"Strip index {idx} past {numVertices} vertices.");
                strip[i] = idx;
            }
            AppendStripTriangles(strip, triangles);
        }

        return new NifMesh
        {
            Name = name,
            Vertices = vertices,
            Normals = normals,
            TexCoords = uvSets.Length > 0 ? uvSets[0] : null,
            UvSets = uvSets,
            VertexColors = colors,
            Triangles = triangles.ToArray(),
        };
    }

    // NiSourceTexture body (4.2.x), from here positioned just past the type string:
    //   u32 unknown, 2x link ref (ignored), Use External (1-byte bool). If external, a sized-string
    //   File Name follows. We read only the file name and let the caller resync past the rest.
    private static string? ReadSourceTextureFileName(ref NifCursor c, ReadOnlySpan<byte> bytes)
    {
        try
        {
            c.U32();               // unknown/flags
            c.U32(); c.U32();      // link refs (no parent here)
            if (!c.Bool())         // Use External
                return null;
            string name = c.SizedString();
            // Sanity: an external texture name is a short Latin-1 file name, not arbitrary bytes.
            return name.Length is > 0 and < 256 && name.All(ch => ch is >= ' ' and < (char)127)
                ? name
                : null;
        }
        catch (NifFormatException)
        {
            return null;
        }
    }

    private static Vector3[] ReadVec3Array(ref NifCursor c, int n)
    {
        var a = new Vector3[n];
        for (int i = 0; i < n; i++)
            a[i] = c.Vec3();
        return a;
    }

    // Advances the cursor to the next position that begins a plausible sized type string naming a
    // "Ni*" block, so parsing can continue past a block body we don't model. Returns false if none.
    private static bool ResyncToNextTypeString(ref NifCursor c, ReadOnlySpan<byte> bytes)
    {
        for (int q = c.Pos; q + 6 <= bytes.Length; q++)
        {
            uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(q, 4));
            if (len is >= 4 and <= 40 && q + 4 + (int)len <= bytes.Length
                && bytes[q + 4] == (byte)'N' && bytes[q + 5] == (byte)'i')
            {
                c.SeekTo(q);
                return true;
            }
        }
        return false;
    }
}
