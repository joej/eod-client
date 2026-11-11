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

    // Static world objects (0xD9: doors, items, banners), by object id: drawn by their model (WorldView.Objects), not as
    // entities, and a position update for one must not turn it into a capsule.
    private readonly Dictionary<ushort, ObjectCreate> _worldObjects = new();

    // OpenDAoC's model for a creature it has hidden (HideableNpc.HiddenModel).
    private const ushort HiddenModel = 1;

    // Untargetable NPCs with no figure (markers and effects, e.g. a teleporter's light node): not drawn,
    // and kept here so their position updates don't spawn a grey capsule either.
    private readonly HashSet<ushort> _hiddenNpcs = new();

    private void OnWorldObject(ObjectCreate o)
    {
        _worldObjects[o.ObjectId] = o;
        DrawWorldObject(o);
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
            e.Health = u.HealthPercent;
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
        // OpenDAoC hides a creature without removing it (HideableNpc: a night spawn by day) by sending it as
        // model 1, untargetable, name hidden and peaceful, and sends it again with its real model when it shows.
        // Model 1 is the client table's "Demon": drawn, the hidden zombie boars of Camelot Hills stood in a field
        // as red demons (owner, 2026-11-06).
        bool serverHidden = npc.ModelId == HiddenModel && npc.Untargetable;
        if (serverHidden || npc.Untargetable && Figure(npc.ModelId) is null && EffectsOf(npc.ModelId).Count == 0)
        {
            // A marker we have neither a figure nor an effect for: a capsule would stand where the real
            // client shows no creature.
            OnObjectRemoved(npc.ObjectId);
            _hiddenNpcs.Add(npc.ObjectId);
            GD.Print($"[entity] {npc.ObjectId} \"{npc.Name}\": " + (serverHidden
                ? "hidden by the server (model 1, untargetable): not drawn"
                : $"untargetable, no figure for model {npc.ModelId}: not drawn"));
            return;
        }
        // Shown again (a night spawn at nightfall): its position updates count once more.
        _hiddenNpcs.Remove(npc.ObjectId);
        bool drawn = SpawnEntity(npc.ObjectId, pos, YawFromHeading(npc.HeadingOnly), RealmColor(npc.Flags >> 6), npc.ModelId, npc.Size,
            pickable: !npc.Untargetable);
        if (_entities.TryGetValue(npc.ObjectId, out Entity? spawned))
        {
            // OpenDAoC's server has no terrain: a creature wandering over a hill keeps its spawn height and floats or
            // sinks (owner's goblin fishermen, Black Mtns. South, 2026-11-10). Unless it flies or swims, it stands on
            // the ground under where the server puts it (GroundEntities).
            spawned.Grounded = !npc.Flying && !npc.Swimming;
            spawned.ProbedAt = new Vector3(float.NaN, 0, 0);
            SetQuestGlyph(spawned, npc.Quest);
            spawned.Name = npc.Name;
            NameNpc(spawned, npc);
        }
        if (!drawn)
            GD.Print($"[entity] {npc.ObjectId} \"{npc.Name}\": no figure for model {npc.ModelId} (sent {npc.Model}): a capsule");
    }

    private void OnPlayerSpawned(PlayerCreate p)
    {
        _sessionObjects[p.SessionId] = p.ObjectId;
        Vector3 pos = ToGodot(p.X, p.Y, p.Z);
        // Drawn like your own character (its model id's Catacombs figure); without one, a capsule in a
        // brighter tint so other players read as players, not mobs.
        bool drawn = SpawnEntity(p.ObjectId, pos, YawFromHeading(p.HeadingOnly), new Color(0.95f, 0.85f, 0.3f), DaocProtocol.Packets.PlayerModel.Id(p.Model), look: p.Look);
        if (_entities.TryGetValue(p.ObjectId, out Entity? spawned))
        {
            spawned.Name = p.Name;
            NamePlayer(spawned, p);
        }
        if (!drawn)
            GD.Print($"[entity] {p.ObjectId} \"{p.Name}\": no figure for model {DaocProtocol.Packets.PlayerModel.Id(p.Model)} (sent {p.Model}): a capsule");
    }

    // Players we heard from but never saw created: asked for by session id (0xD5, as the server expects of a client), again
    // after 5 s if the create still hasn't come (out of range then, or gone).
    private readonly Dictionary<ushort, double> _playersRequested = new();

    private void RequestPlayer(ushort sessionId)
    {
        if (_session is null || _closedReason is not null || sessionId == 0
            || (_playersRequested.TryGetValue(sessionId, out double at) && _seconds - at < 5))
            return;
        _playersRequested[sessionId] = _seconds;
        GD.Print($"[entity] player session {sessionId}: heard from but never created, create requested");
        _ = Send(() => _session.RequestPlayerAsync(sessionId), "player create request");
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
        RemoveWorldObjectNode(objectId);
        _hiddenNpcs.Remove(objectId);
        if (_entities.Remove(objectId, out Entity? e))
            e.Node.QueueFree();
        ForgetTarget(objectId);
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
        CharacterLook? look = null, bool pickable = true)
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
        if (pickable)
            AddPickShape(node, objectId, size);
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
        {
            RequestPlayer(p.SessionId);   // not seen created yet: ask for its 0x4B, which places it
            return;
        }
        e.TargetPosition = ToGodot(p.X, p.Y, p.Z);
        e.TargetYaw = YawFromHeading(p.HeadingOnly);
        e.Health = p.HealthPercent;
        SetCombat(p.ObjectId, e, p.InCombat);
    }

    // Another player turned, or its state changed standing still (0xBA).
    private void OnPlayerTurned(PlayerHeading h)
    {
        if (!_sessionObjects.TryGetValue(h.SessionId, out ushort objectId) || !_entities.TryGetValue(objectId, out Entity? e))
        {
            RequestPlayer(h.SessionId);
            return;
        }
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
                fx.Transform = FromGltf;   // a bone's frame is the NIF's (units, Z up); the effect was converted to glTF
                bone.AddChild(fx);
            }
            else if (figure.FindChild(attach, true, false) is Node3D node)
                node.AddChild(fx);
            else
                figure.AddChild(fx);
        }
    }

    // Undoes the converter's NIF→glTF transform (units to metres, Z up to Y up) for a converted model hung on a figure's
    // bone, whose frame stays the NIF's: as the gear's frames do (cata_gear.json, WriteCataGear).
    private static readonly Transform3D FromGltf = MakeFromGltf();

    private static Transform3D MakeFromGltf()
    {
        System.Numerics.Matrix4x4.Invert(DaocAssets.Nif.GltfExporter.NifToGltfMatrix, out var m);
        return new Transform3D(new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33)),
            new Vector3(m.M41, m.M42, m.M43));
    }

    // ----- Quest glyphs (the knot over a quest giver's head) -----

    // models/quest.csv (state, effect model, attach node), read once.
    private Dictionary<string, (string Effect, string Attach)>? _questGlyphs;

    // Puts the glyph for `quest` over the entity (replacing any it has), or takes it away for None. It hangs on the
    // figure's helmet bone (where the client's table attaches it), else its head, else above the entity.
    private void SetQuestGlyph(Entity e, QuestIndicator quest)
    {
        if (e.QuestGlyph is { } old)
        {
            old.QueueFree();
            e.QuestGlyph = null;
        }
        if (quest == QuestIndicator.None)
            return;
        string lib = _libraryPath ?? "";
        if (_questGlyphs is null)
        {
            _questGlyphs = new();
            string path = lib.PathJoin("models").PathJoin("quest.csv");
            if (lib.Length > 0 && Godot.FileAccess.FileExists(path))
                foreach (string line in Godot.FileAccess.GetFileAsString(path).Split('\n').Skip(1))
                    if (line.Trim().Split(',') is { Length: >= 3 } c)
                        _questGlyphs[c[0]] = (c[1], c[2]);
        }
        if (!_questGlyphs.TryGetValue(quest.ToString().ToLowerInvariant(), out var glyph))
            return;
        string file = glyph.Effect + ".glb";
        if (!_figureFiles.TryGetValue(file, out Node3D? template))
        {
            string path = lib.PathJoin("models").PathJoin(file);
            template = Godot.FileAccess.FileExists(path) ? ModelTexturing.LoadGlb(path, TexDirs("effects")) : null;
            _figureFiles[file] = template;
        }
        if (template is null)
            return;
        var node = (Node3D)template.Duplicate();
        node.Name = "QuestGlyph";
        // Placed as retail shows it (Wicoessa's gold knot, Astoissi's cyan one): upright, centred 0.9 m over
        // the head, room for the name between. The files don't say how retail gets there: hung on the helmet frame as stored, the knot (under a
        // node flipped 180 degrees, "Dummy01") would sit a metre below it. Their small glow cards are billboards
        // authored at other offsets, so they are left out.
        var box = new Aabb();
        bool any = false;
        foreach (MeshInstance3D mi in node.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
        {
            if (mi.Mesh is null || mi.Mesh.GetFaces().Length <= 6)
            {
                mi.Visible = false;
                continue;
            }
            Aabb b = mi.Transform * mi.GetAabb();
            box = any ? box.Merge(b) : b;
            any = true;
        }
        node.Position = HeadHeight(e) + new Vector3(0, 0.9f, 0) - (any ? box.GetCenter() : Vector3.Zero);
        e.Node.AddChild(node);
        e.QuestGlyph = node;
    }

    // Where an entity's head is, in its own node's space: the figure's helmet or head bone at spawn, else 2 m up.
    private static Vector3 HeadHeight(Entity e) => HeadHeight(e.Node);

    private static Vector3 HeadHeight(Node3D root)
    {
        foreach (Skeleton3D sk in root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>())
            foreach (string name in new[] { "Bip01 Helmet", "Bip01 Head" })
                if (sk.FindBone(name) is int bone and >= 0)
                    return (root.GlobalTransform.AffineInverse() * sk.GlobalTransform * sk.GetBoneGlobalPose(bone)).Origin;
        return new Vector3(0, 2f, 0);
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

    // The ground under each grounded NPC, probed in the physics frame (rays are only reliable there) whenever the server
    // moves it: the first floor or terrain from a little above its reported height down; when there is none (it was
    // sent under the ground), the terrain from high above. Without either (the zone not loaded yet), its own height.
    private void GroundEntities()
    {
        foreach (Entity e in _entities.Values)
        {
            if (!e.Grounded)
            {
                e.GroundY = null;
                continue;
            }
            Vector3 at = e.TargetPosition;
            if (Mathf.Abs(at.X - e.ProbedAt.X) < 0.05f && Mathf.Abs(at.Z - e.ProbedAt.Z) < 0.05f && Mathf.Abs(at.Y - e.ProbedAt.Y) < 0.05f)
                continue;
            float? y = GroundRay(at + Vector3.Up * 3f, at + Vector3.Down * 40f, ~NotSolid)
                       ?? GroundRay(new Vector3(at.X, at.Y + 80f, at.Z), at, TerrainLayer);
            if (y is null)
                continue;   // nothing loaded under it yet: probe again next frame
            // Over water (a water surface above the ground): it swims, as creatures the server didn't flag do (water snakes
            // and eels were pulled 25 m down to the riverbed, 2026-11-10). Its own height, kept between the bed and the surface.
            if (GroundRay(new Vector3(at.X, at.Y + 40f, at.Z), new Vector3(at.X, y.Value, at.Z), WaterLayer) is float water
                && water > y.Value)
                y = Mathf.Clamp(at.Y, y.Value, water);
            if (float.IsNaN(e.ProbedAt.X) && Mathf.Abs(y.Value - at.Y) > 1f)
                GD.Print($"[entity] \"{e.Name}\" stood on the ground: {(y.Value - at.Y) / RegionMetresPerUnit:+0;-0} units from the server's height");
            (e.GroundY, e.ProbedAt) = (y, at);
        }
    }

    private float? GroundRay(Vector3 from, Vector3 to, uint mask)
    {
        var ray = PhysicsRayQueryParameters3D.Create(from, to);
        ray.CollisionMask = mask;
        ray.HitBackFaces = false;   // not a bridge's underside
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : null;
    }

    // Ease each entity toward the last position/facing the server reported. The per-NPC update rate
    // is low (~1 Hz in the capture), so without this they teleport; with it they glide. The rate is
    // frame-rate independent (exponential smoothing). Yaw lerps the short way around the circle.
    private void InterpolateEntities(double delta)
    {
        float t = Mathf.Min(1f, (float)delta * 8f);
        foreach (Entity e in _entities.Values)
        {
            e.Age += delta;
            Vector3 target = e.GroundY is float gy ? new Vector3(e.TargetPosition.X, gy, e.TargetPosition.Z) : e.TargetPosition;
            e.Node.Position = e.Node.Position.Lerp(target, t);
            float yaw = e.Node.Rotation.Y;
            e.Node.Rotation = new Vector3(0, yaw + Mathf.AngleDifference(yaw, e.TargetYaw) * t, 0);

            // The clip follows how fast the figure is gliding (metres per second, smoothed). Retail runs a figure above OUR
            // max speed (0xB6, read as units/s: normally 100, 2.5 m/s), for every figure, so the line rises with our speed
            // buffs (daoc-re RE/82, correcting RE/77).
            float runAbove = (_speedPercent > 0 ? _speedPercent : 100) * RegionMetresPerUnit;
            if (e.Player is not null && delta > 0 && !e.Dead)
            {
                Vector3 step = e.Node.Position - e.LastPosition;
                step.Y = 0;
                e.Speed = Mathf.Lerp(e.Speed, step.Length() / (float)delta, Mathf.Min(1f, (float)delta * 4f));
                e.LastPosition = e.Node.Position;
                PlayClip(e, e.Speed > runAbove ? "run" : e.Speed > 0.4f ? "walk" : Idle(e.Player, e.InCombat, e.CombatIdle));
            }
        }
    }
}
