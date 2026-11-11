// SPDX-License-Identifier: GPL-3.0-or-later
//
// The seam between the protocol library and a game front end (roadmap M5). The Godot client's
// WorldView drains whole ServerPackets from the connection each frame and hands them here; this routes
// each opcode to a parser and raises a typed event. Nothing above the protocol library touches raw
// bytes, and nothing here touches Godot (it logs through a callback), so it is unit-tested.
//
// It replaces the old "drain and count" loop: opcodes we don't yet interpret are still counted
// (so the status overlay's packet/ping totals keep working) and simply have no subscribers.

using DaocProtocol.Net;
using DaocProtocol.Packets;
namespace DaocProtocol.Session;

/// <summary>
/// Routes incoming server packets to typed events. Construct it once per world session, subscribe
/// to the events you care about, then feed it every drained <see cref="ServerPacket"/>.
/// </summary>
public sealed class NetworkBridge(Action<string>? log = null)
{
    /// <summary>An in-view entity moved or changed facing (0xA1 ObjectUpdate, the common case).</summary>
    public event Action<ObjectUpdate>? ObjectMoved;

    /// <summary>An NPC/mob entered view (0xDA NPCCreate).</summary>
    public event Action<NpcCreate>? NpcSpawned;

    /// <summary>Another player entered view (0x4B PlayerCreate).</summary>
    public event Action<PlayerCreate>? PlayerSpawned;

    /// <summary>A living's visible gear (0x15 EquipmentUpdate): other players and NPCs, never us.</summary>
    public event Action<EquipmentUpdate>? EquipmentChanged;

    /// <summary>Another player moved (S→C 0xA9) or turned (0xBA, keyed by session id); both carry its attack
    /// mode (weapons drawn).</summary>
    public event Action<PlayerPosition>? PlayerMoved;
    public event Action<PlayerHeading>? PlayerTurned;

    /// <summary>Our own attack mode as the server has it (S→C 0x74): weapons drawn or put away.</summary>
    public event Action<bool>? AttackModeChanged;

    /// <summary>An NPC's quest glyph changed (0x4C kind 7): object id and the new state.</summary>
    public event Action<ushort, QuestIndicator>? QuestChanged;

    /// <summary>How fast the server lets us run (0xB6): run no faster, or its speed check snaps us back.</summary>
    public event Action<MaxSpeed>? MaxSpeedChanged;

    /// <summary>A static world object entered view (0xD9 ObjectCreate).</summary>
    public event Action<ObjectCreate>? ObjectSpawned;

    /// <summary>An entity left view (0xA2 RemoveObject) or was destroyed (0xE1 ObjectDelete).</summary>
    public event Action<ushort>? ObjectRemoved;

    /// <summary>A chat or system message arrived (0xAF Message), for the on-screen chat log.</summary>
    public event Action<ChatMessage>? ChatReceived;

    /// <summary>The server moved the player within its region (0x04 CharacterJump, e.g. /jump).</summary>
    public event Action<CharacterJump>? Jumped;

    /// <summary>
    /// The server moved the player to another region (0xB7). The packets after it belong to the
    /// re-entry (ClientSession.ChangeRegionAsync), so the subscriber must stop draining at once.
    /// </summary>
    public event Action<RegionChanged>? RegionChanged;

    /// <summary>
    /// The server ended our time in the world (0xA4) without ClientSession.QuitAsync waiting for it
    /// (a /quit sent another way). The character is gone; the subscriber stops draining and finishes
    /// the quit (ClientSession.FinishQuitAsync).
    /// </summary>
    public event Action<ServerPacket>? QuitArrived;

    /// <summary>Our own vitals (0xAD): health, mana, endurance, concentration, and whether we sit.</summary>
    public event Action<StatusUpdate>? StatusChanged;

    /// <summary>Our XP, realm and bounty points, specialisation points (0x91).</summary>
    public event Action<PointsUpdate>? PointsChanged;

    /// <summary>Our money (0xFA).</summary>
    public event Action<MoneyUpdate>? MoneyChanged;

    /// <summary>Who we are: level, class, race, guild, titles, realm rank (0x16 subcode 3).</summary>
    public event Action<PlayerInfo>? PlayerInfoChanged;

    /// <summary>The usable-skill list arrived in full (0x16 subcode 1, all pages): <see cref="Skills"/>.</summary>
    public event Action<IReadOnlyList<SkillEntry>>? SkillsChanged;

    /// <summary>A list caster's spell lines arrived in full (0x16 subcode 2, through the footer).</summary>
    public event Action<IReadOnlyList<SpellLinePage>>? SpellLinesChanged;

    /// <summary>Weapon damage, weapon skill and armour factor (0x16 subcode 5).</summary>
    public event Action<WeaponStats>? WeaponStatsChanged;

    /// <summary>The crafting skills (0x16 subcode 8).</summary>
    public event Action<CraftingSkills>? CraftingSkillsChanged;

    /// <summary>A cooldown started or ended (0xD6); <see cref="SkillBook.CooldownLeft"/> has the times.</summary>
    public event Action<DisableSkills>? CooldownsChanged;

    /// <summary>The skills and spell lines as last sent in full.</summary>
    public SkillBook Skills { get; } = new();

    /// <summary>A player died (0xAE): us or one in view.</summary>
    public event Action<PlayerDied>? PlayerDied;

    /// <summary>A player is alive again (0x89): released or resurrected.</summary>
    public event Action<PlayerRevived>? PlayerRevived;

    /// <summary>A countdown window opened or closed (0xF3; after death, the release timer).</summary>
    public event Action<TimerWindow>? TimerChanged;

    /// <summary>The game clock (0x7E): at world entry and every 15 minutes.</summary>
    public event Action<GameTime>? TimeChanged;

    /// <summary>The character's stats or resists (0xFB; <see cref="StatsUpdate.IsResists"/> says which).</summary>
    public event Action<StatsUpdate>? StatsChanged;

    /// <summary>What the character carries (0x02 for its own slots; <see cref="Inventory"/> holds the result).</summary>
    public event Action<Inventory>? InventoryChanged;

    /// <summary>The character's own items, slot by slot.</summary>
    public Inventory Inventory { get; } = new();

    /// <summary>How much the character carries and can carry (0xBD).</summary>
    public event Action<Encumbrance>? EncumbranceChanged;

    /// <summary>A text window (0xC4): an item's description and the like. The bool is true when the window continues the
    /// previous one (that packet's "more" flag was set), whose lines it adds to.</summary>
    public event Action<DetailWindow, bool>? DetailWindowArrived;
    private bool _detailContinues;

    /// <summary>The server answered a keep-alive (0x29), echoing our timestamp (Environment.TickCount when sent): for the
    /// overlay's count and the lag meter's round trip (PingRequest.RoundTrip).</summary>
    public event Action<uint>? PingReplied;

    /// <summary>Every packet, handled or not, for the overlay's running total.</summary>
    public int PacketCount { get; private set; }

    /// <summary>Opcodes with no handler yet, for a one-line "what are we ignoring" summary.</summary>
    public IReadOnlyDictionary<byte, int> UnhandledCounts => _unhandled;

    private readonly Dictionary<byte, int> _unhandled = new();

    // 0x16 carries several lists, by its first byte. Subcodes not read yet count as unhandled.
    private void VariousUpdate(ServerPacket packet)
    {
        switch (packet.Payload.Length > 0 ? packet.Payload[0] : -1)
        {
            case SkillListPage.Subcode:
                if (Skills.Add(SkillListPage.Parse(packet.Payload)))
                    SkillsChanged?.Invoke(Skills.Skills);
                break;
            case SpellLinePage.Subcode:
                if (Skills.Add(SpellLinePage.Parse(packet.Payload)))
                    SpellLinesChanged?.Invoke(Skills.SpellLines);
                break;
            case PlayerInfo.Subcode:
                PlayerInfoChanged?.Invoke(PlayerInfo.Parse(packet.Payload));
                break;
            case WeaponStats.Subcode:
                WeaponStatsChanged?.Invoke(WeaponStats.Parse(packet.Payload));
                break;
            case CraftingSkills.Subcode:
                CraftingSkillsChanged?.Invoke(CraftingSkills.Parse(packet.Payload));
                break;
            default:
                _unhandled[packet.Code] = _unhandled.GetValueOrDefault(packet.Code) + 1;
                break;
        }
    }

    /// <summary>
    /// Parse one packet and raise its event. A malformed payload is swallowed with a log line
    /// rather than killing the frame: one bad packet must not stop the world from updating.
    /// </summary>
    public void Dispatch(ServerPacket packet)
    {
        PacketCount++;
        try
        {
            switch (packet.Code)
            {
                case ObjectUpdate.Code:
                    ObjectMoved?.Invoke(ObjectUpdate.Parse(packet.Payload));
                    break;
                case NpcCreate.Code:
                    NpcSpawned?.Invoke(NpcCreate.Parse(packet.Payload));
                    break;
                case PlayerCreate.Code:
                    PlayerSpawned?.Invoke(PlayerCreate.Parse(packet.Payload));
                    break;
                case PlayerPosition.Code:
                    PlayerMoved?.Invoke(PlayerPosition.Parse(packet.Payload));
                    break;
                case PlayerHeading.Code:
                    PlayerTurned?.Invoke(PlayerHeading.Parse(packet.Payload));
                    break;
                case AttackMode.Code:
                    AttackModeChanged?.Invoke(AttackMode.IsOn(packet.Payload));
                    break;
                case VisualEffect.Code:
                    if (VisualEffect.Parse(packet.Payload) is { Quest: { } quest } effect)
                        QuestChanged?.Invoke(effect.ObjectId, quest);
                    else
                        _unhandled[packet.Code] = _unhandled.GetValueOrDefault(packet.Code) + 1;
                    break;
                case MaxSpeed.Code:
                    MaxSpeedChanged?.Invoke(MaxSpeed.Parse(packet.Payload));
                    break;
                case ObjectCreate.Code:
                    ObjectSpawned?.Invoke(ObjectCreate.Parse(packet.Payload));
                    break;
                case EquipmentUpdate.Code:
                    EquipmentChanged?.Invoke(EquipmentUpdate.Parse(packet.Payload));
                    break;
                case RemoveObject.Code:
                    ObjectRemoved?.Invoke(RemoveObject.Parse(packet.Payload).ObjectId);
                    break;
                case ObjectDelete.Code:
                    ObjectRemoved?.Invoke(ObjectDelete.Parse(packet.Payload).ObjectId);
                    break;
                case ChatMessage.Code:
                    ChatReceived?.Invoke(ChatMessage.Parse(packet.Payload));
                    break;
                case CharacterJump.Code:
                    Jumped?.Invoke(CharacterJump.Parse(packet.Payload));
                    break;
                case DaocProtocol.Packets.RegionChanged.Code:
                    RegionChanged?.Invoke(DaocProtocol.Packets.RegionChanged.Parse(packet.Payload));
                    break;
                case StatusUpdate.Code:
                    StatusChanged?.Invoke(StatusUpdate.Parse(packet.Payload));
                    break;
                case PointsUpdate.Code:
                    PointsChanged?.Invoke(PointsUpdate.Parse(packet.Payload));
                    break;
                case DisableSkills.Code:
                {
                    DisableSkills d = DisableSkills.Parse(packet.Payload);
                    Skills.Apply(d, Environment.TickCount64);
                    CooldownsChanged?.Invoke(d);
                    break;
                }
                case MoneyUpdate.Code:
                    MoneyChanged?.Invoke(MoneyUpdate.Parse(packet.Payload));
                    break;
                case PlayerInfo.Code:
                    VariousUpdate(packet);
                    break;
                case DaocProtocol.Packets.PlayerDied.Code:
                    PlayerDied?.Invoke(DaocProtocol.Packets.PlayerDied.Parse(packet.Payload));
                    break;
                case DaocProtocol.Packets.PlayerRevived.Code:
                    PlayerRevived?.Invoke(DaocProtocol.Packets.PlayerRevived.Parse(packet.Payload));
                    break;
                case TimerWindow.Code:
                    TimerChanged?.Invoke(TimerWindow.Parse(packet.Payload));
                    break;
                case GameTime.Code:
                    TimeChanged?.Invoke(GameTime.Parse(packet.Payload));
                    break;
                case StatsUpdate.Code:
                    StatsChanged?.Invoke(StatsUpdate.Parse(packet.Payload));
                    break;
                case InventoryUpdate.Code:
                    if (Inventory.Apply(InventoryUpdate.Parse(packet.Payload)))
                        InventoryChanged?.Invoke(Inventory);
                    break;
                case Encumbrance.Code:
                    EncumbranceChanged?.Invoke(Encumbrance.Parse(packet.Payload));
                    break;
                case DetailWindow.Code:
                    DetailWindow window = DetailWindow.Parse(packet.Payload);
                    DetailWindowArrived?.Invoke(window, _detailContinues);
                    _detailContinues = window.More != 0;
                    break;
                case PingRequest.ReplyCode:
                    PingReplied?.Invoke(PingRequest.ParseReply(packet.Payload));
                    break;
                case QuitReply.Code:
                    QuitArrived?.Invoke(packet);
                    break;
                default:
                    _unhandled[packet.Code] = _unhandled.GetValueOrDefault(packet.Code) + 1;
                    break;
            }
        }
        catch (InvalidDataException e)
        {
            log?.Invoke($"[bridge] 0x{packet.Code:X2} ({packet.Payload.Length} bytes) failed to parse: {e.Message}");
        }
    }
}
