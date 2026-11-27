// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: spell effects (roadmap M12), as the retail client shows them (daoc-re RE/78 "Spell effects"; RE/82 for the
// cull distance). Every visual is an effect NIF from spells.csv → speffects.csv → spnifs.csv (`mpk speffects`:
// models/speffects.csv and models/effects/<name>.glb with its particles); effects never loop, each lasts its Duration.
// - 0x72 (cast, T = cast time): instant (T = 0): Effect3 on the caster now. Otherwise Effect1A now; when animations are
//   allowed (Play Anims 2 stops them while the caster moves) also Effect1B now and Effect2 at +1 s; Effect3 at T - 1 s.
// - 0x1B (landing): with the pulse flag only the Pulse effect, on the target. Play Anims 1 runs the cast sequence again
//   with the bolt time as T. A spell's Remove letters (A, B, 2, 3) take those caster effects off. A bolt (a Projectile
//   effect and a known caster) starts after the Projectile Lag and flies for bolt time - lag (at least 0.5 s; no bolt
//   time: the projectile's Duration) from the caster's attach node (else between the hands) to the target's chest
//   (targetchest, else Bip01, else its root), at lerp(start, end, min(age / (duration - 0.1 s), 0.99)). Without a bolt,
//   a Buildup effect (result not 2 or 3) shows on the target and the impact waits 2 s. The impact by result: 1 Target, 0
//   and 4 Resist, 2 Radius (else Target), 3 Resist.
// - 0x73: all the caster's spell effects go.
// - Placement: the attach node ("rootnode" or none: the root), else Bip01, else the root; the effect follows that node's
//   position each frame, not its rotation. Track Angle 0: turned 180° about up once, at creation; 1: the figure's facing;
//   2: the node's own rotation, then turned about the Spin Axis (1 X, 2 Y, 3 Z) by Angle. Auto Scale: the figure's size.
//   Playback at Rate % (particles); at most 20 effects on a figure; an effect on the root isn't added twice; none on a
//   figure more than 2048 units from our character in x or y (4096 for result 4).
// Their sounds: WorldView.SpellSounds. Ours / not built: the focus channel (Focus CasterFX, Focus Effect), the last 0.1 s of a
// bolt at 5x speed, weapon trails and item projectiles (NIF ids from 1500), MonEffects NIFs from spells, and the
// EffectsFlag setting (all effects are shown). An effect's moving parts, fades, colour changes and scrolling play from its
// start at its rate (its "all" animation, MaterialAnims).

using DaocAssets.Effects;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const float FxCull = 2048 * RegionMetresPerUnit, FxCullFar = 4096 * RegionMetresPerUnit;
    private const int FxPerFigure = 20;

    private sealed record FxRow(string Model, string Node, float Duration, float Rate, int Track, bool AutoScale, int Spin, float Angle);

    private sealed class LiveFx
    {
        public required Node3D Node;
        public required ushort Owner;        // the figure it sits on (the caster of a bolt)
        public required int EffectId;
        public required FxRow Row;
        public required double Born;
        public required float Duration;      // seconds
        public string Tag = "";              // the caster effect it is (1A, 1B, 2, 3), for Remove letters
        public ushort FlyTo;                 // a bolt's target (0: not a bolt)
        public Vector3 FlyFrom;
        public bool OnRoot;
    }

    private Dictionary<int, FxRow>? _fxRows;
    private Dictionary<int, SpellVisual>? _spellVisuals;
    private readonly List<LiveFx> _liveFx = [];
    private readonly List<(double At, Action Run)> _pendingFx = [];
    private readonly Dictionary<string, Node3D?> _fxTemplates = [];
    private Node3D? _fxRoot;

    private SpellVisual? SpellVisualOf(ushort spell)
    {
        if (_spellVisuals is null)
        {
            string path = (_libraryPath ?? "").PathJoin("icons").PathJoin("spells.csv");
            _spellVisuals = SpellEffectTables.ParseSpells(Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : "");
        }
        return _spellVisuals.GetValueOrDefault(spell);
    }

    private FxRow? FxRowOf(int id)
    {
        if (_fxRows is null)
        {
            _fxRows = [];
            string path = (_libraryPath ?? "").PathJoin("models").PathJoin("speffects.csv");
            if (Godot.FileAccess.FileExists(path))
                foreach (string line in Godot.FileAccess.GetFileAsString(path).Split('\n').Skip(1))
                {
                    string[] c = line.TrimEnd('\r').Split(',');
                    if (c.Length < 9 || !int.TryParse(c[0], out int fxId) || c[1].Length == 0)
                        continue;
                    float Num(int i) => float.TryParse(c[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0;
                    _fxRows[fxId] = new FxRow(c[1], c[2].Trim(), Num(3) / 1000f, Num(4) <= 0 ? 1f : Num(4) / 100f, (int)Num(5),
                        Num(6) != 0, (int)Num(7), Num(8));
                }
            GD.Print($"[spellfx] {_fxRows.Count} drawable spell effects");
        }
        return id > 0 ? _fxRows.GetValueOrDefault(id) : null;
    }

    // ----- the packets -----

    private void CastEffects(SpellCast c, bool animations)
    {
        if (SpellVisualOf(c.SpellId) is not { } v)
            return;
        double t = c.CastMilliseconds / 1000.0;
        if (t <= 0)
        {
            AddFx(v.Effect3, c.CasterId, "3");
            return;
        }
        AddFx(v.Effect1A, c.CasterId, "1A");
        if (animations)
        {
            AddFx(v.Effect1B, c.CasterId, "1B");
            Later(1, () => AddFx(v.Effect2, c.CasterId, "2"));
        }
        Later(Math.Max(0, t - 1), () => AddFx(v.Effect3, c.CasterId, "3"));
    }

    private void LandEffects(SpellEffect fx)
    {
        if (SpellVisualOf(fx.SpellId) is not { } v)
            return;
        if (fx.Pulse)
        {
            AddFx(v.Pulse, fx.TargetId, "", far: fx.Result == 4);
            return;
        }
        // Remove letters: those caster effects come off.
        if (CastLook(fx.SpellId) is { Remove.Length: > 0 } look)
            foreach (char letter in look.Remove.ToUpperInvariant())
                RemoveFx(f => f.Owner == fx.CasterId && f.Tag == (letter is 'A' or 'B' ? "1" + letter : letter.ToString()));

        bool far = fx.Result == 4;
        int impact = fx.Result switch
        {
            1 => v.Target,
            2 => v.Radius > 0 ? v.Radius : v.Target,
            _ => v.Resist,
        };
        double wait = 0;
        float bolt = fx.BoltTenths / 10f;
        if (v.Projectile > 0 && FxRowOf(v.Projectile) is { } proj && FxOwnerNode(fx.CasterId) is not null)
        {
            float lag = v.ProjectileLagMs / 1000f;
            float travel = bolt > 0 ? Math.Max(Math.Max(bolt, 0.5f) - lag, 0.5f) : proj.Duration;
            Later(lag, () => AddBolt(v.Projectile, fx.CasterId, fx.TargetId, travel));
            wait = lag + travel;
        }
        else if (v.Buildup > 0 && fx.Result is not (2 or 3))
        {
            AddFx(v.Buildup, fx.TargetId, "", far: far);
            wait = 2;
        }
        Later(wait, () => AddFx(impact, fx.TargetId, "", far: far));
    }

    private void InterruptEffects(ushort caster) => RemoveFx(f => f.Owner == caster);

    // ----- effects -----

    private void Later(double seconds, Action run)
    {
        if (seconds <= 0)
            run();
        else
            _pendingFx.Add((_seconds + seconds, run));
    }

    // The node an effect sits on: our body, or the entity's.
    private Node3D? FxOwnerNode(ushort id) =>
        id == SelfId ? _body : _entities.TryGetValue(id, out Entity? e) && IsInstanceValid(e.Node) && !e.GroundObject ? e.Node : null;

    // The figure under an owner node (where its skeleton and nodes are), or the node itself.
    private Node3D FxFigure(ushort id, Node3D owner) =>
        id == SelfId ? (_ownFigure is not null && IsInstanceValid(_ownFigure) ? _ownFigure : owner)
            : _entities.TryGetValue(id, out Entity? e) && e.Figure is { } f && IsInstanceValid(f) ? f
            : owner.GetChildren().OfType<Node3D>().FirstOrDefault(n => n.FindChildren("*", "Skeleton3D", true, false).Count > 0) ?? owner;

    // A named node's world transform on a figure: a bone of its skeleton, else a node of that name; null if neither.
    private static Transform3D? FxNode(Node3D figure, string name)
    {
        if (name.Length == 0)
            return null;
        foreach (Skeleton3D sk in figure.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>())
            if (sk.FindBone(name) is int bone and >= 0)
                return sk.GlobalTransform * sk.GetBoneGlobalPose(bone);
        return figure.FindChild(name, true, false) is Node3D n ? n.GlobalTransform : null;
    }

    // Where an effect on this node goes: the node, else Bip01, else the figure's root.
    private Transform3D FxAttach(ushort owner, Node3D ownerNode, string node, bool onRoot)
    {
        Node3D figure = FxFigure(owner, ownerNode);
        if (!onRoot && (FxNode(figure, node) ?? FxNode(figure, "Bip01")) is Transform3D t)
            return t;
        return ownerNode.GlobalTransform;
    }

    private Node3D? FxInstance(FxRow row)
    {
        if (!_fxTemplates.TryGetValue(row.Model, out Node3D? template))
        {
            string path = (_libraryPath ?? "").PathJoin("models").PathJoin(row.Model + ".glb");
            template = Godot.FileAccess.FileExists(path) ? ModelTexturing.LoadGlb(path, TexDirs("effects")) : null;
            _fxTemplates[row.Model] = template;
        }
        return template?.Duplicate() as Node3D;
    }

    private bool FxAllowed(ushort owner, Node3D ownerNode, bool far)
    {
        Vector3 me = _body.GlobalPosition, at = ownerNode.GlobalPosition;
        float limit = far ? FxCullFar : FxCull;
        return Mathf.Abs(at.X - me.X) <= limit && Mathf.Abs(at.Z - me.Z) <= limit
            && _liveFx.Count(f => f.Owner == owner) < FxPerFigure;
    }

    private void AddFx(int effectId, ushort owner, string tag, bool far = false)
    {
        if (FxRowOf(effectId) is not { } row || FxOwnerNode(owner) is not { } ownerNode || !FxAllowed(owner, ownerNode, far))
            return;
        bool onRoot = row.Node.Length == 0 || row.Node.Equals("rootnode", StringComparison.OrdinalIgnoreCase);
        if (onRoot && _liveFx.Any(f => f.Owner == owner && f.EffectId == effectId && f.OnRoot))
            return;   // an effect on the root isn't added twice to the same figure
        if (FxInstance(row) is not { } node)
            return;
        _fxRoot ??= AddFxRoot();
        _fxRoot.AddChild(node);
        var fx = new LiveFx
        {
            Node = node, Owner = owner, EffectId = effectId, Row = row, Born = _seconds, Duration = Math.Max(row.Duration, 0.1f),
            Tag = tag, OnRoot = onRoot,
        };
        Place(fx, ownerNode, created: true);
        NifParticles.PlayOnce(node, row.Rate);
        ModelTexturing.PlayOwnAnimation(node, row.Rate);
        _liveFx.Add(fx);
    }

    private void AddBolt(int effectId, ushort caster, ushort target, float travel)
    {
        if (FxRowOf(effectId) is not { } row || FxOwnerNode(caster) is not { } casterNode || !FxAllowed(caster, casterNode, false)
            || FxInstance(row) is not { } node)
            return;
        _fxRoot ??= AddFxRoot();
        _fxRoot.AddChild(node);
        // From the effect's attach node, else between the hands.
        Node3D figure = FxFigure(caster, casterNode);
        Vector3 from = FxNode(figure, row.Node)?.Origin
            ?? (FxNode(figure, "Bip01 L Hand") is Transform3D l && FxNode(figure, "Bip01 R Hand") is Transform3D r
                ? (l.Origin + r.Origin) / 2 : casterNode.GlobalPosition + Vector3.Up);
        var fx = new LiveFx
        {
            Node = node, Owner = caster, EffectId = effectId, Row = row, Born = _seconds, Duration = Math.Max(travel, 0.1f),
            FlyTo = target, FlyFrom = from,
        };
        node.GlobalPosition = from;
        NifParticles.PlayOnce(node, row.Rate);
        ModelTexturing.PlayOwnAnimation(node, row.Rate);
        _liveFx.Add(fx);
    }

    private Node3D AddFxRoot()
    {
        var root = new Node3D { Name = "SpellEffects" };
        AddChild(root);
        return root;
    }

    // Puts an effect at its node: position always; rotation by its Track Angle; size by Auto Scale.
    private void Place(LiveFx fx, Node3D ownerNode, bool created)
    {
        Transform3D at = FxAttach(fx.Owner, ownerNode, fx.Row.Node, fx.OnRoot);
        Basis facing = ownerNode.GlobalBasis.Orthonormalized();
        Basis turn = fx.Row.Track switch
        {
            // 0: the figure's facing turned half round, once.
            0 when created => facing * new Basis(Vector3.Up, Mathf.Pi),
            0 => fx.Node.GlobalBasis.Orthonormalized(),
            // 2: the node's own rotation (a bone's frame is the NIF's: undo the glTF turn), then the spin.
            2 => at.Basis.Orthonormalized() * FromGltf.Basis.Orthonormalized() * SpinBasis(fx.Row),
            _ => facing,
        };
        float scale = 1f;
        if (fx.Row.AutoScale)
        {
            Node3D figure = FxFigure(fx.Owner, ownerNode);
            scale = figure.GlobalBasis.Scale.X / Mathf.Max(ownerNode.GlobalBasis.Scale.X, 0.0001f);
        }
        fx.Node.GlobalTransform = new Transform3D(turn.Scaled(Vector3.One * scale), at.Origin);
    }

    private static Basis SpinBasis(FxRow row) => row.Spin switch
    {
        1 => new Basis(Vector3.Right, Mathf.DegToRad(row.Angle)),
        2 => new Basis(Vector3.Up, Mathf.DegToRad(row.Angle)),
        3 => new Basis(Vector3.Back, Mathf.DegToRad(row.Angle)),
        _ => Basis.Identity,
    };

    private void RemoveFx(Func<LiveFx, bool> which)
    {
        foreach (LiveFx f in _liveFx.Where(which).ToList())
        {
            if (IsInstanceValid(f.Node))
                f.Node.QueueFree();
            _liveFx.Remove(f);
        }
    }

    // Each frame: due effects start, live ones follow their figures or fly, finished ones go.
    private void UpdateSpellFx()
    {
        if (_pendingFx.Count > 0)
        {
            var due = _pendingFx.Where(p => p.At <= _seconds).ToList();
            _pendingFx.RemoveAll(p => p.At <= _seconds);
            foreach ((_, Action run) in due)
                run();
        }
        for (int i = _liveFx.Count - 1; i >= 0; i--)
        {
            LiveFx f = _liveFx[i];
            double age = _seconds - f.Born;
            Node3D? owner = FxOwnerNode(f.Owner);
            if (age >= f.Duration || !IsInstanceValid(f.Node) || (owner is null && f.FlyTo == 0))
            {
                if (IsInstanceValid(f.Node))
                    f.Node.QueueFree();
                _liveFx.RemoveAt(i);
                continue;
            }
            if (f.FlyTo != 0)
            {
                // To the target's chest, else Bip01, else its root; it ends just short of it.
                if (FxOwnerNode(f.FlyTo) is Node3D target)
                {
                    Node3D figure = FxFigure(f.FlyTo, target);
                    Vector3 end = (FxNode(figure, "targetchest") ?? FxNode(figure, "Bip01"))?.Origin ?? target.GlobalPosition + Vector3.Up;
                    float t = Mathf.Min((float)(age / Math.Max(f.Duration - 0.1, 0.05)), 0.99f);
                    Vector3 pos = f.FlyFrom.Lerp(end, t);
                    Vector3 dir = end - f.FlyFrom;
                    f.Node.GlobalPosition = pos;
                    if (dir.LengthSquared() > 1e-4f)
                        f.Node.LookAt(pos + dir, Mathf.Abs(dir.Normalized().Y) > 0.99f ? Vector3.Forward : Vector3.Up);
                }
            }
            else if (owner is not null)
                Place(f, owner, created: false);
        }
    }
}
