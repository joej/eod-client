// SPDX-License-Identifier: GPL-3.0-or-later
//
// The seam between the protocol library and Godot (roadmap M5). WorldView drains whole
// ServerPackets from the connection each frame and hands them here; this routes each opcode to a
// parser and raises a typed event. Nothing above the protocol library touches raw bytes, and
// nothing in the protocol library touches Godot — the bridge is the only place they meet.
//
// It replaces the old "drain and count" loop: opcodes we don't yet interpret are still counted
// (so the status overlay's packet/ping totals keep working) and simply have no subscribers.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

/// <summary>
/// Routes incoming server packets to typed events. Construct it once per world session, subscribe
/// to the events you care about, then feed it every drained <see cref="ServerPacket"/>.
/// </summary>
public sealed class NetworkBridge
{
    /// <summary>An in-view entity moved or changed facing (0xA1 ObjectUpdate, the common case).</summary>
    public event Action<ObjectUpdate>? ObjectMoved;

    /// <summary>An NPC/mob entered view (0xDA NPCCreate).</summary>
    public event Action<NpcCreate>? NpcSpawned;

    /// <summary>Another player entered view (0x4B PlayerCreate).</summary>
    public event Action<PlayerCreate>? PlayerSpawned;

    /// <summary>A living's visible gear (0x15 EquipmentUpdate): other players and NPCs, never us.</summary>
    public event Action<EquipmentUpdate>? EquipmentChanged;

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

    /// <summary>The server answered a keep-alive (0x29). Kept so the overlay can count pings.</summary>
    public event Action? PingReplied;

    /// <summary>Every packet, handled or not, for the overlay's running total.</summary>
    public int PacketCount { get; private set; }

    /// <summary>Opcodes with no handler yet, for a one-line "what are we ignoring" summary.</summary>
    public IReadOnlyDictionary<byte, int> UnhandledCounts => _unhandled;

    private readonly Dictionary<byte, int> _unhandled = new();

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
                case PingRequest.ReplyCode:
                    PingReplied?.Invoke();
                    break;
                default:
                    _unhandled[packet.Code] = _unhandled.GetValueOrDefault(packet.Code) + 1;
                    break;
            }
        }
        catch (InvalidDataException e)
        {
            GD.PrintErr($"[bridge] 0x{packet.Code:X2} ({packet.Payload.Length} bytes) failed to parse: {e.Message}");
        }
    }
}
