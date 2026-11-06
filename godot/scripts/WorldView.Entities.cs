// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: Other players and NPCs (M5+): spawning, figures and their look, gear, attack mode, interpolation.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    // ----- Live entities (M5): spawn, move, remove -----

    /// <summary>
    /// 0xA1 carries ZONE-LOCAL coordinates and only the low 8 bits of the entity's zone-skin id, so
    /// resolve the full zone within the player's region (all in-view entities share it) and add that
    /// zone's region-coordinate offset to recover a region-global position. Falls back to the
    /// player's own zone when the skin byte matches nothing (the common case: everyone in one zone).
    /// </summary>
    private Vector3 ZoneLocalToGodot(ushort xOffset, ushort yOffset, ushort z, byte zoneSkin)
    {
        ushort region = _world.Position.RegionId;
        Zone zone = Zones.All.FirstOrDefault(zz => zz.RegionId == region && (zz.Id & 0xFF) == zoneSkin)
            ?? _world.Zone;
        return ToGodot(zone.X + xOffset, zone.Y + yOffset, z);
    }

    // Static world objects (0xD9: doors, items, banners), by object id: not drawn as entities (a keep's
    // gate is part of its zone model), and a position update for one must not turn it into a capsule.
    private readonly Dictionary<ushort, ObjectCreate> _worldObjects = new();

    // Untargetable NPCs with no figure (markers and effects, e.g. a teleporter's light node): not drawn,
    // and kept here so their position updates don't spawn a grey capsule either.
    private readonly HashSet<ushort> _hiddenNpcs = new();

    private void OnWorldObject(ObjectCreate o)
    {
        _worldObjects[o.ObjectId] = o;
        GD.Print($"[object] {o.ObjectId} \"{o.Name}\" model {o.Model} at {o.X} {o.Y} {o.Z} (not drawn)");
    }

    private void OnObjectMoved(ObjectUpdate u)
    {
        // The server sends 0xA1 for our own character too; we drive that from input, so skip it.
        if (u.ObjectId == _world.Position.ObjectId || _worldObjects.ContainsKey(u.ObjectId) || _hiddenNpcs.Contains(u.ObjectId))
            return;
        Vector3 pos = ZoneLocalToGodot(u.XOffsetInZone, u.YOffsetInZone, u.Z, u.ZoneSkinId);
        float yaw = YawFromHeading(u.HeadingOnly);
        if (_entities.TryGetValue(u.ObjectId, out Entity? e))
        {
            e.TargetPosition = pos;
            e.TargetYaw = yaw;
        }
        else
        {
            // An update for something we never saw spawn (we entered mid-session, or the create
            // packet is one we don't parse yet): place a plain capsule so the world isn't empty, and ask
            // the server for its create packet (0xBE, as retail does), once per object.
            bool ask = _session is not null && _closedReason is null && _objectsRequested.Add(u.ObjectId);
            GD.Print($"[entity] {u.ObjectId}: seen only by its position updates (no create packet): a grey capsule" +
                (ask ? ", create requested" : ""));
            SpawnEntity(u.ObjectId, pos, yaw, new Color(0.55f, 0.55f, 0.6f));
            if (ask)
                _ = RequestObject(u.ObjectId);
        }
    }

    private void OnNpcSpawned(NpcCreate npc)
    {
        Vector3 pos = ToGodot(npc.X, npc.Y, npc.Z);
        if (npc.Untargetable && Figure(npc.ModelId) is null && EffectsOf(npc.ModelId).Count == 0)
        {
            // A marker we have neither a figure nor an effect for: a capsule would stand where the real
            // client shows no creature.
            OnObjectRemoved(npc.ObjectId);
            _hiddenNpcs.Add(npc.ObjectId);
            GD.Print($"[entity] {npc.ObjectId} \"{npc.Name}\": untargetable, no figure for model {npc.ModelId}: not drawn");
            return;
        }
        if (!SpawnEntity(npc.ObjectId, pos, YawFromHeading(npc.HeadingOnly), RealmColor(npc.Flags >> 6), npc.ModelId, npc.Size))
            GD.Print($"[entity] {npc.ObjectId} \"{npc.Name}\": no figure for model {npc.ModelId} (sent {npc.Model}): a capsule");
    }

    private void OnPlayerSpawned(PlayerCreate p)
    {
        _sessionObjects[p.SessionId] = p.ObjectId;
        Vector3 pos = ToGodot(p.X, p.Y, p.Z);
        // Drawn like your own character (its model id's Catacombs figure); without one, a capsule in a
        // brighter tint so other players read as players, not mobs.
        if (!SpawnEntity(p.ObjectId, pos, YawFromHeading(p.HeadingOnly), new Color(0.95f, 0.85f, 0.3f), DaocProtocol.Packets.PlayerModel.Id(p.Model), look: p.Look))
            GD.Print($"[entity] {p.ObjectId} \"{p.Name}\": no figure for model {DaocProtocol.Packets.PlayerModel.Id(p.Model)} (sent {p.Model}): a capsule");
    }

    // Objects whose create we asked for (0xBE), so each is asked for once while in view.
    private readonly HashSet<ushort> _objectsRequested = new();

    private async System.Threading.Tasks.Task RequestObject(ushort objectId)
    {
        try { await _session!.RequestObjectAsync(objectId); }
        catch (System.Exception e) { GD.Print($"[entity] {objectId}: create request failed: {e.Message}"); }
    }

    private void OnObjectRemoved(ushort objectId)
    {
        _objectsRequested.Remove(objectId);
        _equipment.Remove(objectId);
        _worldObjects.Remove(objectId);
        _hiddenNpcs.Remove(objectId);
        if (_entities.Remove(objectId, out Entity? e))
            e.Node.QueueFree();
    }

    private static Color RealmColor(int realm) => realm switch
    {
        1 => new Color(0.6f, 0.2f, 0.2f),   // Albion red
        2 => new Color(0.2f, 0.4f, 0.75f),  // Midgard blue
        3 => new Color(0.3f, 0.6f, 0.3f),   // Hibernia green
        _ => new Color(0.55f, 0.55f, 0.6f), // neutral
    };

    // look: a player (drawn with its Catacombs figure, dressed in its face and hair); null for an NPC.
    // Returns whether it is drawn as a figure (false: a capsule).
    private bool SpawnEntity(ushort objectId, Vector3 pos, float yaw, Color color, ushort model = 0, byte size = 50,
        CharacterLook? look = null)
    {
        // Free any node we already have for this id before replacing it. This happens normally: an
        // entity first seen via a bare 0xA1 update spawns grey (OnObjectMoved), then its 0xDA/0x4B
        // create arrives and re-spawns it realm-coloured. Without freeing the old node it is orphaned
        // in the scene tree — the NPC is drawn twice (a grey ghost left at the old spot, z-fighting
        // the coloured one) until it next moves. Also covers a repeated create for a re-seen id.
        if (_entities.Remove(objectId, out Entity? existing))
            existing.Node.QueueFree();

        var node = new Node3D { Position = pos, Rotation = new Vector3(0, yaw, 0) };
        AnimationPlayer? anim = null;
        Node3D? dressed = null;
        bool figured = false;
        if (model != 0 && ((look is not null ? Figure(model, catacombs: true) : null) ?? Figure(model)) is { } figure)
        {
            // The server's model id is the client's monsters.csv row; mpk figures converted it to
            // models/model<id>.glb. Size 50 is normal; the table's own scale applies on top.
            figured = true;
            var inst = (Node3D)figure.Template.Duplicate();
            float scale = figure.Scale * Mathf.Max(size, 1) / 50f;
            inst.Scale = new Vector3(scale, scale, scale);
            inst.Rotation = new Vector3(0, Mathf.Pi, 0);   // figures face +Z; entities face -Z
            if (look is { } l && ApplyLook(inst, model, l).Length > 0)
            {
                dressed = inst;
                if (_equipment.TryGetValue(objectId, out EquipmentUpdate? gear))
                    Outfit(inst, model, l, gear.Items, gear.HoodUp, gear.CloakHidden, gear.HelmHidden, gear.ActiveWeapons, false);
                else
                    Outfit(inst, model, l, [], false, false, false, 0xFF, false);
            }
            node.AddChild(inst);
            anim = inst.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() as AnimationPlayer;
            AttachEffects(inst, model);
        }
        else if (model != 0 && EffectsOf(model) is { Count: > 0 } effects)
        {
            // A model that is only an effect (OpenDAoC's teleporter marker, model 1923: an empty figure with
            // the "teleporter_ground" ring): the effects alone, at the model's scale and the NPC's size.
            figured = true;
            float scale = effects[0].Scale * Mathf.Max(size, 1) / 50f;
            var holder = new Node3D { Scale = new Vector3(scale, scale, scale), Rotation = new Vector3(0, Mathf.Pi, 0) };
            node.AddChild(holder);
            AttachEffects(holder, model);
        }
        else
        {
            node.AddChild(new MeshInstance3D
            {
                Mesh = new CapsuleMesh { Radius = 0.4f, Height = 2f },
                Position = new Vector3(0, 1, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = color },
            });
        }
        _entityRoot.AddChild(node);
        var entity = new Entity { Node = node, TargetPosition = pos, TargetYaw = yaw, Player = anim, LastPosition = pos,
            Figure = dressed, Model = model, Look = look ?? default };
        _entities[objectId] = entity;
        PlayClip(entity, "idle");
        return figured;
    }

    // A figure's clip, looping; a no-op for a capsule or a clip the figure lacks.
    private static void PlayClip(Entity e, string clip)
    {
        if (e.Player is null || e.Clip == clip || !e.Player.HasAnimation(clip))
            return;
        e.Player.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
        e.Player.Play(clip, customBlend: 0.2);
        e.Clip = clip;
    }

    // Converted figures by model id (null: none for that id), loaded once each and duplicated per
    // entity: classic ones (models/model<id>.glb, listed in figures.csv) and Catacombs ones
    // (models/cata<id>.glb, catacombs.csv), both written by mpk figures.
    private readonly Dictionary<(ushort, bool), (Node3D Template, float Scale, string File)?> _figures = new();
    private Dictionary<int, float>? _figureScales, _cataScales;

    private (Node3D Template, float Scale, string File)? Figure(ushort model, bool catacombs = false)
    {
        if (_figures.TryGetValue((model, catacombs), out var cached))
            return cached;
        (Node3D, float, string)? result = null;
        string lib = string.IsNullOrEmpty(_libraryPath) ? "" : _libraryPath;
        Dictionary<int, float> scales = catacombs
            ? _cataScales ??= ReadFigureScales(lib.PathJoin("models").PathJoin("catacombs.csv"))
            : _figureScales ??= ReadFigureScales(lib.PathJoin("models").PathJoin("figures.csv"));
        // A Catacombs model id is drawn with its race's figure (cata_fig<N>.glb, shared by every model of
        // that race); Dress then puts on the model's own skins.
        _cataFig3 ??= ReadCataFigures(lib.PathJoin("models").PathJoin("catacombs.csv"));
        string file = !catacombs ? $"model{model}.glb"
            : _cataFig3.TryGetValue(model, out int fig3) ? $"cata_fig{fig3}.glb" : "";
        string path = lib.PathJoin("models").PathJoin(file);
        if (lib.Length > 0 && file.Length > 0 && scales.TryGetValue(model, out float scale) && Godot.FileAccess.FileExists(path))
        {
            if (!_figureFiles.TryGetValue(file, out Node3D? template))
            {
                template = ModelTexturing.LoadGlb(path, TexDirs("figures"));
                _figureFiles[file] = template;
            }
            if (template is not null)
                result = (template, scale, file);
        }
        _figures[(model, catacombs)] = result;
        return result;
    }

    // Loaded figure files (null: failed), shared by the model ids drawn with them.
    private readonly Dictionary<string, Node3D?> _figureFiles = new();

    // ----- Catacombs appearance -----

    // fig3 figure number per Catacombs model id (catacombs.csv column 4), read once.
    private Dictionary<int, int>? _cataFig3;

    // The travel window (F6): pick a zone, and the GM jump takes us there. The landing spot where the
    // converter wrote one, else the zone's middle at height 0: outdoors the character is then under the
    // terrain and is put on top of it (Ground), so no height needs knowing in advance.
    private TravelDialog OpenTravel() => TravelDialog.Open(this, ZonesDir, zone =>
    {
        if (_session is null || _closedReason is not null)
        {
            AppendChat("(offline: can't travel)", new Color(0.8f, 0.5f, 0.5f));
            return;
        }
        (float x, float y, float z, string where) = TravelDialog.Target(zone, ZonesDir, 0f);
        string command = System.FormattableString.Invariant($"/jump to {x:0} {y:0} {z:0} {zone.RegionId}");
        AppendChat($"Travelling to {zone.Name} (zone {zone.Id}, {where}): {command}", new Color(0.6f, 0.8f, 1f));
        GD.Print($"[world] travel to zone {zone.Id} {zone.Name}: {command}");
        _ = SendTravel(command);
    });

    // Sends a slash command and reports a failure in the chat log (travel, sprint).
    private async System.Threading.Tasks.Task SendTravel(string command)
    {
        try { await _session!.SendCommandAsync(command); }
        catch (System.Exception e) { AppendChat($"(could not send: {e.Message})", new Color(0.8f, 0.5f, 0.5f)); }
    }

    // Each living's last visible gear (0x15): it can come before or after the player's create packet.
    private readonly Dictionary<ushort, EquipmentUpdate> _equipment = new();

    private void OnEquipmentChanged(EquipmentUpdate gear)
    {
        _equipment[gear.ObjectId] = gear;
        if (_entities.TryGetValue(gear.ObjectId, out Entity? e) && e.Figure is { } figure)
        {
            Outfit(figure, e.Model, e.Look, gear.Items, gear.HoodUp, gear.CloakHidden, gear.HelmHidden, gear.ActiveWeapons, e.InCombat);
            e.CombatIdle = CombatIdle(gear.Items, gear.ActiveWeapons);
        }
    }

    // ----- Attack mode: weapons drawn -----

    // Players' session ids (0xBA names a player by session, the rest by object id), from their 0x4B.
    private readonly Dictionary<ushort, ushort> _sessionObjects = new();

    private async System.Threading.Tasks.Task SendAttack(bool start)
    {
        if (_session is null)
        {
            OnAttackModeChanged(start);   // offline: no server to ask, so just draw or sheathe
            return;
        }
        try { await _session.AttackAsync(start); }
        catch (System.Exception e) { AppendChat($"(could not send: {e.Message})", new Color(0.8f, 0.5f, 0.5f)); }
    }

    // Our attack mode, as the server has it: draw or sheathe the weapons, and stand in the weapon's combat idle.
    private void OnAttackModeChanged(bool on)
    {
        if (on == _attacking)
            return;
        _attacking = on;
        GD.Print($"[world] attack mode {(on ? "on: weapons drawn" : "off: weapons put away")}");
        if (_ownFigure is not null && IsInstanceValid(_ownFigure))
            Regear(_ownFigure, PlayerModel, PlayerWorn, PlayerActiveWeapons, false, PlayerHoodUp, on);
    }

    // Another player moved (S→C 0xA9): its position, facing and attack mode.
    private void OnPlayerMoved(PlayerPosition p)
    {
        if (p.ObjectId == _world.Position.ObjectId)
            return;
        _sessionObjects[p.SessionId] = p.ObjectId;
        if (!_entities.TryGetValue(p.ObjectId, out Entity? e))
            return;   // not seen created yet: its 0x4B will place it
        e.TargetPosition = ToGodot(p.X, p.Y, p.Z);
        e.TargetYaw = YawFromHeading(p.HeadingOnly);
        SetCombat(p.ObjectId, e, p.InCombat);
    }

    // Another player turned, or its state changed standing still (0xBA).
    private void OnPlayerTurned(PlayerHeading h)
    {
        if (!_sessionObjects.TryGetValue(h.SessionId, out ushort objectId) || !_entities.TryGetValue(objectId, out Entity? e))
            return;
        e.TargetYaw = YawFromHeading(h.HeadingOnly);
        SetCombat(objectId, e, h.InCombat);
    }

    private void SetCombat(ushort objectId, Entity e, bool on)
    {
        if (e.InCombat == on)
            return;
        e.InCombat = on;
        if (e.Figure is { } figure && _equipment.TryGetValue(objectId, out EquipmentUpdate? gear))
            Regear(figure, e.Model, gear.Items, gear.ActiveWeapons, gear.HelmHidden, gear.HoodUp, on);
    }

    // Re-hangs a figure's weapons (FigureLook.Gear) for attack mode on or off; the look and armour stay.
    private void Regear(Node3D figure, ushort model, IReadOnlyList<WornItem> worn, byte activeWeapons, bool helmHidden,
        bool hoodUp, bool drawn)
    {
        string lib = _libraryPath ?? "";
        _cataFig3 ??= ReadCataFigures(lib.PathJoin("models").PathJoin("catacombs.csv"));
        if (_cataFig3.TryGetValue(model, out int fig3))
            FigureLook.Gear(figure, lib.PathJoin("models").PathJoin($"fig3_{fig3}.json"), lib.PathJoin("models").PathJoin("cata_gear.json"), lib.PathJoin("models").PathJoin("colours.json"),
                lib.PathJoin("models"), worn, activeWeapons, helmHidden, hoodUp, drawn, TexDirs("items"));
    }

    private string CombatIdle(IReadOnlyList<WornItem> worn, byte activeWeapons) =>
        FigureLook.CombatIdle((_libraryPath ?? "").PathJoin("models").PathJoin("cata_gear.json"), worn, activeWeapons);

    // Standing still: the idle, or in attack mode the weapon's combat idle (else the one-hand one; a figure
    // converted before the combat clips existed has neither and keeps its idle).
    private static string Idle(AnimationPlayer? player, bool combat, string combatIdle) =>
        !combat || player is null ? "idle"
        : player.HasAnimation(combatIdle) ? combatIdle
        : player.HasAnimation("combat_idle") ? "combat_idle" : "idle";

    // A player's Catacombs figure in full: its look (face, hair), its armour and cloak, then what it holds
    // and wears on its head. The look first, so a helm taken off gives the hair back.
    private string Outfit(Node3D figure, ushort model, CharacterLook look, IReadOnlyList<WornItem> worn,
        bool hoodUp, bool cloakHidden, bool helmHidden, byte activeWeapons, bool drawn)
    {
        string lib = _libraryPath ?? "";
        string text = ApplyLook(figure, model, look) + Dress(figure, model, look, worn, hoodUp, cloakHidden);
        if (_cataFig3 is not null && _cataFig3.TryGetValue(model, out int fig3))
            text += FigureLook.Gear(figure, lib.PathJoin("models").PathJoin($"fig3_{fig3}.json"), lib.PathJoin("models").PathJoin("cata_gear.json"), lib.PathJoin("models").PathJoin("colours.json"),
                lib.PathJoin("models"), worn, activeWeapons, helmHidden, hoodUp, drawn, TexDirs("items"));
        return text;
    }

    // Dresses a player's Catacombs figure in worn items (FigureLook.Dress); a description for the log.
    private string Dress(Node3D figure, ushort model, CharacterLook look, IReadOnlyList<WornItem> worn, bool hoodUp, bool cloakHidden)
    {
        string lib = _libraryPath ?? "";
        _cataFig3 ??= ReadCataFigures(lib.PathJoin("models").PathJoin("catacombs.csv"));
        return _cataFig3.TryGetValue(model, out int fig3)
            ? FigureLook.Dress(figure, lib.PathJoin("models").PathJoin($"fig3_{fig3}.json"), lib.PathJoin("models").PathJoin("cata_items.json"),
                lib.PathJoin("models").PathJoin("cata_models.json"), lib.PathJoin("models").PathJoin("colours.json"), model, look, worn, hoodUp, cloakHidden,
                TexDirs("items"))
            : "";
    }

    // Dresses a player's Catacombs figure in its appearance (FigureLook); a description for the log.
    private string ApplyLook(Node3D figure, ushort model, CharacterLook look)
    {
        string lib = _libraryPath ?? "";
        _cataFig3 ??= ReadCataFigures(lib.PathJoin("models").PathJoin("catacombs.csv"));
        return _cataFig3.TryGetValue(model, out int fig3)
            ? FigureLook.Apply(figure, lib.PathJoin("models").PathJoin($"fig3_{fig3}.json"), look,
                TexDirs("figures"))
            : "";
    }

    // models/figures.csv and catacombs.csv (written by mpk figures; DaocAssets.Figures.FigureTable).
    private static Dictionary<int, DaocAssets.Figures.FigureRow> ReadFigureTable(string path) =>
        Godot.FileAccess.FileExists(path) ? DaocAssets.Figures.FigureTable.Read(Godot.FileAccess.GetFileAsString(path)) : new();

    // catacombs.csv's fig3 per model id: the race's options file number.
    internal static Dictionary<int, int> ReadCataFigures(string path) =>
        ReadFigureTable(path).Values.Where(r => r.Fig3 is not null).ToDictionary(r => r.Id, r => r.Fig3!.Value);

    // ----- Model effects (monsters.csv "Mon Effect": glows, auras, the teleporter's ground ring) -----

    // models/effects.csv (model, scale, effect, attach, range), read once: the effects per model id.
    private Dictionary<int, List<(string Effect, string Attach, float Scale)>>? _effectTable;

    private IReadOnlyList<(string Effect, string Attach, float Scale)> EffectsOf(ushort model)
    {
        if (_effectTable is null)
        {
            _effectTable = new();
            string path = (_libraryPath ?? "").PathJoin("models").PathJoin("effects.csv");
            if (!string.IsNullOrEmpty(_libraryPath) && Godot.FileAccess.FileExists(path))
                foreach (string line in Godot.FileAccess.GetFileAsString(path).Split('\n').Skip(1))
                {
                    string[] c = line.Trim().Split(',');
                    if (c.Length >= 4 && int.TryParse(c[0], out int id)
                        && float.TryParse(c[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sc))
                    {
                        if (!_effectTable.TryGetValue(id, out var list))
                            _effectTable[id] = list = new();
                        list.Add((c[2], c[3], sc));
                    }
                }
        }
        return _effectTable.TryGetValue(model, out var found) ? found : [];
    }

    // Hangs the model's effects on the figure: on the bone they name (following its animation), else on a node
    // of that name, else on the figure's root. An effect file that failed to load is skipped.
    private void AttachEffects(Node3D figure, ushort model)
    {
        string lib = _libraryPath ?? "";
        foreach ((string effect, string attach, _) in EffectsOf(model))
        {
            string file = effect + ".glb";
            if (!_figureFiles.TryGetValue(file, out Node3D? template))
            {
                string path = lib.PathJoin("models").PathJoin(file);
                template = Godot.FileAccess.FileExists(path)
                    ? ModelTexturing.LoadGlb(path, TexDirs("effects")) : null;
                _figureFiles[file] = template;
            }
            if (template is null)
                continue;
            var fx = (Node3D)template.Duplicate();
            fx.Name = "Effect_" + effect;
            if (figure.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>()
                    .FirstOrDefault(s => s.FindBone(attach) >= 0) is { } skeleton)
            {
                var bone = new BoneAttachment3D { Name = "EffectOn_" + attach, BoneName = attach };
                skeleton.AddChild(bone);
                bone.AddChild(fx);
            }
            else if (figure.FindChild(attach, true, false) is Node3D node)
                node.AddChild(fx);
            else
                figure.AddChild(fx);
        }
    }

    // Texture folders for one kind of model, in search order: overrides, the kind's own converted copies
    // (textures/figures, textures/items, textures/effects: the install's file of that name where it differs
    // from the zone one), the shared textures, the library. Items fall back to the figures' copies.
    private string[] TexDirs(string scope)
    {
        string lib = _libraryPath ?? "";
        string tex = lib.PathJoin("textures");
        return scope == "items"
            ? [lib.PathJoin("overrides"), tex.PathJoin("items"), tex.PathJoin("figures"), tex, lib]
            : [lib.PathJoin("overrides"), tex.PathJoin(scope), tex, lib];
    }

    private static Dictionary<int, float> ReadFigureScales(string path) =>
        ReadFigureTable(path).ToDictionary(kv => kv.Key, kv => kv.Value.Scale);

    // Ease each entity toward the last position/facing the server reported. The per-NPC update rate
    // is low (~1 Hz in the capture), so without this they teleport; with it they glide. The rate is
    // frame-rate independent (exponential smoothing). Yaw lerps the short way around the circle.
    private void InterpolateEntities(double delta)
    {
        float t = Mathf.Min(1f, (float)delta * 8f);
        foreach (Entity e in _entities.Values)
        {
            e.Age += delta;
            e.Node.Position = e.Node.Position.Lerp(e.TargetPosition, t);
            float yaw = e.Node.Rotation.Y;
            e.Node.Rotation = new Vector3(0, yaw + Mathf.AngleDifference(yaw, e.TargetYaw) * t, 0);

            // The clip follows how fast the figure is gliding (metres per second, smoothed): DAoC's
            // walk is about 2 m/s, its run about 4.8.
            if (e.Player is not null && delta > 0)
            {
                Vector3 step = e.Node.Position - e.LastPosition;
                step.Y = 0;
                e.Speed = Mathf.Lerp(e.Speed, step.Length() / (float)delta, Mathf.Min(1f, (float)delta * 4f));
                e.LastPosition = e.Node.Position;
                PlayClip(e, e.Speed > 3.2f ? "run" : e.Speed > 0.4f ? "walk" : Idle(e.Player, e.InCombat, e.CombatIdle));
            }
        }
    }
}
