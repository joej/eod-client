// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Text;
using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;

namespace DaocProtocol.Tests;

/// <summary>Trainers: the 0x7B sections from the server, 0x53 from the client.</summary>
public sealed class TrainerPacketTests
{
    // OpenDAoC's WritePascalString: u8 length, bytes.
    private static IEnumerable<byte> Pascal(string s) => new[] { (byte)s.Length }.Concat(Encoding.Latin1.GetBytes(s));

    [Fact]
    public void TheSpecListIsReadAsOpenDaocWritesIt()
    {
        // count 2, 7 points, code 0, 0; then index, level, level + 1, name.
        byte[] bytes = [2, 7, 0, 0, 0, 4, 5, .. Pascal("Slash"), 1, 1, 2, .. Pascal("Parry")];
        TrainerWindow w = TrainerWindow.Parse(bytes);
        Assert.Equal(TrainerWindow.Code0Specs, w.Code);
        Assert.Equal(7, w.Points);
        Assert.Equal([new TrainerSpec(0, 4, 5, "Slash"), new TrainerSpec(1, 1, 2, "Parry")], w.Specs);
    }

    [Fact]
    public void NamesAndSkillsAreRead()
    {
        byte[] names = [2, 7, 4, 0, 3, .. Pascal("Backstab"), .. Pascal("Bleeder")];
        TrainerWindow n = TrainerWindow.Parse(names);
        Assert.Equal(3, n.NameStart);
        Assert.Equal(["Backstab", "Bleeder"], n.Names);

        var b = new List<byte> { 1, 7, 3, 0, 0 };
        b.AddRange(Enumerable.Repeat((byte)255, 49));
        b.AddRange([0, 2, 5]);                                     // spec 0, two skills, 5 levels auto-trained
        b.AddRange([2, 0x01, 0x2C, 4, 0, 0xFD, 0x00, 0x10]);       // an ability at 2: icon 300, page 4, line 0xFD, id 16
        b.AddRange([8, 0x00, 0x96, 2, 1, 3, 0x01, 0x00]);          // a style at 8: icon 150, page 2, opens after type 1 value 3, id 256
        TrainerWindow s = TrainerWindow.Parse(b.ToArray());
        Assert.Equal(50, s.LevelTable.Length);
        TrainerSpecSkills line = Assert.Single(s.SpecSkills);
        Assert.Equal(5, line.Autotrain);
        Assert.Equal(2, line.Skills[0].AtLevel);
        Assert.Equal([new TrainerSkill(2, 300, 4, 0, 0xFD, 16), new TrainerSkill(8, 150, 2, 1, 3, 256)], line.Skills);
    }

    [Fact]
    public void ARecordWithB4Of200CarriesTwoMoreBytes()
    {
        var b = new List<byte> { 1, 0, 3, 0 };
        b.AddRange(new byte[50]);
        b.AddRange([0, 2, 0]);
        b.AddRange([0, 0, 1, 3, 200, 9, 0xAB, 0xCD, 0x00, 0x05]);   // level 0 (= 50), b4 200: extra 0xABCD, then id 5
        b.AddRange([4, 0, 2, 3, 0, 0xFE, 0x00, 0x06]);
        TrainerSpecSkills line = Assert.Single(TrainerWindow.Parse(b.ToArray()).SpecSkills);
        Assert.Equal(new TrainerSkill(0, 1, 3, 200, 9, 5, 0xABCD), line.Skills[0]);
        Assert.Equal(50, line.Skills[0].AtLevel);
        Assert.Equal((ushort)6, line.Skills[1].Id);
    }

    [Fact]
    public void ALineIsAtItsThirdByteLessOne()
    {
        var spec = new TrainerSpec(0, 4, 5, "Slash");
        Assert.Equal(4, spec.Current);
        Assert.True(spec.HasSlider);
        Assert.False(new TrainerSpec(0, 0, 0, "Riding").HasSlider);
        Assert.Equal(new byte[] { 0 }, TrainWindowRequest.Payload());
    }

    [Fact]
    public void RealmAbilitiesAndTheirCostsAreRead()
    {
        byte[] ras = [1, 2, 1, 0, 0, 0, 1, .. Pascal("[Purge]")];
        Assert.Equal(new TrainerRealmAbility(0, 0, 1, "[Purge]"), Assert.Single(TrainerWindow.Parse(ras).RealmAbilities));
        byte[] costs = [1, 2, 5, 0, 1, 0, 3, 1, 3, 6, .. Pascal("Toughness")];
        TrainerRealmCosts c = Assert.Single(TrainerWindow.Parse(costs).RealmCosts);
        Assert.Equal((byte)1, c.Level);
        Assert.Equal(new byte[] { 1, 3, 6 }, c.Costs);
        Assert.Equal("Toughness", c.Name);
    }

    [Fact]
    public void ATrainRequestIsReadAsOpenDaocReadsIt()
    {
        byte[] p = TrainRequest.Payload([(0, 6), (2, 3)], [0, 2]);
        Assert.Equal(264, p.Length);
        // PlayerTrainHandler: 8 ReadInt codes until 0xFFFFFFFF; then one ReadInt level per code; then at +64, 50 ReadInt.
        uint U(int at) => BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(at));
        Assert.Equal(0u, U(0));
        Assert.Equal(2u, U(4));
        Assert.Equal(0xFFFFFFFFu, U(8));   // the first unused slot
        Assert.Equal(0u, U(12));           // later ones 0
        Assert.Equal(6u, U(32));
        Assert.Equal(3u, U(36));
        Assert.Equal(0u, U(64));
        Assert.Equal(2u, U(68));
        Assert.Throws<ArgumentException>(() => TrainRequest.Payload(Enumerable.Range(0, 9).Select(i => ((byte)i, (byte)2)).ToList()));
    }

    [Fact]
    public void TheBridgeReportsEachSection()
    {
        var bridge = new NetworkBridge();
        TrainerWindow? seen = null;
        bridge.TrainerWindowReceived += w => seen = w;
        bridge.Dispatch(new ServerPacket(TrainerWindow.PacketCode, [0, 3, 0, 0]));
        Assert.Equal((byte)3, seen?.Points);
        Assert.False(bridge.UnhandledCounts.ContainsKey(TrainerWindow.PacketCode));
    }
}
