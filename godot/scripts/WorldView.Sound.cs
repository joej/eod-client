// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: what the world sounds like (roadmap M12; docs/sound.md, daoc-re 2026-11-08). GameAudio plays; this decides:
//   - entering a region plays its entry music (zones.dat entry_music, a cc_mp3s id; RegionMusic);
//   - the zone's ambience (its sounds.dat; DaocAssets.Sound.Ambience) at the character's position and the game time;
//   - S→C 0xEF type 2: the area's music (cc_music row); type 1: the realm's level-up fanfare (CC_events "Raise Level");
//   - S→C 0xD3: a crafting result's sound, or a cc_sounds id; 0xC9: a cc_sounds id, at its spot when it has a radius;
//   - S→C 0x99: the door's open or close sound (cc_doors row = the packet's sound byte), at the door;
//   - attack mode on: combat music (mp3_Combat1 or 2 at random; not in region 27, the tutorial, nor while it already
//     plays); attack mode off or your own death fades it out;
//   - S→C 0xBC: at the attacker as the attack starts, its weapon's "use" (cc_weapons column 5, 6 for a style) or, with no
//     weapon, its creature's "attack vocal" (cc_creatures column 0); at the defender at the hit time, the weapon's
//     "parried" (16) or "blocked" (17), or on a hit its "hits body" light, medium or styled (9, 10, 11) and the defender's
//     pain light, medium or heavy (creatures 1, 2, 3), light below 10 % of health lost. With no weapon the weapon row is
//     the attacker's creature column 14 ("unarmed set"). Weapons of stance 5, 6 and 8 make no attack sound;
//   - a death (0xAE, or 0xA2 type 0 without one): the creature's "death vocal" (column 4), 2D for your own;
//   - footsteps: the creature's "move on dirt" (column 7) for each step on the ground; wading steps (cc_sounds
//     994–997) and swim strokes (1002–1005) for your own; entering water (19) and leaving it (221).
// Ours: the weather is always clear (no 0x92 yet), the character is never under water for ambience, and 0xD3 type 3
// plays 2D (its position's layout isn't known). Ours until daoc-re answers how retail times them: a step every half of
// the figure's run or walk stride travelled (strides.csv), a swim stroke every half run stride, footsteps for every
// figure on the ground within its sound's r2.

using DaocAssets.Sound;
using DaocAssets.Zone;
using DaocProtocol.Packets;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const ushort TutorialRegion = 27;
    private Ambience? _ambience;
    private ushort _ambienceZone = ushort.MaxValue;
    private readonly Dictionary<(int Region, int Line), GameAudio.Voice> _ambientLoops = [];
    private readonly Dictionary<(int Region, int Line), GameAudio.Voice> _ambientShots = [];
    private bool _combatMusic;
    private static IniFile? _zonesDat;
    private static SoundSets? _soundSets;
    private const int AttackVocal = 0, PainLight = 1, PainMedium = 2, PainHeavy = 3, DeathVocal = 4, MoveOnDirt = 7, UnarmedSet = 14;
    private const int WeaponUse = 5, WeaponStyleUse = 6, HitLight = 9, HitMedium = 10, HitStyled = 11, WeaponParried = 16, WeaponBlocked = 17;
    private const int EnterWater = 19, LeaveWater = 221;
    private float _stepTravel;
    private Vector3 _stepFrom;
    private bool _wasSwimming;
    private readonly Dictionary<ushort, (Vector3 From, float Travel)> _entitySteps = [];

    private static GameAudio? Audio => GameAudio.Instance;

    private void StartSound()
    {
        if (Audio is not GameAudio audio)
            return;
        audio.Listener = _camera;
        _bridge.RegionSoundArrived += OnRegionSound;
        _bridge.SoundPlayed += OnPlaySound;
        _bridge.SoundEffectArrived += OnSoundEffect;
        _bridge.PlayerDied += d =>
        {
            if (d.VictimId == _world.Position.ObjectId)
                CombatMusic(false);
            DeathSound(d.VictimId);
        };
        string sets = System.IO.Path.Combine(_libraryPath ?? "", "sounds", "soundsets.csv");
        if (_soundSets is null && System.IO.File.Exists(sets))
            _soundSets = SoundSets.FromCsv(System.IO.File.ReadAllText(sets));
        _stepFrom = _body.Position;
        TreeExiting += StopAmbience;
        PlayEntryMusic(audio);
    }

    private void PlayEntryMusic(GameAudio audio)
    {
        string datPath = System.IO.Path.Combine(_libraryPath ?? "", "sounds", "zones.dat");
        if (_zonesDat is null && System.IO.File.Exists(datPath))
            _zonesDat = IniFile.Parse(System.IO.File.ReadAllText(datPath, System.Text.Encoding.Latin1));
        ushort region = _world.Position.RegionId;
        if (_zonesDat is not null && RegionMusic.EntryMusic(_zonesDat, region) is int id && audio.Tables.Mp3Name(id) is string music)
            audio.PlayMusic(music, restart: true);
        else
            audio.StopMusic();
    }

    private void OnRegionSound(RegionSound s)
    {
        if (Audio is not GameAudio audio)
            return;
        if (s.Type == RegionSound.AreaMusic && audio.Tables.Music.Get(s.Value, 0) is string music)
            audio.PlayMusic(music);
        else if (s.Type == RegionSound.LevelUp && audio.Tables.Events.Get(s.Value, 0) is string fanfare)
            audio.Play(fanfare);
        GD.Print($"[sound] 0xEF type {s.Type} value {s.Value}");
    }

    private void OnPlaySound(PlaySound s)
    {
        ushort? id = s.Type == PlaySound.Craft ? PlaySound.CraftSound(s.Id) : s.Id;
        if (id is ushort sound)
            Audio?.Play(sound);
    }

    private void OnSoundEffect(SoundEffect s)
    {
        Vector3? at = null;
        if (s.Positional && Zones.All.FirstOrDefault(z => z.Id == s.Zone) is Zone zone)
            at = ToGodot(zone.X + s.X, zone.Y + s.Y, s.Z);
        Audio?.Play(s.SoundId, at);
    }

    // A door's open or close sound, by the 0x99 sound byte (cc_doors row), at the door.
    private void DoorSound(DoorState state, Node3D model, bool open)
    {
        if (Audio is GameAudio audio && audio.Tables.Doors.Get(state.Sound, open ? 0 : 1) is string sound)
            audio.Play(sound, model.GlobalPosition);
    }

    private void CombatMusic(bool on)
    {
        if (Audio is not GameAudio audio)
            return;
        if (on && !_combatMusic && _world.Position.RegionId != TutorialRegion)
        {
            _combatMusic = true;
            audio.PlayMusic(Random.Shared.Next(2) == 0 ? "mp3_Combat1" : "mp3_Combat2");
        }
        else if (!on && _combatMusic)
        {
            _combatMusic = false;
            if (audio.MusicName is "mp3_Combat1" or "mp3_Combat2")
                audio.StopMusic(3f);
        }
    }

    // --- figures: combat, deaths, steps ----------------------------------------------------------------------------------

    private ushort ModelOf(ushort id) =>
        id == SelfId ? (ushort)PlayerModel : _entities.TryGetValue(id, out Entity? e) ? e.Model : (ushort)0;

    private Vector3? WhereIs(ushort id) =>
        id == SelfId ? _body.Position : _entities.TryGetValue(id, out Entity? e) && IsInstanceValid(e.Node) ? e.Node.Position : null;

    private string? CreatureSound(ushort id, int column) =>
        Audio is GameAudio audio && _soundSets?.Creature(ModelOf(id)) is int row ? audio.Tables.Creatures.Get(row, column) : null;

    // Plays a figure's sound at it (2D for your own character: it is where you hear from).
    private void FigureSound(ushort id, string? sound)
    {
        if (sound is null || Audio is not GameAudio audio)
            return;
        if (id == SelfId)
            audio.Play(sound);
        else if (WhereIs(id) is Vector3 at)
            audio.Play(sound, at);
    }

    // S→C 0xBC's sounds; health is the defender's before this packet (null: unknown).
    private void CombatSound(CombatAnimation c, int? healthBefore)
    {
        if (Audio is not GameAudio audio || _soundSets is null || c.AttackerId == 0)
            return;
        (int Row, int Stance)? weapon = c.WeaponModel != 0 ? _soundSets.Weapon(c.WeaponModel) : null;
        int? weaponRow = weapon?.Row;
        if (c.WeaponModel == 0 && int.TryParse(CreatureSound(c.AttackerId, UnarmedSet), out int unarmed))
            weaponRow = unarmed;
        bool styled = c.Result == CombatAnimation.StyledHit;
        if (c.Swings)
        {
            if (c.WeaponModel == 0)
                FigureSound(c.AttackerId, CreatureSound(c.AttackerId, AttackVocal));
            else if (weapon is { } w && SoundSets.HasAttackSound(w.Stance))
                FigureSound(c.AttackerId, audio.Tables.Weapons.Get(w.Row, styled ? WeaponStyleUse : WeaponUse));
        }
        string? impact = null, pain = null;
        if (weaponRow is int wr)
            impact = c.Result switch
            {
                CombatAnimation.Parried => audio.Tables.Weapons.Get(wr, WeaponParried),
                CombatAnimation.Blocked => audio.Tables.Weapons.Get(wr, WeaponBlocked),
                _ when c.IsHit => audio.Tables.Weapons.Get(wr, styled ? HitStyled : Light(c, healthBefore) ? HitLight : HitMedium),
                _ => null,
            };
        if (c.IsHit)
            pain = CreatureSound(c.DefenderId, styled ? PainHeavy : Light(c, healthBefore) ? PainLight : PainMedium);
        if (impact is null && pain is null)
            return;
        ushort defender = c.DefenderId;
        GetTree().CreateTimer(HitSeconds).Timeout += () =>
        {
            FigureSound(defender, impact);
            FigureSound(defender, pain);
        };
    }

    // Under 10 % of the defender's health lost: a light hit.
    private static bool Light(CombatAnimation c, int? before) => before is not int b || b - c.DefenderHealth < 10;

    private void DeathSound(ushort id) => FigureSound(id, CreatureSound(id, DeathVocal));

    // Footsteps, swim strokes, and going into and out of the water.
    private void UpdateSteps()
    {
        if (Audio is not GameAudio audio)
            return;
        if (_swimming != _wasSwimming)
        {
            _wasSwimming = _swimming;
            audio.Play(_swimming ? EnterWater : LeaveWater);
        }
        Vector3 at = _body.Position;
        float moved = new Vector2(at.X - _stepFrom.X, at.Z - _stepFrom.Z).Length() / RegionMetresPerUnit;
        _stepFrom = at;
        bool onFoot = !_airborne && !_climbing && !_dead;
        if (moved > 0 && moved < 200 && (onFoot || _swimming))
        {
            _stepTravel += moved;
            DaocAssets.Figures.Strides? strides = StridesOf((ushort)PlayerModel, catacombs: true);
            bool running = _runningForward;
            float step = (_swimming || running ? strides?.Run ?? 200 : strides?.Walk ?? 100) / 2f;
            if (_stepTravel >= step)
            {
                _stepTravel = 0;
                if (_swimming)
                    audio.Play(1002 + Random.Shared.Next(4));
                else if (_wading)
                    audio.Play(994 + Random.Shared.Next(4));
                else
                    audio.Play(CreatureSound(SelfId, MoveOnDirt) ?? "");
            }
        }
        foreach ((ushort id, Entity e) in _entities)
        {
            if (!IsInstanceValid(e.Node) || e.Dead)
                continue;
            Vector3 p = e.Node.Position;
            (Vector3 from, float travel) = _entitySteps.GetValueOrDefault(id, (p, 0f));
            float d = new Vector2(p.X - from.X, p.Z - from.Z).Length() / RegionMetresPerUnit;
            if (d > 200 || !e.Grounded)
                d = 0;
            travel += d;
            float stride = (Math.Abs(e.MoveSpeed) > 150 ? e.Strides?.Run ?? 200 : e.Strides?.Walk ?? 100) / 2f;
            if (travel >= stride)
            {
                travel = 0;
                if (p.DistanceTo(_body.Position) < 1000 * RegionMetresPerUnit && CreatureSound(id, MoveOnDirt) is string sound)
                    audio.Play(sound, p);
            }
            _entitySteps[id] = (p, travel);
        }
        if (_entitySteps.Count > _entities.Count + 32)
            foreach (ushort gone in _entitySteps.Keys.Where(k => !_entities.ContainsKey(k)).ToList())
                _entitySteps.Remove(gone);
    }

    // Each frame: the zone's ambience where the character stands.
    private void UpdateSound(double delta)
    {
        if (Audio is not GameAudio audio)
            return;
        UpdateSteps();
        Zone zone = _world.Zone;
        if (zone.Id != _ambienceZone)
            LoadAmbience(zone.Id);
        if (_ambience is null)
            return;
        (float x, float y, _) = ToRegion(_body.Position);
        int hhmm = GameMillisOfDay is uint ms ? (int)(ms / 3_600_000 * 100 + ms / 60_000 % 60) : 1200;
        const int clear = 1;
        const bool underwater = false;
        foreach (AmbientShot shot in _ambience.Step(delta, x - zone.X, y - zone.Y, hhmm, clear, underwater, MusicHeard(audio)))
        {
            if (_ambientShots.TryGetValue((shot.Region, shot.Line), out GameAudio.Voice? last) && !last.Finished)
                continue;   // ours: a random sound doesn't start again while it still plays
            ZoneSoundRegion region = _ambience.Regions[shot.Region];
            if (audio.Play(region.Sounds[shot.Line].Sound, volume: shot.Volume, ignoreOffset: region.IgnoreRandomOffset,
                    bus: region.IsMusic ? GameAudio.MusicBus : GameAudio.EffectsBus) is GameAudio.Voice v)
                _ambientShots[(shot.Region, shot.Line)] = v;
        }
        var heard = new HashSet<(int, int)>();
        foreach (AmbientShot loop in _ambience.Loops(hhmm, clear, underwater))
        {
            heard.Add((loop.Region, loop.Line));
            if (!_ambientLoops.TryGetValue((loop.Region, loop.Line), out GameAudio.Voice? v) || v.Finished)
            {
                ZoneSoundRegion region = _ambience.Regions[loop.Region];
                v = audio.Play(region.Sounds[loop.Line].Sound, loop: true, ignoreOffset: true,
                    bus: region.IsMusic ? GameAudio.MusicBus : GameAudio.EffectsBus);
                if (v is null)
                    continue;
                _ambientLoops[(loop.Region, loop.Line)] = v;
            }
            v.FadeTo(loop.Volume, 0.1f);
        }
        foreach (((int, int) key, GameAudio.Voice v) in _ambientLoops.Where(kv => !heard.Contains(kv.Key)).ToList())
        {
            v.Stop(0.5f);
            _ambientLoops.Remove(key);
        }
    }

    // fade_on_mp3: music from the music channel (not the ambience's own sound_type 2 parts).
    private static bool MusicHeard(GameAudio audio) => audio.MusicPlaying;

    private void LoadAmbience(ushort zone)
    {
        StopAmbience();
        _ambienceZone = zone;
        string path = System.IO.Path.Combine(_libraryPath ?? "", "sounds", "zones", $"zone{zone:000}.dat");
        _ambience = System.IO.File.Exists(path)
            ? new Ambience(ZoneSounds.Parse(System.IO.File.ReadAllText(path, System.Text.Encoding.Latin1)))
            : null;
        GD.Print($"[sound] zone {zone} ambience: {(_ambience is null ? "none" : $"{_ambience.Regions.Count} regions")}");
    }

    private void StopAmbience()
    {
        foreach (GameAudio.Voice v in _ambientLoops.Values)
            v.Stop(1f);
        _ambientLoops.Clear();
        _ambientShots.Clear();
        _ambience = null;
    }
}
