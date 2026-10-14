// SPDX-License-Identifier: GPL-3.0-or-later
//
// No recorded session contains a 0xFF, so these tests decode our payloads with a port of the
// server's reader: OpenDAoC-Core 2026.09.36 (f0aaa1e)
// GameServer/packets/Client/168/CharacterCreateRequestHandler.cs, CreationCharacterData, the
// "client.Version > Version1124" branch for 1.126+, plus HandleDeleteCharacterRequest's slot
// arithmetic. OpenDAoC-Core is GPL-3.0.

using DaocProtocol.Net;
using DaocProtocol.Packets;

namespace DaocProtocol.Tests;

public class CharacterCreateTests
{
    private static readonly CharacterStats BritonArmsman = new(70, 70, 70, 60, 60, 60, 60, 60);

    private static NewCharacter Character(string name = "Testarms", Realm realm = Realm.Albion, int slot = 0) =>
        new(name, realm, slot, ClassId: 2, Race: 1, Female: false, Model: 32, BritonArmsman);

    /// <summary>What the server reads from a 1.126+ 0xFF payload.</summary>
    private sealed record ServerView(
        int CharacterSlot, string CharName, int CustomMode, uint Operation, int Class, int Realm,
        int Race, int Gender, ushort CreationModel, byte[] Stats, int NewConstitution, int Unread);

    private static ServerView ServerReads(byte[] payload)
    {
        var p = new PayloadReader(payload);
        int characterSlot = p.U8();
        string charName = p.StringIntLE();
        p.Skip(4);
        int customMode = p.U8();
        p.Skip(6);       // eye size, lip size, eye colour, hair colour, face type, hair style
        p.Skip(3);
        p.Skip(1);       // mood
        p.Skip(9);
        uint operation = p.U8();
        p.Skip(1);       // customise type
        p.Skip(2);
        p.Skip(5);       // location
        p.Skip(5);       // class name
        p.Skip(5);       // race name
        int @class = p.U8();
        int realm = p.U8();
        if (realm > 0)
            characterSlot -= (realm - 1) * 10;
        byte startRaceGender = p.U8();
        int race = startRaceGender & 0x1F;
        int gender = (startRaceGender >> 7) & 0x01;
        ushort creationModel = p.U16LE();
        byte[] stats = [p.U8(), p.U8(), p.U8(), p.U8(), p.U8(), p.U8(), p.U8(), p.U8()];
        p.Skip(43);
        int newConstitution = p.U8();
        return new ServerView(characterSlot, charName, customMode, operation, @class, realm,
            race, gender, creationModel, stats, newConstitution, p.Remaining);
    }

    [Fact]
    public void ServerReadsTheCharacterWeSend()
    {
        ServerView view = ServerReads(CharacterCreateRequest.CreatePayload(Character()));

        Assert.Equal(0, view.CharacterSlot);
        Assert.Equal("Testarms", view.CharName);
        Assert.Equal(0, view.CustomMode);
        Assert.Equal(1u, view.Operation);
        Assert.Equal(2, view.Class);
        Assert.Equal(1, view.Realm);
        Assert.Equal(1, view.Race);
        Assert.Equal(0, view.Gender);
        Assert.Equal(32, view.CreationModel);
        Assert.Equal(new byte[] { 70, 70, 70, 60, 60, 60, 60, 60 }, view.Stats);
        Assert.Equal(70, view.NewConstitution);
        Assert.Equal(1, view.Unread); // the trailing zero the server's comment mentions
    }

    [Fact]
    public void FemaleAndRaceShareOneByte()
    {
        NewCharacter female = Character() with { Race = 0x13, Female = true };
        ServerView view = ServerReads(CharacterCreateRequest.CreatePayload(female));
        Assert.Equal(0x13, view.Race);
        Assert.Equal(1, view.Gender);
    }

    [Theory]
    [InlineData(Realm.Albion, 0, 0)]
    [InlineData(Realm.Midgard, 3, 13)]
    [InlineData(Realm.Hibernia, 9, 29)]
    public void SlotsAreNumberedAcrossRealms(Realm realm, int slot, byte wireSlot)
    {
        byte[] payload = CharacterCreateRequest.CreatePayload(Character(realm: realm, slot: slot));
        Assert.Equal(wireSlot, payload[0]);
        Assert.Equal(slot, ServerReads(payload).CharacterSlot);
    }

    [Theory]
    [InlineData(Realm.Albion, 4)]
    [InlineData(Realm.Midgard, 3)]
    [InlineData(Realm.Hibernia, 9)]
    public void DeleteAddressesTheAccountSlot(Realm accountRealm, int slot)
    {
        ServerView view = ServerReads(CharacterCreateRequest.DeletePayload(accountRealm, slot));
        Assert.Equal(3u, view.Operation);
        Assert.Equal("", view.CharName);
        Assert.Equal(0, view.Realm);
        Assert.Equal(1, view.Unread);

        // HandleDeleteCharacterRequest (1.125+) turns the slot into DbCoreCharacter.AccountSlot,
        // which CreateCharacter stored as slot + realm * 100.
        int r = (int)accountRealm;
        int accountSlot = r * 100 + (view.CharacterSlot - (r - 1) * 10);
        Assert.Equal(slot + r * 100, accountSlot);
    }

    [Theory]
    [InlineData("Ab")]        // server: too short → disconnect
    [InlineData("testarms")]  // server: must start upper-case → disconnect
    [InlineData("Te5tarms")]  // server accepts; we don't
    [InlineData("Test arms")]
    public void RejectsNamesThatWouldGetAPlayerKicked(string name)
    {
        Assert.Throws<ArgumentException>(() => CharacterCreateRequest.CreatePayload(Character(name)));
    }

    [Fact]
    public void RejectsBadSlotsAndMissingRealm()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterCreateRequest.CreatePayload(Character(slot: 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterCreateRequest.DeletePayload(Realm.Albion, -1));
        Assert.Throws<ArgumentException>(() => CharacterCreateRequest.CreatePayload(Character(realm: Realm.None)));
        Assert.Throws<ArgumentException>(() => CharacterCreateRequest.DeletePayload(Realm.None, 0));
    }
}
