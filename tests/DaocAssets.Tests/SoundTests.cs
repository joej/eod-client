// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using DaocAssets.Sound;

namespace DaocAssets.Tests;

/// <summary>
/// The sound tables, what a name plays, zone sound regions and the .wav decoder (roadmap M12). Synthetic rows in the
/// install's own shapes; the live tests read the owner's tables (private/sound-fixtures: the cc_ csv files, zones.dat,
/// zone000's sounds.dat and three .wav files, one of each kind).
/// </summary>
public sealed class SoundTests
{
    private const string Sounds = """
        ;,ID#,sound ID,filename,rate,vol,pan,offset,r1,r2,,,polygroup,,
        !,POLYGROUPS,,,,,,,,,,,,,
        #,,ambient_sfx,,6,1,"rule-of-thumb : if a sound is not a result of player-action, it's ambient",,,,,,,,
        #,,TutorialVO,,1,2,,,,,,,,,
        #,0,s_default,,1,1,C,0,0,1000,50,0,,1,1
        ,412,s_Bed_Prairie,ambloop2.wav,.9-1.0,0.5,*,*,*,*,*,*,ambient_sfx,*,*
        ,2,s_click,click.wav,*,*,*,*,*,*,*,*,*,*,*
        ,10,s_A_01,a1.wav,1.0 - 1.1,*,.9L-.9R,300-349,*,4500,*,*,*,*,*
        ,11,s_A_02,a2.wav,*,*,*,*,*,*,*,*,*,*,*
        ,12,s_B,b.wav,*,*,*,*,*,*,*,*,*,*,*
        """;

    private const string Groups = """
        ;,group ID,group members,repeat,usage,notes
        ,g_A,"s_A_01, *02",100,spells,
        ,g_Maybe,"s_null, s_B",100,,
        """;

    private const string Queues = """
        ;,queue ID,queue members,interval,repeat,usage,notes
        ,q_AB,"s_B, s_silence(500), g_A",250,100,creatures,
        ,q_EndToEnd,"s_B, s_click",-1,100,,
        """;

    private static SoundTables Tables(string? groups = Groups, string? queues = Queues) => SoundTables.Parse(name => name switch
    {
        "cc_sounds.csv" => Sounds,
        "cc_groups.csv" => groups,
        "cc_queues.csv" => queues,
        "cc_mp3s.csv" => ",2,mp3_Enter_Albion,play.mp3,referenced by zones.dat",
        "cc_music.csv" => ";,MP3\n6,mp3_Region_AlbionTown1,,,",
        "cc_doors.csv" => ";,name,open,close\n1,door1,s_door1open,s_door1close,\"wooden, 1 second\"",
        _ => null,
    });

    [Fact]
    public void AStarTakesTheDefaultRowsValue()
    {
        SoundTables t = Tables();
        SoundRow bed = t.Sound(412)!;
        Assert.Equal(new SoundRange(0.9f, 1.0f), bed.Rate);
        Assert.Equal(new SoundRange(0.5f, 0.5f), bed.Volume);
        Assert.Equal(new SoundRange(0, 0), bed.Pan);       // C
        Assert.Equal(1000, bed.R2);
        Assert.Equal("ambient_sfx", bed.Polygroup);
        Assert.Same(bed, t.Sound("S_BED_PRAIRIE"));        // names any case
        SoundRow a = t.Sound("s_A_01")!;
        Assert.Equal(new SoundRange(1.0f, 1.1f), a.Rate);
        Assert.Equal(new SoundRange(-0.9f, 0.9f), a.Pan);
        Assert.Equal(new SoundRange(300, 349), a.OffsetMs);
        Assert.Equal(4500, a.R2);
        Assert.Null(t.Sound(2)!.Polygroup);
        Assert.Equal(new Polygroup("TutorialVO", 1, 2), t.Polygroups["tutorialvo"]);
    }

    [Fact]
    public void SheetsAndMusicAreReadByRowNumber()
    {
        SoundTables t = Tables();
        Assert.Equal("s_door1open", t.Doors.Get(1, 0));
        Assert.Equal("s_door1close", t.Doors.Get(1, 1));
        Assert.Null(t.Doors.Get(2, 0));
        Assert.Equal("mp3_Region_AlbionTown1", t.Music.Get(6, 0));
        Assert.Equal("mp3_Enter_Albion", t.Mp3Name(2));
        Assert.Equal("play.mp3", t.Mp3File("mp3_enter_albion"));
    }

    [Fact]
    public void AStarredMemberIsTheLastNameRenumbered()
    {
        Assert.Equal(["s_A_01", "s_A_02", "s_A_03"], SoundTables.Members("s_A_01, *02, *03"));
        Assert.Equal(["s_x", "g_y"], SoundTables.Members(" s_x ,g_y,"));
        Assert.Equal(["a", "b,c", ""], SoundTables.Split("a,\"b,c\","));
    }

    [Fact]
    public void AGroupPlaysOneMemberAndSNullPlaysNothing()
    {
        var planner = new SoundPlanner(Tables(), new Random(1));
        var seen = new HashSet<string>();
        for (int i = 0; i < 40; i++)
            seen.Add(planner.Plan("g_A").Single().Row.Name);
        Assert.Equal(["s_A_01", "s_A_02"], seen.Order());
        int silent = Enumerable.Range(0, 40).Count(_ => planner.Plan("g_Maybe").Count == 0);
        Assert.InRange(silent, 1, 39);
    }

    [Fact]
    public void AQueuePlaysItsMembersAnIntervalApart()
    {
        var planner = new SoundPlanner(Tables());
        List<PlannedSound> q = planner.Plan("q_AB");
        Assert.Equal(2, q.Count);
        Assert.Equal(("s_B", 0.0), (q[0].Row.Name, q[0].AtMs));
        Assert.Equal(750, q[1].AtMs);   // 250 after s_B the spacer starts, and takes its own 500 in place of the interval
        List<PlannedSound> ends = planner.Plan("q_EndToEnd", r => r.Name == "s_B" ? 700 : 100);
        Assert.Equal(700, ends[1].AtMs);
        Assert.Single(planner.Plan("412"));   // by number, as sounds.dat names them
        Assert.Empty(planner.Plan("g_Nothing"));
    }

    [Fact]
    public void AZoneSoundRegionHasItsLinesAndShapes()
    {
        IReadOnlyList<ZoneSoundRegion> regions = ZoneSounds.Parse("""
            [SoundRegion00]
            Name=global ambience
            fade_time=5
            zone_wide_sounds=1
            Sound00=2, 412, 0, 100, 18, 0, 0, 0 ; ZONE-SPECIFIC AMBIENCE LOOP

            [SoundRegion01]
            Name=river
            version=2
            Sound00=1, g_Wind_Prairie, 2-6, 60, 64, 1901, 0430, 1, 2
            Shape00=1, 1000, 2000, 500
            Shape01=0, 0, 0, 100, 100
            xShape00=1, 1000, 2000, 100
            """);
        Assert.Equal(2, regions.Count);
        ZoneSoundRegion global = regions[0];
        Assert.True(global.Contains(-5, 99999));
        Assert.Equal(5, global.FadeTime);
        ZoneSoundLine loop = global.Sounds.Single();
        Assert.Equal((ZoneSoundLine.Loop, "412", 18f), (loop.Kind, loop.Sound, loop.Volume));
        Assert.True(loop.PlaysAt(1234));

        ZoneSoundRegion river = regions[1];
        ZoneSoundLine wind = river.Sounds.Single();
        Assert.Equal(new SoundRange(2, 6), wind.PeriodS);
        Assert.Equal(60, wind.Chance);
        Assert.Equal(2, wind.Environment);
        Assert.True(wind.PlaysAt(2300) && wind.PlaysAt(100) && !wind.PlaysAt(1200));   // wraps past midnight
        Assert.True(wind.PlaysIn(1) && !wind.PlaysIn(2));
        Assert.True(river.Contains(1300, 2000));
        Assert.False(river.Contains(1050, 2000));   // in the silent circle
        Assert.True(river.Contains(50, 50));
        Assert.False(river.Contains(5000, 5000));
    }

    private static readonly IReadOnlyList<ZoneSoundRegion> Ambient = ZoneSounds.Parse("""
        [SoundRegion00]
        fade_time=2
        zone_wide_sounds=1
        Sound00=2, 412, 0, 100, 50, 0, 0, 0
        Sound01=2, 413, 0, 100, 50, 1900, 0500, 0
        [SoundRegion01]
        fade_time=0
        random_sound_spacing=5
        Sound00=1, 417, 2, 100, 40, 0, 0, 1
        Sound01=1, 418, 2, 100, 40, 0, 0, 2
        Shape00=1, 0, 0, 100
        """);

    [Fact]
    public void ALoopFadesInWithItsRegionAndKeepsToItsHours()
    {
        var a = new Ambience(Ambient, new Random(3));
        a.Step(1, 5000, 5000, 1200, 1, false, false);
        Assert.Equal(0.5f, a.Gain(0), 3);   // half of the 2 s fade
        Assert.Equal([(0, 0, 0.25f)], a.Loops(1200, 1, false).Select(s => (s.Region, s.Line, s.Volume)));
        a.Step(5, 5000, 5000, 2300, 1, false, false);
        Assert.Equal([0, 1], a.Loops(2300, 1, false).Select(s => s.Line));   // the night bed too
        Assert.Empty(a.Loops(2300, 1, underwater: true));                    // version 1 lines are above water
        Assert.Equal(0, a.Gain(1));                                          // outside the circle
    }

    [Fact]
    public void ARandomLineRollsEachPeriodAndKeepsItsRegionsSpacing()
    {
        var a = new Ambience(Ambient, new Random(3));
        var times = new List<double>();
        for (int i = 0; i < 300; i++)
            foreach (AmbientShot s in a.Step(0.1, 10, 10, 1200, 1, false, false))
                times.Add(i * 0.1);
        Assert.NotEmpty(times);
        Assert.All(times.Zip(times.Skip(1)), p => Assert.True(p.Second - p.First >= 4.99, $"{p.First} then {p.Second}"));
        // Only the clear-weather line (rain's never plays when it's clear), and nothing outside the circle.
        var b = new Ambience(Ambient, new Random(3));
        Assert.Empty(Enumerable.Range(0, 300).SelectMany(_ => b.Step(0.1, 900, 900, 1200, 1, false, false)));
        var c = new Ambience(Ambient, new Random(3));
        Assert.All(Enumerable.Range(0, 300).SelectMany(_ => c.Step(0.1, 10, 10, 1200, 1, false, false)), s => Assert.Equal(0, s.Line));
    }

    [Fact]
    public void SoundSetsComeFromTheModelAndObjectTables()
    {
        // monsters.csv column 15 and objects.csv columns 19 (stance) and 21 (sound set), after two header rows.
        string monsters = "Information,,NIF\nID,name,#\n8,Briton Male Merchant 1,206,114,1020,116,117,118,119,120,0,0,100,80,1,1,0\n" +
                          "9,No Sound,206,0,0,0,0,0,0,0,0,0,100,80,1,0,0\n";
        string objects = "Information,,NIF\nID,name,#\n1,briton dagger,166,201,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,1,0,21,0\n" +
                         "2,bow,166,201,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,5,0,3,0\n";
        SoundSets sets = SoundSets.FromTables(monsters, objects);
        Assert.Equal(1, sets.Creature(8));
        Assert.Null(sets.Creature(9));
        Assert.Equal((21, 1), sets.Weapon(1));
        Assert.Equal((3, 5), sets.Weapon(2));
        Assert.False(SoundSets.HasAttackSound(5));
        SoundSets again = SoundSets.FromCsv(sets.ToCsv());
        Assert.Equal((1, (21, 1)), (again.Creature(8), again.Weapon(1)));
    }

    [Fact]
    public void EntryMusicIsTheRegionsOrItsProxys()
    {
        var zonesDat = DaocAssets.Zone.IniFile.Parse("""
            [region001]
            entry_music=2
            [region030]
            proxy_region=73
            [region073]
            entry_music=6
            [region050]
            entry_music=0
            """);
        Assert.Equal(2, RegionMusic.EntryMusic(zonesDat, 1));
        Assert.Equal(6, RegionMusic.EntryMusic(zonesDat, 30));
        Assert.Null(RegionMusic.EntryMusic(zonesDat, 50));
        Assert.Null(RegionMusic.EntryMusic(zonesDat, 99));
    }

    [Fact]
    public void PcmAndImaAdpcmDecodeTo16BitSamples()
    {
        // PCM 16: the samples as stored.
        WavFile pcm = WavFile.Decode(Riff(1, 1, 22050, 16, 2, [0x34, 0x12, 0xFF, 0xFF]));
        Assert.Equal((1, 22050, 2), (pcm.Channels, pcm.SampleRate, pcm.Frames));
        Assert.Equal((short)0x1234, BinaryPrimitives.ReadInt16LittleEndian(pcm.Pcm16));

        // IMA ADPCM, one mono block of 8 bytes: header (first sample 100, step index 0), then 8 nibbles. Nibble 7 at
        // step 7 adds 7/8 + 7 + 7/2 + 7/4 = 11 (integer shifts) and moves the index up by 8.
        WavFile ima = WavFile.Decode(Riff(0x11, 1, 22050, 4, 8, [100, 0, 0, 0, 0x07, 0, 0, 0]));
        Assert.Equal(9, ima.Frames);   // the header's sample, then 8
        short[] s = Enumerable.Range(0, ima.Frames).Select(i => BinaryPrimitives.ReadInt16LittleEndian(ima.Pcm16.AsSpan(i * 2))).ToArray();
        Assert.Equal(100, s[0]);
        Assert.Equal(111, s[1]);
        Assert.True(s.Skip(2).All(v => v >= 111));   // nibble 0 only ever adds step/8
        Assert.Throws<InvalidDataException>(() => WavFile.Decode("RIFX0000WAVE"u8));
    }

    private static byte[] Riff(int format, int channels, int rate, int bits, int blockAlign, byte[] data)
    {
        var b = new List<byte>();
        void U32(int v) => b.AddRange(BitConverter.GetBytes(v));
        void U16(int v) => b.AddRange(BitConverter.GetBytes((ushort)v));
        b.AddRange("RIFF"u8.ToArray()); U32(36 + data.Length); b.AddRange("WAVE"u8.ToArray());
        b.AddRange("fmt "u8.ToArray()); U32(16); U16(format); U16(channels); U32(rate); U32(rate * blockAlign); U16(blockAlign); U16(bits);
        b.AddRange("data"u8.ToArray()); U32(data.Length); b.AddRange(data);
        return [.. b];
    }

    // --- the owner's install --------------------------------------------------------------------------------------

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "live-sound", name);

    [LiveFact("live-sound/cc_sounds.csv")]
    public void EveryNameTheTablesUseResolves()
    {
        SoundTables t = SoundTables.Load(Fixture(""));
        var planner = new SoundPlanner(t);
        Assert.True(t.Sounds.Count > 4000);
        // Every name a sheet or structure refers to is a sound or a structure (s_null and s_silence aside).
        var unknown = new List<string>();
        foreach (SoundSheet sheet in new[] { t.Creatures, t.Weapons, t.Spells, t.Doors, t.Events, t.Interface })
            foreach (int row in sheet.RowIds)
                for (int c = 0; c < 40; c++)
                    if (sheet.Get(row, c) is string n && (n.StartsWith("s_") || n.StartsWith("g_") || n.StartsWith("q_") || n.StartsWith("spell_"))
                        && !planner.Knows(n) && !n.Equals("s_null", StringComparison.OrdinalIgnoreCase))
                        unknown.Add(n);
        // A few names in the shipped sheets have no row (typos in the install's tables); keep that small and known.
        Assert.True(unknown.Distinct().Count() < 40, $"{unknown.Distinct().Count()}: " + string.Join(", ", unknown.Distinct().Take(80)));
        Assert.Equal("s_door1open", t.Doors.Get(1, 0));
        Assert.Equal("mp3_Enter_Albion", t.Mp3Name(2));
        Assert.Equal("mp3_Enter_Dungeon", t.Mp3Name(6));
    }

    [LiveFact("live-sound/zone000.dat")]
    public void CamelotHillsHasItsPrairieBedAndWind()
    {
        IReadOnlyList<ZoneSoundRegion> regions = ZoneSounds.Parse(File.ReadAllText(Fixture("zone000.dat")));
        ZoneSoundRegion global = regions[0];
        Assert.Equal("global ambience", global.Name);
        Assert.True(global.ZoneWide);
        Assert.Contains(global.Sounds, s => s.Kind == ZoneSoundLine.Loop && s.Sound == "412");
        Assert.True(regions.Count > 5);
        Assert.Contains(regions, r => r.Shapes.Count > 0);
    }

    [LiveTheory("live-sound/adrghit.wav")]
    [InlineData("adrghit.wav", 1)]        // PCM 16 mono
    [InlineData("flail_use01.wav", 2)]    // PCM 16 stereo
    [InlineData("ac1a_01a.wav", 1)]       // IMA ADPCM
    public void TheInstallsWaveKindsDecode(string file, int channels)
    {
        WavFile w = WavFile.Decode(File.ReadAllBytes(Fixture(file)));
        Assert.Equal(channels, w.Channels);
        Assert.InRange(w.Seconds, 0.05, 60);
        // Not silence, and not noise from a wrong nibble order: the decoded signal moves smoothly.
        short[] s = Enumerable.Range(0, w.Frames * w.Channels).Select(i => BinaryPrimitives.ReadInt16LittleEndian(w.Pcm16.AsSpan(i * 2))).ToArray();
        Assert.Contains(s, v => Math.Abs(v) > 500);
        double jumps = s.Zip(s.Skip(w.Channels)).Average(p => Math.Abs(p.First - p.Second));
        double level = s.Average(v => Math.Abs((double)v));
        Assert.True(jumps < level * 1.5, $"mean step {jumps:F0} against mean level {level:F0}");
    }
}
