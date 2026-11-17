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
    /// <summary>The number in the name (door001 → 1; door123456789 → 123456789), or -1.</summary>
    public long Number => long.TryParse(DoorDigits.Match(Name).Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : -1;

    private static readonly Regex DoorDigits = new("^door([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>
/// A node inside a door that moves (it has keyframes), in NIF space: its rest world transform and its parent's (row
/// vectors), its rest local transform, and its keys. The meshes under it are baked at rest; at time t they move by
/// <see cref="Delta"/>.
/// </summary>
public sealed record NifMover(string Door, string Node, Matrix4x4 RestWorld, Matrix4x4 ParentWorld, Matrix4x4 RestLocal, KfaTrack Keys)
{
    /// <summary>What a baked vertex is multiplied by at time t (row vectors): rest world⁻¹ × local(t) × parent world.</summary>
    public Matrix4x4 Delta(float t)
    {
        Matrix4x4.Invert(RestWorld, out Matrix4x4 inverse);
        return inverse * Local(t) * ParentWorld;
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

public static partial class NifFile
{
    /// <summary>The frame rate door keyframes are counted in (maxframes/30 s), and the default frame count.</summary>
    public const float DoorFramesPerSecond = 30, DefaultDoorFrames = 15;

    // "door" followed by two or more digits: a door node (retail's names: "door" + 2 digits outdoors, door001…009 in
    // dungeons, "door" + 9 digits anywhere). Not "door" alone or "door1" (a door's own parts: InconnuDoor01's two leaves,
    // "door1" and "door2", hang under its door001), nor a shape's "door1:5".
    private static readonly Regex DoorNode = new("^door[0-9]{2,}[^:]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NameFrames = new("-F([0-9]+)", RegexOptions.CultureInvariant);
    private static readonly Regex NameMid = new("-M([0-9]+)", RegexOptions.CultureInvariant);

    /// <summary>True for a door node's name: "door" and two or more digits (door001, door001s, door12, door123456789).</summary>
    public static bool IsDoorNode(string name) => !string.IsNullOrEmpty(name) && DoorNode.IsMatch(name);

    /// <summary>The doors found in a model's nodes, the movers inside them, and which node index is which mover.</summary>
    internal sealed record DoorScan(IReadOnlyList<NifDoor> Doors, IReadOnlyList<NifMover> Movers, IReadOnlyDictionary<int, int> MoverOf)
    {
        public static readonly DoorScan None = new([], [], new Dictionary<int, int>());
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

        var doors = new List<NifDoor>();
        var movers = new List<NifMover>();
        var moverOf = new Dictionary<int, int>();
        foreach (int d in world.Keys.Order())
        {
            string name = n.Name(d).Trim();
            if (!IsDoorNode(name) || doors.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            bool four = name.Length >= 8 && name[7] is 's' or 'S';
            (float frames, float? mid) = DoorFrames(name, n.ExtraStrings(d));
            doors.Add(new NifDoor(name.ToLowerInvariant(), frames / DoorFramesPerSecond, mid / DoorFramesPerSecond, four));
            if (four)
                continue;
            // The nodes with keys in its subtree (not entering another door; a node of the door's own name is its part:
            // Darkspire's leaves move under door001 / visible / door001).
            var todo = new Stack<int>(n.Children(d));
            var seen = new HashSet<int>();
            while (todo.Count > 0)
            {
                int i = todo.Pop();
                if (!world.ContainsKey(i) || !seen.Add(i)
                    || (IsDoorNode(n.Name(i)) && !n.Name(i).Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (n.Keys(i, n.Name(i)) is { } keys && (keys.Rotations.Length + keys.Translations.Length + keys.Scales.Length) > 0)
                {
                    moverOf[i] = movers.Count;
                    movers.Add(new NifMover(name.ToLowerInvariant(), n.Name(i), world[i].World, world[i].Parent, n.Local(i), keys));
                }
                foreach (int c in n.Children(i))
                    todo.Push(c);
            }
        }
        return doors.Count == 0 ? DoorScan.None : new DoorScan(doors, movers, moverOf);
    }

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
