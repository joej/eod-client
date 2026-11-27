// SPDX-License-Identifier: GPL-3.0-or-later
//
// Doors in a model (roadmap M11: dungeon doors). How the retail client finds and moves them (daoc-re's doors note,
// RE/80, 2026-11-11): a node named "door" + digits is a door; its id comes from the name and where the model is placed
// (DungeonDoors); a "simple" door plays its own nodes' keyframes over [0, maxframes/30] s (maxframes from the extra data
// "maxframes" or a "-F<n>" name suffix, default 15), open forward, close backward, or with a "midframe" open 0 → mid and
// hold, close mid → end and snap to 0. A "four-state" door (eighth character 's', door001s) swaps child models instead:
// not built (two dungeon pieces in the install).
// In the install's dungeon doors (bdungdoor01, cminedoor01, …) the door node holds "collidee" and "visible", and the
// node under each that moves ("collision01", "door") carries its own NiKeyframeController with the same keys; the extra
// data is a UserPropBuffer string "maxframes=45" on every node of the door (1.5 s).
// Here: each node with keyframes inside a simple door's subtree is a "mover"; the meshes under it move with it (the walks
// tag each mesh with its nearest mover). The exporter turns each door into a glTF animation (GltfExporter.Doors).

using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace DaocAssets.Nif;

/// <summary>A door in a model: its node name (lower case, "door001"), how long it opens (seconds), the hold point of a
/// door with a mid frame, and whether it is a four-state door (not animated here).</summary>
public sealed record NifDoor(string Name, float Duration, float? Mid = null, bool FourState = false)
{
    /// <summary>The number after "door" (door001 → 1, door001H → 1; door123456789 → 123456789).</summary>
    public long Number => NifFile.DoorNumber(Name);
}

/// <summary>
/// A node inside a door that moves (it has keyframes), in NIF space: its rest world transform and its parent's (row
/// vectors), its rest local transform, and its keys. The meshes under it are baked at rest; at time t they move by
/// <see cref="Delta"/>. <paramref name="Carrier"/> is the nearest moving node above it in the same door, whose motion it
/// rides on (a door's ring under the door leaf: its own keys stay put, and it moves with the leaf).
/// </summary>
public sealed record NifMover(string Door, string Node, Matrix4x4 RestWorld, Matrix4x4 ParentWorld, Matrix4x4 RestLocal, KfaTrack Keys,
    NifMover? Carrier = null)
{
    /// <summary>Outside doors (<see cref="Door"/> empty): whether it is inside an "anim…" node, which a world object
    /// plays (<see cref="NifAnimation.Anims"/>); every mover outside doors is in <see cref="NifAnimation.All"/>.</summary>
    public bool InAnim { get; init; }

    /// <summary>The last key's time (seconds).</summary>
    public float Length => Math.Max(Math.Max(Keys.Rotations.LastOrDefault().Time, Keys.Translations.LastOrDefault().Time),
        Keys.Scales.LastOrDefault().Time);

    /// <summary>
    /// What a baked vertex is multiplied by at time t (row vectors): rest world⁻¹ × local(t) × parent world, then the
    /// carrier's delta (everything under the carrier moves with it).
    /// </summary>
    public Matrix4x4 Delta(float t)
    {
        Matrix4x4.Invert(RestWorld, out Matrix4x4 inverse);
        Matrix4x4 own = inverse * Local(t) * ParentWorld;
        return Carrier is null ? own : own * Carrier.Delta(t);
    }

    /// <summary>The node's local transform at time t: its keys, or its rest value for a channel without keys.</summary>
    public Matrix4x4 Local(float t)
    {
        Matrix4x4.Decompose(RestLocal, out Vector3 restScale, out Quaternion restRot, out Vector3 restTrans);
        Quaternion r = Keys.Rotations.Length > 0 ? SampleRotation(Keys.Rotations, t) : restRot;
        Vector3 p = Keys.Translations.Length > 0 ? Sample(Keys.Translations, t, Vector3.Lerp) : restTrans;
        float s = Keys.Scales.Length > 0 ? Sample(Keys.Scales, t, (a, b, u) => a + (b - a) * u) : restScale.X;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(p);
    }

    private static Quaternion SampleRotation((float Time, Quaternion Value)[] keys, float t) =>
        Sample(keys, t, (a, b, u) => Quaternion.Slerp(a, b, u));

    private static T Sample<T>((float Time, T Value)[] keys, float t, Func<T, T, float, T> lerp)
    {
        if (t <= keys[0].Time) return keys[0].Value;
        for (int i = 1; i < keys.Length; i++)
            if (t <= keys[i].Time)
            {
                float span = keys[i].Time - keys[i - 1].Time;
                return lerp(keys[i - 1].Value, keys[i].Value, span <= 0 ? 1 : (t - keys[i - 1].Time) / span);
            }
        return keys[^1].Value;
    }
}

/// <summary>
/// A model's own animation outside doors, exported as a glTF animation of its name: <see cref="Anims"/>, the "anim…"
/// nodes a world object plays continuously while the viewer is within <see cref="FarUnits"/>; <see cref="All"/>, every
/// node with keyframes (an effect plays all of its controllers from its start). Both loop over <see cref="Duration"/>.
/// </summary>
public sealed record NifAnimation(string Name, float Duration, float FarUnits)
{
    public const string Anims = "anims", All = "all";
}

public static partial class NifFile
{
    /// <summary>The frame rate door keyframes are counted in (maxframes/30 s), and the default frame count.</summary>
    public const float DoorFramesPerSecond = 30, DefaultDoorFrames = 15;

    // A door node, as the client tests a name (daoc-re's door-id answer, 2026-11-17): it starts with "door" in any case, is
    // at least 7 characters long, and the number read from right after "door" (C atol: leading digits) is not 0. So
    // door001, door001H (Galladoria's ROOTRoom03), door001s, Door001, door1:5 and door0012 are doors; door, door1, door12,
    // doorway and door000 are not.
    private static readonly Regex NameFrames = new("-F([0-9]+)", RegexOptions.CultureInvariant);
    private static readonly Regex NameMid = new("-M([0-9]+)", RegexOptions.CultureInvariant);

    /// <summary>True for a door node's name: "door…", 7 characters or more, a non-zero number after "door".</summary>
    public static bool IsDoorNode(string name) =>
        !string.IsNullOrEmpty(name) && name.Length >= 7 && name.StartsWith("door", StringComparison.OrdinalIgnoreCase)
        && DoorNumber(name) != 0;

    /// <summary>The number right after "door", read as C atol reads it (leading digits; 0 when there are none).</summary>
    public static long DoorNumber(string name)
    {
        long n = 0;
        for (int i = 4; i < name.Length && char.IsAsciiDigit(name[i]) && n < 1_000_000_000_000; i++)
            n = n * 10 + (name[i] - '0');
        return n;
    }

    /// <summary>The doors found in a model's nodes, the movers inside them, and which node index is which mover.</summary>
    internal sealed record DoorScan(IReadOnlyList<NifDoor> Doors, IReadOnlyList<NifMover> Movers, IReadOnlyDictionary<int, int> MoverOf)
    {
        public static readonly DoorScan None = new([], [], new Dictionary<int, int>());

        /// <summary>The animations outside doors (anims, all), from the movers.</summary>
        public IReadOnlyList<NifAnimation> Animations { get; init; } = [];
    }

    // The "anim…" node prefix and its far distance: a "-D<n>" suffix or extra data "faranim", n = 0 → 8000 units,
    // 1 → 16000 (inferred: (n + 1) × 8000); default 8000.
    private static readonly Regex NameFar = new("-D([0-9]+)", RegexOptions.CultureInvariant);

    internal static bool IsAnimNode(string name) => name.StartsWith("anim", StringComparison.OrdinalIgnoreCase);

    internal static float AnimFar(string name, IEnumerable<string> extra)
    {
        float? n = NameFar.Match(name) is { Success: true } m ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        foreach (string text in extra)
            foreach (string line in text.Split('\n', '\r', ';'))
            {
                string[] kv = line.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("faranim", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    n ??= v;
            }
        return ((n ?? 0) + 1) * 8000;
    }

    /// <summary>What the door scan needs of a node, from either reader.</summary>
    private interface IDoorNodes
    {
        int Count { get; }
        IEnumerable<int> Roots { get; }
        bool IsNode(int i);
        string Name(int i);
        int[] Children(int i);
        Matrix4x4 Local(int i);
        IEnumerable<string> ExtraStrings(int i);
        KfaTrack? Keys(int i, string name);
    }

    private static DoorScan ScanDoors(IDoorNodes n)
    {
        // Rest world and parent world of every node, from the roots.
        var world = new Dictionary<int, (Matrix4x4 World, Matrix4x4 Parent)>();
        var stack = new Stack<(int, Matrix4x4)>();
        foreach (int r in n.Roots)
            stack.Push((r, Matrix4x4.Identity));
        while (stack.Count > 0)
        {
            (int i, Matrix4x4 parent) = stack.Pop();
            if (i < 0 || i >= n.Count || world.ContainsKey(i) || !n.IsNode(i))
                continue;
            Matrix4x4 w = n.Local(i) * parent;
            world[i] = (w, parent);
            foreach (int c in n.Children(i))
                stack.Push((c, w));
        }

        // The doors: walking down from the roots, the first door node on a branch is a door and the client doesn't look
        // inside it; door-named nodes under it are its geometry (Darkspire's door001 / visible / door001, door001:5). A
        // second door of a name the model already has gets the same id; retail moves only the first, so it is left out.
        var outer = new List<int>();
        var down = new Stack<int>(n.Roots.Reverse());
        var visited = new HashSet<int>();
        while (down.Count > 0)
        {
            int i = down.Pop();
            if (!world.ContainsKey(i) || !visited.Add(i))
                continue;
            if (IsDoorNode(n.Name(i).Trim()))
            {
                outer.Add(i);
                continue;
            }
            foreach (int c in n.Children(i).Reverse())
                down.Push(c);
        }

        var doors = new List<NifDoor>();
        var movers = new List<NifMover>();
        var moverOf = new Dictionary<int, int>();
        foreach (int d in outer)
        {
            string name = n.Name(d).Trim();
            if (doors.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            // The eighth character 's' makes a four-state door (the note says 's'; upper case too is ours: AerusIsland14's
            // Door001S).
            bool four = name.Length >= 8 && name[7] is 's' or 'S';
            (float frames, float? mid) = DoorFrames(name, n.ExtraStrings(d));
            doors.Add(new NifDoor(name.ToLowerInvariant(), frames / DoorFramesPerSecond, mid / DoorFramesPerSecond, four));
            if (four)
                continue;
            // The nodes with keys in its whole subtree (door-named ones included: they are its geometry), each carried by
            // the nearest one above it.
            var todo = new Stack<(int Node, NifMover? Carrier)>(n.Children(d).Select(c => (c, (NifMover?)null)));
            var seen = new HashSet<int>();
            while (todo.Count > 0)
            {
                (int i, NifMover? carrier) = todo.Pop();
                if (!world.ContainsKey(i) || !seen.Add(i))
                    continue;
                if (n.Keys(i, n.Name(i)) is { } keys && (keys.Rotations.Length + keys.Translations.Length + keys.Scales.Length) > 0)
                {
                    moverOf[i] = movers.Count;
                    carrier = new NifMover(name.ToLowerInvariant(), n.Name(i), world[i].World, world[i].Parent, n.Local(i), keys, carrier);
                    movers.Add(carrier);
                }
                foreach (int c in n.Children(i))
                    todo.Push((c, carrier));
            }
        }
        // Outside the doors: every node with keyframes is a mover, carried by the nearest one above it; inside an "anim…"
        // node it belongs to the world object's animation too.
        var doorNodes = new HashSet<int>(outer);
        float far = float.MaxValue;
        var walk = new Stack<(int Node, NifMover? Carrier, bool InAnim)>(n.Roots.Select(r => (r, (NifMover?)null, false)));
        var walked = new HashSet<int>();
        while (walk.Count > 0)
        {
            (int i, NifMover? carrier, bool inAnim) = walk.Pop();
            if (!world.ContainsKey(i) || doorNodes.Contains(i) || !walked.Add(i))
                continue;
            string name = n.Name(i).Trim();
            if (IsAnimNode(name))
            {
                inAnim = true;
                far = Math.Min(far, AnimFar(name, n.ExtraStrings(i)));
            }
            if (n.Keys(i, n.Name(i)) is { } keys && (keys.Rotations.Length + keys.Translations.Length + keys.Scales.Length) > 0)
            {
                moverOf[i] = movers.Count;
                carrier = new NifMover("", n.Name(i), world[i].World, world[i].Parent, n.Local(i), keys, carrier) { InAnim = inAnim };
                movers.Add(carrier);
            }
            foreach (int c in n.Children(i))
                walk.Push((c, carrier, inAnim));
        }
        var outside = movers.Where(m => m.Door.Length == 0).ToList();
        var animations = new List<NifAnimation>();
        if (outside.Count > 0)
        {
            float all = Math.Max(outside.Max(m => m.Length), 1f / DoorFramesPerSecond);
            animations.Add(new NifAnimation(NifAnimation.All, all, far == float.MaxValue ? 8000 : far));
            if (outside.Any(m => m.InAnim))
                animations.Add(new NifAnimation(NifAnimation.Anims,
                    Math.Max(outside.Where(m => m.InAnim).Max(m => m.Length), 1f / DoorFramesPerSecond), far == float.MaxValue ? 8000 : far));
        }
        return doors.Count == 0 && movers.Count == 0 ? DoorScan.None
            : new DoorScan(doors, movers, moverOf) { Animations = animations };
    }

    /// <summary>Whether any node could move: a door, or a keyframe controller anywhere.</summary>
    private static bool MayMove(IEnumerable<string> names, IEnumerable<string> types) =>
        names.Any(IsDoorNode) || types.Any(t => t == "NiKeyframeController");

    // maxframes and midframe: a "-F<n>" / "-M<n>" in the name, else extra data ("maxframes=45", one key per line in a
    // UserPropBuffer, or a string of its own), else 15 frames and no mid frame.
    internal static (float Frames, float? Mid) DoorFrames(string name, IEnumerable<string> extra)
    {
        float? frames = NameFrames.Match(name) is { Success: true } f ? float.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        float? mid = NameMid.Match(name) is { Success: true } m ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        foreach (string text in extra)
            foreach (string line in text.Split('\n', '\r', ';'))
            {
                string[] kv = line.Split('=', 2);
                if (kv.Length != 2 || !float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    continue;
                string key = kv[0].Trim();
                if (key.Equals("maxframes", StringComparison.OrdinalIgnoreCase)) frames ??= v;
                else if (key.Equals("midframe", StringComparison.OrdinalIgnoreCase)) mid ??= v;
            }
        return (frames is > 0 ? frames.Value : DefaultDoorFrames, mid);
    }

    private sealed class DoorNodes4x(byte[] bytes, Block[] blocks) : IDoorNodes
    {
        public int Count => blocks.Length;
        public IEnumerable<int> Roots => [0];
        public bool IsNode(int i) => blocks[i].Type is "NiNode" or "NiLODNode" or "NiBillboardNode" or "NiTriShape" or "NiTriStrips";
        public string Name(int i) => blocks[i].Name;
        public int[] Children(int i) => blocks[i].Children;
        public Matrix4x4 Local(int i) => ToMatrix(blocks[i].Local);

        public IEnumerable<string> ExtraStrings(int i)
        {
            for (int e = blocks[i].ExtraRef, guard = 0; e >= 0 && e < blocks.Length && guard < 64; e = blocks[e].NextRef, guard++)
                foreach (string s in blocks[e].Strings)
                    yield return s;
        }

        public KfaTrack? Keys(int i, string name)
        {
            for (int c = blocks[i].Controller, guard = 0; c >= 0 && c < blocks.Length && guard < 16; c = blocks[c].NextRef, guard++)
                if (blocks[c].Type == "NiKeyframeController" && blocks[c].DataRef is int d && d >= 0 && d < blocks.Length
                    && blocks[d].Type == "NiKeyframeData")
                    try { return KfaClip.ReadKeysAt(bytes, blocks[d].Offset, name, skipType: true); }
                    catch (NifFormatException) { return null; }
            return null;
        }
    }

    private sealed class DoorNodesGb(byte[] bytes, Gb10File g) : IDoorNodes
    {
        private readonly Gb10Block[] _b = g.Blocks!;
        public int Count => _b.Length;
        public IEnumerable<int> Roots => g.Roots.Length > 0 ? g.Roots : [0];
        public bool IsNode(int i) => _b[i] is { Type: "NiNode" or "NiLODNode" or "NiBillboardNode" or "NiTriShape" or "NiTriStrips" };
        public string Name(int i) => _b[i]?.Name ?? "";
        public int[] Children(int i) => _b[i]?.Children ?? [];
        public Matrix4x4 Local(int i) => ToMatrix(_b[i].Local);

        public IEnumerable<string> ExtraStrings(int i) =>
            (_b[i]?.Extra ?? []).Where(e => e >= 0 && e < _b.Length && _b[e] is not null).SelectMany(e => _b[e].Strings);

        public KfaTrack? Keys(int i, string name)
        {
            for (int c = _b[i].Controller, guard = 0; c >= 0 && c < _b.Length && guard < 16; c = _b[c].NextController, guard++)
                if (_b[c] is { Type: "NiKeyframeController" } k && k.DataRef >= 0 && k.DataRef < _b.Length && _b[k.DataRef]?.Type == "NiKeyframeData")
                    try { return KfaClip.ReadKeysAt(bytes, _b[k.DataRef].Offset, name, skipType: false); }
                    catch (NifFormatException) { return null; }
            return null;
        }
    }
}
