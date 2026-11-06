// SPDX-License-Identifier: GPL-3.0-or-later

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

/// <summary>
/// Incoming entity packets (roadmap M5) checked against the recorded 1.127e session. 0xA1
/// ObjectUpdate dominates the stream (1143 of 1886 S→C packets), so it is the one we validate
/// hardest; the create/remove packets are exercised for layout sanity where the capture has them.
/// </summary>
public class WorldEntityPacketTests
{
    private static readonly CaptureFile Capture = CaptureFile.Login1127e();
    private static readonly List<ServerPacket> Server = Capture.ServerPackets();

    private delegate T SpanParser<out T>(ReadOnlySpan<byte> payload);

    private static List<T> ParseAll<T>(byte code, SpanParser<T> parse)
    {
        var result = new List<T>();
        foreach (ServerPacket p in Server)
            if (p.Code == code)
                result.Add(parse(p.Payload));
        return result;
    }

    [Fact]
    public void ObjectUpdateIsTheBulkOfTheStream()
    {
        // The capture is why M5 targets 0xA1 first: it's the common case by a wide margin.
        int objectUpdates = Server.Count(p => p.Code == ObjectUpdate.Code);
        Assert.True(objectUpdates > Server.Count / 2, $"expected 0xA1 to dominate, got {objectUpdates}/{Server.Count}");
    }

    [Fact]
    public void ObjectUpdateDecodesTheFirstRecordedPacketByField()
    {
        // packet0 payload: 0000 0b82 377f 0000 6645 0000 0a7a 0000 3401 0000 6440 0000
        ObjectUpdate first = ParseAll(ObjectUpdate.Code, ObjectUpdate.Parse)[0];
        Assert.Equal(0, first.Speed);               // stationary
        Assert.Equal(0x0B82, first.Heading);        // < 4096, so no Z-speed bits
        Assert.Equal(0x0B82, first.HeadingOnly);
        Assert.Equal(0x377F, first.XOffsetInZone);  // zone-local
        Assert.Equal(0x6645, first.YOffsetInZone);
        Assert.Equal(0x0A7A, first.Z);              // ~2682, a plausible Camelot Hills height
        Assert.Equal(0x3401, first.ObjectId);
        Assert.Equal(0, first.TargetId);            // not attacking
        Assert.Equal(100, first.HealthPercent);
        Assert.Equal(0x40, first.Flags);
        Assert.Equal(1, first.Realm);               // Albion
        Assert.Equal(0, first.ZoneSkinId);
    }

    [Fact]
    public void EveryObjectUpdateParsesAndHoldsItsInvariants()
    {
        List<ObjectUpdate> all = ParseAll(ObjectUpdate.Code, ObjectUpdate.Parse);
        Assert.NotEmpty(all);
        foreach (ObjectUpdate u in all)
        {
            // Heading minus the Z-speed bits is a real DAoC heading.
            Assert.True(u.HeadingOnly <= 0x0FFF);
            // Realm is one of the four valid values (most are Albion here; a few are Hib/none).
            Assert.InRange(u.Realm, 0, 3);
        }
        // Health is a percent byte for 1113 of these, but 30 report 200 — NPC HealthPercent is not
        // clamped to 100 the way a player's is. See docs/open-questions.md (0xA1 health > 100).
        Assert.All(all, u => Assert.True(u.HealthPercent is 100 or 200,
            $"unexpected 0xA1 health byte {u.HealthPercent}"));
    }

    [Fact]
    public void ObjectUpdateRoundTripsThroughZoneOffsetsToRegionGlobal()
    {
        // The payoff of the zone-local encoding: add the zone's offset to recover a region-global
        // coordinate the renderer can place with the same ToGodot() the player uses. Camelot Hills
        // is zone 0 / region 1; its offset is the west/north edge in region coords.
        World.Zone? camhills = World.Zones.All.FirstOrDefault(z => z.RegionId == 1 && z.Id == 0);
        Assert.NotNull(camhills);
        ObjectUpdate first = ParseAll(ObjectUpdate.Code, ObjectUpdate.Parse)[0];
        long regionX = camhills!.X + first.XOffsetInZone;
        long regionY = camhills.Y + first.YOffsetInZone;
        // The recorded player (0x20) spawned near region (574797, 530161); NPCs in the same zone
        // land in the same neighbourhood once the offset is applied, not at the raw ~14000 value.
        Assert.InRange(regionX, 500_000, 650_000);
        Assert.InRange(regionY, 450_000, 600_000);
    }

    [Fact]
    public void RemoveObjectAndObjectDeleteLayoutsAreStable()
    {
        // No frame in this short capture, so exercise the layout directly against hand-built bytes
        // matching the server writers (SendObjectRemove / SendObjectDelete).
        RemoveObject r = RemoveObject.Parse([0x12, 0x34, 0x00, 0x02]);
        Assert.Equal(0x1234, r.ObjectId);
        Assert.Equal(2, r.ObjectType);   // a player left view

        ObjectDelete d = ObjectDelete.Parse([0x12, 0x34, 0x00, 0x01]);
        Assert.Equal(0x1234, d.ObjectId);
    }

    private static byte[] NpcCreatePayload(byte flags2, string name, byte flags3 = 0) => new PayloadWriter()
        .U16BE(4778).U16BE(0).U16BE(0).U16BE(2897).U32BE(574000).U32BE(530000).Zeros(2)
        .U16BE(1923).U8(255).U8(0).U8(0x30)          // model, size, level, flags (peace, flying)
        .U8(0x20).U8(flags2).U8(flags3).Zeros(2)     // max stick, flags2, flags3, unknown
        .U8((byte)name.Length).Bytes(System.Text.Encoding.ASCII.GetBytes(name)).U8(0).ToArray();

    [Theory]
    [InlineData(0x03, "", true)]            // a player sees the bits (can't target, name hidden)
    [InlineData(0x00, "[]-DOR-NON", true)]  // a GM sees name suffixes instead (PacketLib1124.SendNPCCreate)
    [InlineData(0x02, "Guard", false)]      // a hidden name alone is still a creature
    public void AnUntargetableNpcIsMarkedByItsBitOrItsGmSuffix(byte flags2, string name, bool untargetable)
    {
        NpcCreate npc = NpcCreate.Parse(NpcCreatePayload(flags2, name));
        Assert.Equal((flags2, name, (ushort)1923, untargetable), (npc.Flags2, npc.Name, npc.ModelId, npc.Untargetable));
    }

    [Fact]
    public void TheRecordingsUntargetableNpcsAreMarkersAndEffects()
    {
        // 111 NPC creates: two nameless teleporter indicators (model 1923) and a swarm of flies; every
        // other NPC can be targeted.
        List<NpcCreate> npcs = ParseAll(NpcCreate.Code, NpcCreate.Parse);
        Assert.Equal(111, npcs.Count);
        Assert.Equal(
            ["1923 3 ", "1923 3 ", "2020 3 swarm of flies"],
            npcs.Where(n => n.Untargetable).Select(n => $"{n.ModelId} {n.Flags2} {n.Name}").Order());
    }

    [Theory]
    [InlineData(0x00, 0x00, QuestIndicator.None)]
    [InlineData(0x08, 0x00, QuestIndicator.Available)]   // PacketLib1124.SendNPCCreate: flags2 0x08
    [InlineData(0x10, 0x00, QuestIndicator.Finish)]      // flags2 0x10
    [InlineData(0x00, 0x01, QuestIndicator.Lesson)]      // flags3 0x01
    [InlineData(0x00, 0x02, QuestIndicator.Lore)]        // flags3 0x02
    [InlineData(0x00, 0x20, QuestIndicator.Pending)]     // flags3 0x20
    [InlineData(0x04, 0x00, QuestIndicator.None)]        // stealthed only
    public void AnNpcCarriesItsQuestGlyphInItsFlagBytes(byte flags2, byte flags3, QuestIndicator quest) =>
        Assert.Equal(quest, NpcCreate.Parse(NpcCreatePayload(flags2, "Wicoessa", flags3)).Quest);

    [Fact]
    public void AQuestEffectChangesTheGlyphAndOtherEffectsDoNot()
    {
        // PacketLib173.SendNPCsQuestEffect: object id, kind 7, indicator, u32 0.
        VisualEffect quest = VisualEffect.Parse(new PayloadWriter().U16BE(0x1234).U8(7).U8(0x02).U32BE(0).ToArray());
        Assert.Equal(((ushort)0x1234, (QuestIndicator?)QuestIndicator.Finish), (quest.ObjectId, quest.Quest));
        // The recording's two 0x4C packets are other kinds (5 and 9): no glyph.
        List<VisualEffect> recorded = ParseAll(VisualEffect.Code, VisualEffect.Parse);
        Assert.Equal([5, 9], recorded.Select(e => (int)e.Kind));
        Assert.All(recorded, e => Assert.Null(e.Quest));
    }
}
