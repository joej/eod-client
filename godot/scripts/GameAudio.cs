// SPDX-License-Identifier: GPL-3.0-or-later
//
// The game's sound and music (roadmap M12; docs/sound.md). One node for the whole run, under the character screen, so
// music carries on from the title into the world and across region changes. It plays the install's own files from the
// library (make sounds: library/sounds), as its tables say:
//   - a name (a cc_sounds row, g_ group, q_ queue, l_ loop; SoundPlanner) becomes one or more players, each with its
//     row's rate (pitch), volume and pan picked at random within the row's ranges, and its offset (a delay);
//   - positional sounds play at a spot, fading out linearly from the row's r1 to r2 (units);
//   - a polygroup (cc_sounds POLYGROUPS) caps how many of its sounds play at once;
//   - music (mp3_) plays on its own channel and crossfades.
// Ours, until daoc-re answers (W52): the fade from r1 to r2 is linear and silent beyond r2, heard from the camera; a full
// polygroup with cutoff 1 stops its oldest sound for the new one, any other cutoff drops the new one; a 2D sound's pan
// is a position left or right of the camera; title music loops, the rest plays once.

using DaocAssets.Sound;
using Godot;

namespace EodClient;

public partial class GameAudio : Node
{
    public const string MusicBus = "Music", EffectsBus = "Effects";
    private const float MetresPerUnit = 0.025f;
    private const float StealFade = 0.15f;    // seconds: a stolen voice fades this fast (ours)
    private const float PanDistance = 2f;     // metres: how far to the side a fully panned 2D sound is placed (ours)

    public static GameAudio? Instance { get; private set; }

    private readonly string _dir;
    private readonly string[] _folders;
    public SoundTables Tables { get; }
    public SoundPlanner Planner { get; }
    private readonly Dictionary<string, AudioStream?> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Pending> _pending = [];
    private readonly List<Voice> _voices = [];
    private readonly Random _rng = new();
    private double _now;
    private readonly bool _log = OS.GetCmdlineUserArgs().Contains("--sound-log");   // each sound started, for checks

    private AudioStreamPlayer? _music, _musicOld;
    private float _musicFade, _musicOldFade, _musicTarget = 1f;
    public string? MusicName { get; private set; }

    /// <summary>The ambience and effects heard through the given node (the camera), for positional sounds.</summary>
    public Node3D? Listener { get; set; }

    private GameAudio(string dir)
    {
        _dir = dir;
        _folders = [dir, System.IO.Path.Combine(dir, "tutorial"), System.IO.Path.Combine(dir, "frontiers")];
        Tables = SoundTables.Load(dir);
        Planner = new SoundPlanner(Tables, _rng);
        Name = "GameAudio";
    }

    /// <summary>The run's audio, from library/sounds, or null (with a note) when it hasn't been made.</summary>
    public static GameAudio? Create(string? library)
    {
        string? dir = library is null ? null : System.IO.Path.Combine(library, "sounds");
        if (dir is null || !System.IO.File.Exists(System.IO.Path.Combine(dir, "cc_sounds.csv")))
        {
            GD.Print("[sound] no library/sounds (make sounds): silent");
            return null;
        }
        EnsureBus(MusicBus);
        EnsureBus(EffectsBus);
        Instance = new GameAudio(dir);
        Instance.ApplyVolumes();
        GD.Print($"[sound] {Instance.Tables.Sounds.Count} sounds from {dir}");
        return Instance;
    }

    public override void _ExitTree()
    {
        if (Instance == this)
            Instance = null;
    }

    private static void EnsureBus(string name)
    {
        if (AudioServer.GetBusIndex(name) >= 0)
            return;
        AudioServer.AddBus();
        int i = AudioServer.BusCount - 1;
        AudioServer.SetBusName(i, name);
        AudioServer.SetBusSend(i, "Master");
    }

    /// <summary>Sets the buses from the saved volumes (Settings → Sound).</summary>
    public void ApplyVolumes()
    {
        bool mute = OS.GetCmdlineUserArgs().Contains("--mute");
        AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex(MusicBus), Mathf.LinearToDb(mute ? 0 : ClientSettings.MusicVolume));
        AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex(EffectsBus), Mathf.LinearToDb(mute ? 0 : ClientSettings.EffectsVolume));
    }

    // --- sounds -------------------------------------------------------------------------------------------------------

    /// <summary>One play of a name: its players, so a loop can be faded, moved or stopped.</summary>
    public sealed class Voice
    {
        internal readonly List<(Node Player, float Base)> Players = [];
        internal int Waiting;   // planned sounds not started yet
        internal string? Polygroup;
        internal double Started;
        internal float Gain = 1f, Target = 1f, FadeRate;
        internal bool StopAtZero;
        internal Vector3? At;
        internal float R1, R2;
        public bool Looping { get; internal set; }
        public bool Finished { get; internal set; }

        /// <summary>Fades to <paramref name="gain"/> over <paramref name="seconds"/>.</summary>
        public void FadeTo(float gain, float seconds) => (Target, FadeRate) = (gain, seconds <= 0 ? 1e6f : 1f / seconds);

        /// <summary>Fades out over <paramref name="seconds"/>, then stops.</summary>
        public void Stop(float seconds = 0)
        {
            FadeTo(0, seconds);
            StopAtZero = true;
        }
    }

    private sealed record Pending(Voice Voice, SoundRow Row, double At, float Volume, bool Loop, string Bus);

    /// <summary>
    /// Plays a sound name or id string (2D when <paramref name="at"/> is null). <paramref name="volume"/> multiplies the
    /// rows' own. A loop (<paramref name="loop"/>, or an l_ name) repeats until stopped. Returns null when nothing plays.
    /// </summary>
    public Voice? Play(string name, Vector3? at = null, float volume = 1f, bool loop = false, bool ignoreOffset = false,
        string bus = EffectsBus)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (Planner.Loop(name) is SoundLoop l)
            (name, loop) = (l.Body, true);
        List<PlannedSound> plan = Planner.Plan(name, Length);
        if (plan.Count == 0)
            return null;
        string? group = plan[0].Row.Polygroup;
        if (group is not null && !MakeRoom(group))
            return null;
        var voice = new Voice { Polygroup = group, Started = _now, Looping = loop, At = at, R1 = plan[0].Row.R1, R2 = plan[0].Row.R2 };
        foreach (PlannedSound p in plan)
        {
            double offset = ignoreOffset ? 0 : p.Row.OffsetMs.Pick(_rng);
            _pending.Add(new Pending(voice, p.Row, _now + (p.AtMs + offset) / 1000, volume, loop, bus));
            voice.Waiting++;
        }
        _voices.Add(voice);
        return voice;
    }

    /// <summary>Plays a cc_sounds id.</summary>
    public Voice? Play(int id, Vector3? at = null, float volume = 1f) => Play(id.ToString(), at, volume);

    // A free voice in the group, stealing the oldest when its cutoff allows (ours: cutoff 1 steals, others drop the new).
    private bool MakeRoom(string group)
    {
        if (!Tables.Polygroups.TryGetValue(group, out Polygroup? g) || g.Limit <= 0)
            return true;
        List<Voice> playing = _voices.Where(v => !v.Finished && !v.StopAtZero && v.Polygroup == group).OrderBy(v => v.Started).ToList();
        if (playing.Count < g.Limit)
            return true;
        if (g.Cutoff != 1)
            return false;
        playing[0].Stop(StealFade);
        return true;
    }

    private double Length(SoundRow row) =>
        Stream(row, loop: false) is AudioStream s ? s.GetLength() * 1000 / Math.Max(0.1f, (row.Rate.Min + row.Rate.Max) / 2) : 0;

    private void Start(Pending p)
    {
        Voice v = p.Voice;
        v.Waiting--;
        if (v.Finished || v.StopAtZero)
            return;
        if (Stream(p.Row, p.Loop) is not AudioStream stream)
            return;
        float baseVolume = p.Volume * p.Row.Volume.Pick(_rng);
        float pitch = Math.Max(0.05f, p.Row.Rate.Pick(_rng));
        Node player;
        if (v.At is not null || p.Row.Pan != default)
        {
            var p3 = new AudioStreamPlayer3D
            {
                Stream = stream, Bus = p.Bus, PitchScale = pitch,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
                DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
            };
            AddChild(p3);
            if (v.At is Vector3 at)
                p3.GlobalPosition = at;
            else
                PlaceBeside(p3, p.Row.Pan.Pick(_rng));
            p3.VolumeDb = Mathf.LinearToDb(baseVolume * v.Gain * DistanceGain(v));
            p3.Play();
            player = p3;
        }
        else
        {
            var p2 = new AudioStreamPlayer { Stream = stream, Bus = p.Bus, PitchScale = pitch, VolumeDb = Mathf.LinearToDb(baseVolume * v.Gain) };
            AddChild(p2);
            p2.Play();
            player = p2;
        }
        v.Players.Add((player, baseVolume));
        if (_log)
            GD.Print($"[sound] play {p.Row.Name} ({p.Row.File}) vol {baseVolume * v.Gain:0.00}{(p.Loop ? " loop" : "")}" +
                     $"{(v.At is Vector3 at3 ? $" at {at3}" : "")} bus {p.Bus}");
    }

    // A 2D sound with a pan: placed left or right of the listener, where Godot's 3D panning puts it (ours).
    private void PlaceBeside(AudioStreamPlayer3D p, float pan)
    {
        if (Listener is null || !IsInstanceValid(Listener))
            return;
        Transform3D t = Listener.GlobalTransform;
        p.GlobalPosition = t.Origin + t.Basis.X.Normalized() * pan * PanDistance - t.Basis.Z.Normalized() * 0.5f;
    }

    private float DistanceGain(Voice v)
    {
        if (v.At is not Vector3 at || Listener is null || !IsInstanceValid(Listener))
            return 1f;
        float d = Listener.GlobalPosition.DistanceTo(at) / MetresPerUnit;
        float r1 = Math.Max(0, v.R1), r2 = v.R2 > r1 ? v.R2 : r1 + 1;
        return d <= r1 ? 1f : d >= r2 ? 0f : 1f - (d - r1) / (r2 - r1);
    }

    /// <summary>Moves a positional voice (a sound that follows a figure).</summary>
    public static void Move(Voice? v, Vector3 at)
    {
        if (v is null)
            return;
        v.At = at;
        foreach ((Node p, _) in v.Players)
            if (p is AudioStreamPlayer3D p3 && IsInstanceValid(p3))
                p3.GlobalPosition = at;
    }

    /// <summary>The decoded stream for a row's file (cached), or null when the file is missing or won't decode.</summary>
    private AudioStream? Stream(SoundRow row, bool loop)
    {
        string key = (loop ? "loop:" : "") + row.File;
        if (_streams.TryGetValue(key, out AudioStream? cached))
            return cached;
        AudioStream? stream = null;
        string? path = _folders.Select(f => System.IO.Path.Combine(f, row.File.ToLowerInvariant())).FirstOrDefault(System.IO.File.Exists);
        if (path is not null)
        {
            try
            {
                WavFile w = WavFile.Decode(System.IO.File.ReadAllBytes(path));
                var wav = new AudioStreamWav
                {
                    Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = w.SampleRate, Stereo = w.Channels == 2, Data = w.Pcm16,
                };
                if (loop)
                    (wav.LoopMode, wav.LoopBegin, wav.LoopEnd) = (AudioStreamWav.LoopModeEnum.Forward, 0, w.Frames);
                stream = wav;
            }
            catch (System.IO.InvalidDataException e)
            {
                GD.PrintErr($"[sound] {row.File}: {e.Message}");
            }
        }
        else
            GD.Print($"[sound] {row.Name}: no file {row.File}");
        _streams[key] = stream;
        return stream;
    }

    public override void _Process(double delta)
    {
        _now += delta;
        for (int i = _pending.Count - 1; i >= 0; i--)
            if (_pending[i].At <= _now)
            {
                Pending p = _pending[i];
                _pending.RemoveAt(i);
                Start(p);
            }
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            Voice v = _voices[i];
            if (v.Gain != v.Target)
                v.Gain = Mathf.MoveToward(v.Gain, v.Target, (float)delta * v.FadeRate);
            bool playing = v.Waiting > 0;
            float distance = DistanceGain(v);
            for (int j = v.Players.Count - 1; j >= 0; j--)
            {
                (Node node, float baseVolume) = v.Players[j];
                bool alive = node switch
                {
                    AudioStreamPlayer p2 => IsInstanceValid(p2) && p2.Playing,
                    AudioStreamPlayer3D p3 => IsInstanceValid(p3) && p3.Playing,
                    _ => false,
                };
                if (!alive || (v.StopAtZero && v.Gain <= 0))
                {
                    if (IsInstanceValid(node))
                        node.QueueFree();
                    v.Players.RemoveAt(j);
                    continue;
                }
                playing = true;
                float db = Mathf.LinearToDb(Math.Max(1e-5f, baseVolume * v.Gain * distance));
                if (node is AudioStreamPlayer a)
                    a.VolumeDb = db;
                else if (node is AudioStreamPlayer3D b)
                    b.VolumeDb = db;
            }
            if (!playing || (v.StopAtZero && v.Gain <= 0))
            {
                v.Finished = true;
                _voices.RemoveAt(i);
            }
        }
        StepMusic((float)delta);
    }

    // --- music --------------------------------------------------------------------------------------------------------

    /// <summary>Plays an mp3_ name on the music channel, crossfading from what plays now. The same name already playing
    /// carries on unless <paramref name="restart"/>.</summary>
    public void PlayMusic(string? name, bool loop = false, float fade = 2f, bool restart = false)
    {
        if (name is null || (!restart && MusicName == name && _music is not null && _music.Playing))
            return;
        if (Tables.Mp3File(name) is not string file ||
            _folders.Select(f => System.IO.Path.Combine(f, file.ToLowerInvariant())).FirstOrDefault(System.IO.File.Exists) is not string path)
        {
            GD.Print($"[sound] music {name}: no file");
            return;
        }
        StopMusic(fade);
        var mp3 = new AudioStreamMP3 { Data = System.IO.File.ReadAllBytes(path), Loop = loop };
        _music = new AudioStreamPlayer { Stream = mp3, Bus = MusicBus, VolumeDb = Mathf.LinearToDb(0.001f) };
        AddChild(_music);
        _music.Play();
        (_musicFade, MusicName) = (fade <= 0 ? 1 : 0, name);
        _musicTarget = 1f;
        GD.Print($"[sound] music {name} ({file})");
    }

    /// <summary>Fades the music out.</summary>
    public void StopMusic(float fade = 2f)
    {
        if (_music is null)
            return;
        if (_musicOld is not null && IsInstanceValid(_musicOld))
            _musicOld.QueueFree();
        (_musicOld, _musicOldFade, _music, MusicName) = (_music, fade <= 0 ? 0 : _musicFade, null, null);
        _musicOldRate = fade <= 0 ? 1e6f : 1f / fade;
    }

    private float _musicOldRate = 0.5f;

    /// <summary>Lowers the music (ambience's fade_on_mp3 works the other way; this is for duck requests), 0–1.</summary>
    public void DuckMusic(float level) => _musicTarget = level;

    public bool MusicPlaying => _music is not null && IsInstanceValid(_music) && _music.Playing;

    private void StepMusic(float delta)
    {
        if (_music is not null && IsInstanceValid(_music))
        {
            _musicFade = Mathf.MoveToward(_musicFade, _musicTarget, delta * 0.5f);
            _music.VolumeDb = Mathf.LinearToDb(Math.Max(1e-4f, _musicFade));
            if (!_music.Playing)
            {
                GD.Print($"[sound] music {MusicName} ended");
                _music.QueueFree();
                (_music, MusicName) = (null, null);
            }
        }
        if (_musicOld is not null && IsInstanceValid(_musicOld))
        {
            _musicOldFade = Mathf.MoveToward(_musicOldFade, 0, delta * _musicOldRate);
            _musicOld.VolumeDb = Mathf.LinearToDb(Math.Max(1e-4f, _musicOldFade));
            if (_musicOldFade <= 0 || !_musicOld.Playing)
            {
                _musicOld.QueueFree();
                _musicOld = null;
            }
        }
    }

    // --- tables -------------------------------------------------------------------------------------------------------

    /// <summary>The interface click (cc_interface "mouse click"; ours: the chargen row, the only kind the table has).</summary>
    public void Click() => Play(Tables.Interface.Get(1, 1) ?? "s_click");
}
