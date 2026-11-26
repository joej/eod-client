// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: keeps the server builds from components (S→C 0x69 keep, 0x6C per component; New Frontiers keeps and
// towers; Old Frontiers keeps are zone fixtures). Each component's model is its type's (the 0x6C skin) at the TIER its
// height byte names, from library/keeps/pieces.csv (`make keeps`), placed around the keep and turned with it, with the
// branches for its realm, level and state shown (DaocAssets.Keeps.KeepLayout: retail's rules, daoc-re RE/92). 0x69 has
// no height: the keep stands on the terrain under it, found once that terrain is loaded.
// A piece's doors open and close on 0x99 and are clicked like a dungeon's (WorldView.Doors), by the id the client gives
// them (KeepLayout.DoorId: 7 + keep id low byte + its high byte + component + n).
// --keeps FILE replays a keep packet file (`mpk keeps`) as if the server had sent it, when the file's region is ours:
// our server has no New Frontiers keeps.

using System.Collections.Generic;
using System.Linq;
using DaocAssets.Keeps;
using DaocProtocol.Net;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private sealed class Keep
    {
        public required KeepInfo Info;
        public float? GroundY;   // Godot metres: the terrain under the keep, once loaded
        public readonly Dictionary<ushort, KeepComponentInfo> Parts = [];
        public readonly Dictionary<ushort, Node3D> Nodes = [];
    }

    private readonly Dictionary<ushort, Keep> _keeps = [];
    private readonly Dictionary<uint, (Node3D Model, string Door)> _keepDoors = [];   // keep door id → its piece and door
    private readonly HashSet<uint> _keepDoorsOpen = [];   // keep doors the last 0x99 opened
    private Dictionary<int, string?[]>? _keepPieces;   // skin → glb per tier (pieces.csv)
    private readonly Dictionary<string, Node3D?> _keepTemplates = [];
    private Dictionary<(string Name, int Realm, int Group), (KeepTextureSet Set, bool Damage, int Emittance)>? _keepMaterials;
    private Dictionary<int, string>? _keepDamage;   // damage state → overlay texture (keeps/damage.csv)

    private void ListenForKeeps()
    {
        _bridge.KeepArrived += k =>
        {
            Keep keep = _keeps.TryGetValue(k.KeepId, out Keep? old) ? old : _keeps[k.KeepId] = new Keep { Info = k };
            keep.Info = k;
            RebuildKeep(keep);
        };
        _bridge.KeepComponentArrived += c =>
        {
            if (!_keeps.TryGetValue(c.KeepId, out Keep? keep))
                return;   // the server sends the keep first
            keep.Parts[c.ComponentId] = c;
            PlaceKeepPart(keep, c);
        };
        _bridge.KeepComponentChanged += d =>
        {
            // Health 199 means "unchanged" (daoc-re RE/92 §1).
            if (_keeps.TryGetValue(d.KeepId, out Keep? keep) && keep.Parts.TryGetValue(d.ComponentId, out KeepComponentInfo? c))
                PlaceKeepPart(keep, keep.Parts[d.ComponentId] = c with
                {
                    Height = d.Height, Health = d.Health == KeepComponentDetail.HealthUnchanged ? c.Health : d.Health, Flags = d.Flags,
                });
        };
        _bridge.KeepRealmChanged += r =>
        {
            if (_keeps.TryGetValue(r.KeepId, out Keep? keep))
            {
                keep.Info = keep.Info with { Realm = r.Realm, Level = r.Level };
                RebuildKeep(keep);
            }
        };
        _bridge.KeepUpdated += u =>
        {
            if (!_keeps.TryGetValue(u.KeepId, out Keep? keep))
                return;
            keep.Info = keep.Info with { Realm = u.Realm, Level = u.Level };
            // One byte per component, in the order they were sent (by component id, as the server keeps them).
            ushort[] ids = keep.Parts.Keys.Order().ToArray();
            for (int i = 0; i < ids.Length && i < u.Components.Count; i++)
            {
                byte b = u.Components[i];
                KeepComponentInfo c = keep.Parts[ids[i]];
                byte flags = (byte)((b & KeepUpdate.DeadBit) != 0 ? KeepComponentInfo.Broken : 0);
                flags |= (byte)((b & KeepUpdate.RaisedBit) != 0 ? KeepComponentInfo.Raised : 0);
                flags |= (byte)(flags == 0 && (b & KeepUpdate.ClimbableBit) != 0 ? KeepComponentInfo.Climbable : 0);
                keep.Parts[ids[i]] = c with { Height = (byte)(b & KeepUpdate.HeightMask), Flags = flags };
            }
            RebuildKeep(keep);
        };
        _bridge.KeepRemoved += (keepId, componentId) =>
        {
            if (!_keeps.TryGetValue(keepId, out Keep? keep))
                return;
            foreach ((ushort id, Node3D node) in keep.Nodes.Where(n => componentId is null || n.Key == componentId).ToList())
            {
                DropKeepPart(node);
                keep.Nodes.Remove(id);
                keep.Parts.Remove(id);
            }
            if (componentId is null)
                _keeps.Remove(keepId);
        };
    }

    // --keeps FILE: the packets go through the bridge as the server's would. The file says its region on its first line.
    private void ReplayKeeps(string path)
    {
        if (!Godot.FileAccess.FileExists(path))
        {
            GD.PrintErr($"[keeps] {path}: no such file");
            return;
        }
        string text = Godot.FileAccess.GetFileAsString(path);
        var match = System.Text.RegularExpressions.Regex.Match(text, @"region (\d+)");
        int region = _world.Position.RegionId;
        if (match.Success && int.Parse(match.Groups[1].Value) != region)
        {
            GD.Print($"[keeps] {System.IO.Path.GetFileName(path)} is for region {match.Groups[1].Value}; we're in {region}: not replayed");
            return;
        }
        List<ServerPacket> packets = DaocProtocol.World.KeepReplay.ReadText(text);
        foreach (ServerPacket p in packets)
            _bridge.Dispatch(p);
        GD.Print($"[keeps] replayed {packets.Count} packets from {System.IO.Path.GetFileName(path)}: {_keeps.Count} keeps");
    }

    private void RebuildKeep(Keep keep)
    {
        foreach (KeepComponentInfo c in keep.Parts.Values)
            PlaceKeepPart(keep, c);
    }

    // In the physics frame: the ground under each keep still waiting for it (its zone's terrain may stream in later).
    private void GroundKeeps()
    {
        foreach (Keep keep in _keeps.Values)
        {
            if (keep.GroundY is not null)
                continue;
            Vector3 at = ToGodot(keep.Info.X, keep.Info.Y, 0);
            if (TerrainAbove(new Vector3(at.X, -400f, at.Z)) is float y)
            {
                keep.GroundY = y;
                RebuildKeep(keep);
                GD.Print($"[keeps] keep {keep.Info.KeepId} stands at z {y / RegionMetresPerUnit:0} ({keep.Parts.Count} components)");
            }
        }
    }

    private void PlaceKeepPart(Keep keep, KeepComponentInfo c)
    {
        if (keep.Nodes.Remove(c.ComponentId, out Node3D? old))
            DropKeepPart(old);
        if (keep.GroundY is not float ground || KeepPart(keep, c) is not Node3D node)
            return;
        KeepInfo k = keep.Info;
        (double x, double y, double degrees) = KeepLayout.Place(k.X, k.Y, k.Heading, c.X, c.Y, c.Heading);
        Vector3 at = ToGodot((float)x, (float)y, 0);
        node.Position = new Vector3(at.X, ground, at.Z);
        node.Rotation = new Vector3(0, -Mathf.DegToRad((float)degrees), 0);
        _entityRoot.AddChild(node);
        MakeSolid(node, true, drawnWalls: false);
        keep.Nodes[c.ComponentId] = node;
        TagKeepDoors(node, k.KeepId, c.ComponentId);
    }

    // A piece's doors (its door001… animations): their parts tagged with the door's id (for clicks), a player of their own
    // each, and kept by id for 0x99 (WorldView.Doors).
    private void TagKeepDoors(Node3D model, ushort keepId, ushort componentId)
    {
        if (FindAnimationPlayer(model) is not AnimationPlayer imported)
            return;
        foreach (int n in KeepLayout.DoorNumbers(imported.GetAnimationList().Select(a => a.ToString())).ToList())
        {
            string door = $"door{n:000}";
            if (KeepLayout.DoorId(keepId, componentId, door) is not uint id)
                continue;
            foreach (Node3D part in DoorParts(model, door))
                part.SetMeta("door_id", (long)id);
            var player = new AnimationPlayer { Name = DoorPlayerPrefix + door };
            player.AddAnimationLibrary("", imported.GetAnimationLibrary(""));
            model.AddChild(player);
            _keepDoors[id] = (model, door);
            // A rebuilt piece (0x6D, a realm change) starts closed; the client may not re-apply the state (daoc-re), so we
            // do: a door the server last opened stands open again.
            if (_keepDoorsOpen.Contains(id))
            {
                player.Play(door);
                player.Seek(player.GetAnimation(door).Length, true);
                SetDoorBlocking(model, door, false);
            }
        }
    }

    private void DropKeepPart(Node3D node)
    {
        foreach (uint id in _keepDoors.Where(d => d.Value.Model == node).Select(d => d.Key).ToList())
        {
            _keepDoors.Remove(id);
            _doorsBlockingAt.Remove(id);
        }
        node.QueueFree();
    }

    // The component's model at its tier, with only the branches for its realm, tier and state; null without one.
    private Node3D? KeepPart(Keep keep, KeepComponentInfo c)
    {
        string lib = _libraryPath ?? "";
        if (lib.Length == 0)
            return null;
        if (_keepPieces is null)
        {
            string table = lib.PathJoin("keeps").PathJoin("pieces.csv");
            _keepPieces = Godot.FileAccess.FileExists(table) ? KeepLayout.ReadPieces(Godot.FileAccess.GetFileAsString(table)) : [];
            GD.Print($"[keeps] {_keepPieces.Count} component types{(_keepPieces.Count == 0 ? " (run make keeps)" : "")}");
        }
        bool raised = (c.Flags & KeepComponentInfo.Raised) != 0;
        if (!_keepPieces.TryGetValue(c.Skin, out string?[]? tiers)
            || KeepLayout.Tier(tiers, KeepLayout.TierColumn(c.Height, c.KeepId, raised)) is not int tier
            || tiers[tier - 1] is not string glb)
        {
            GD.Print($"[keeps] keep {c.KeepId} component {c.ComponentId}: no model for skin {c.Skin} at height {c.Height}");
            return null;
        }
        if (!_keepTemplates.TryGetValue(glb, out Node3D? template))
        {
            string path = lib.PathJoin("models").PathJoin(glb + ".glb");
            template = Godot.FileAccess.FileExists(path) ? ModelTexturing.LoadGlb(path, TexDirs("keeps")) : null;
            if (template is not null)
                NifParticles.AddFireLight(template);   // a keep's braziers light their surroundings (Modern particles)
            _keepTemplates[glb] = template;
        }
        if (template is null)
            return null;
        var node = (Node3D)template.Duplicate();
        node.Name = $"keep{c.KeepId}_{c.ComponentId}";
        node.SetMeta("glb", glb);
        bool climbable = (c.Flags & KeepComponentInfo.Climbable) != 0;
        int damage = KeepLayout.DamageState((c.Flags & KeepComponentInfo.Broken) != 0, c.KeepId, c.Health);
        bool broken = damage == 1;
        int levelTier = KeepLayout.LevelTier(keep.Info.Level);
        foreach (MeshInstance3D mi in node.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>().ToList())
        {
            Material? m = mi.Mesh?.GetSurfaceCount() > 0 ? mi.GetActiveMaterial(0) : null;
            m = ClassicLighting.Source(m) ?? m;
            string? branches = m is not null && m.HasMeta(ModelTexturing.LayersMeta)
                && m.GetMeta(ModelTexturing.LayersMeta).AsGodotDictionary().TryGetValue("branches", out Variant b) ? b.AsString() : null;
            // A wall-climb volume ("sclimb") is solid only while the server marks the piece climbable (daoc-re's climbing note).
            if (!KeepLayout.Shows(branches, keep.Info.Realm, levelTier, broken, climbable)
                || (!climbable && mi.Name.ToString().StartsWith(DaocAssets.Nif.GltfExporter.WallClimbPrefix, System.StringComparison.Ordinal)))
                mi.Free();
        }
        // The keep's textures for its realm and level tier ("!door…" by the piece's own tier).
        if (_keepMaterials is null)
        {
            string table = lib.PathJoin("keeps").PathJoin("materials.csv");
            _keepMaterials = Godot.FileAccess.FileExists(table) ? KeepMaterials.ReadCsv(Godot.FileAccess.GetFileAsString(table)) : [];
            string damageTable = lib.PathJoin("keeps").PathJoin("damage.csv");
            _keepDamage = Godot.FileAccess.FileExists(damageTable) ? KeepMaterials.ReadDamageCsv(Godot.FileAccess.GetFileAsString(damageTable)) : [];
        }
        string? overlay = damage > 0 && _keepDamage!.TryGetValue(damage, out string? o) ? o : null;
        int column = KeepLayout.TierColumn(c.Height, c.KeepId, raised);
        ModelTexturing.ApplyKeepTextures(node, name =>
            _keepMaterials.TryGetValue((name, keep.Info.Realm, KeepMaterials.Group(name, levelTier, column)), out var m)
                ? (m.Set.Base, m.Set.Dark, m.Emittance / 255f, m.Damage ? overlay : null) : null,
            _libraryPath!.PathJoin("overrides"), _libraryPath!.PathJoin("textures").PathJoin("keeps"));
        return node;
    }
}
