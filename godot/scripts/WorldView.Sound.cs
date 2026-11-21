// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: what the world sounds like (roadmap M12; docs/sound.md, daoc-re 2026-11-08). GameAudio plays; this decides:
//   - entering a region plays its entry music (zones.dat entry_music, a cc_mp3s id; RegionMusic);
//   - the zone's ambience (its sounds.dat; DaocAssets.Sound.Ambience) at the character's position and the game time;
//   - S→C 0xEF type 2: the area's music (cc_music row); type 1: the realm's level-up fanfare (CC_events "Raise Level");
//   - S→C 0xD3: a crafting result's sound, or a cc_sounds id; 0xC9: a cc_sounds id, at its spot when it has a radius;
//   - S→C 0x99: the door's open or close sound (cc_doors row = the packet's sound byte), at the door;
//   - attack mode on: combat music (mp3_Combat1 or 2 at random; not in region 27, the tutorial, nor while it already
//     plays); attack mode off or your own death fades it out.
// Ours: the weather is always clear (no 0x92 yet), the character is never under water for ambience, and 0xD3 type 3
// plays 2D (its position's layout isn't known).

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
        };
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

    // Each frame: the zone's ambience where the character stands.
    private void UpdateSound(double delta)
    {
        if (Audio is not GameAudio audio)
            return;
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
