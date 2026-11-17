// SPDX-License-Identifier: GPL-3.0-or-later
//
// A dungeon's doors (roadmap M11): which door of which placed room or prop has which id. The client names a dungeon
// piece's door by zone × 1,000,000 + room × 100 + n, n from the node name door001…009 (daoc-re's doors note, RE/80,
// inferred there); a prop's doors take its room's number (its "collision and doors join the room's", RE/83). Checked
// 2026-11-17 against the one dungeon door in OpenDAoC's database that fits a door piece: Hibernia's Darkspire 98000001,
// room 0 (darkspire_01, door001). Its other one, Galladoria 191000501, names room 5, which has no door piece (a
// hand-added row, probably). A name with nine digits is an id of its own.
// Written per zone as doors.csv: id, the zone.csv row the door's model is placed on, the door's node, and that
// placement's position; the client plays the door's animation (GltfExporter's door parts) when 0x99 arrives.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DaocAssets.Nif;

namespace DaocAssets.Zone;

/// <summary>One door of a dungeon: its id, the zone.csv row (0-based, after the header) of the model it belongs to,
/// its node name ("door001") and that placement's region position.</summary>
public readonly record struct DungeonDoor(uint Id, int Row, string Door, float X, float Y, float Z);

public static class DungeonDoors
{
    public const string CsvHeader = "id,row,door,x,y,z";

    /// <summary>A dungeon door's id: zone × 1,000,000 + room × 100 + n (door001 → 1), or the nine digits of a
    /// "door" + 9 digits name. Null when the name has no number or the parts don't fit.</summary>
    public static uint? Id(int zone, int room, string door)
    {
        string digits = new(door.Skip(4).TakeWhile(char.IsAsciiDigit).ToArray());
        if (!door.StartsWith("door", StringComparison.OrdinalIgnoreCase) || digits.Length < 2
            || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
            return null;
        if (digits.Length == 9)
            return (uint)n;
        long id = zone * 1_000_000L + room * 100L + n;
        return n is >= 1 and <= 99 && room is >= 0 and < 10_000 && id <= uint.MaxValue ? (uint)id : null;
    }

    /// <summary>The doors a converted model can open: its glb's animations named like a door node (GltfExporter).</summary>
    public static IReadOnlyList<string> InGlb(string glbPath)
    {
        try
        {
            byte[] head = File.ReadAllBytes(glbPath);
            if (head.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x46546C67)   // "glTF"
                return [];
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
            using JsonDocument json = JsonDocument.Parse(head.AsMemory(20, length));
            if (!json.RootElement.TryGetProperty("animations", out JsonElement anims))
                return [];
            return anims.EnumerateArray()
                .Select(a => a.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "")
                .Where(NifFile.IsDoorNode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException)
        {
            return [];
        }
    }

    public static string ToCsv(IEnumerable<DungeonDoor> doors)
    {
        var sb = new StringBuilder(CsvHeader).Append('\n');
        foreach (DungeonDoor d in doors)
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{d.Id},{d.Row},{d.Door},{d.X:0.#},{d.Y:0.#},{d.Z:0.#}\n"));
        return sb.ToString();
    }

    public static List<DungeonDoor> ReadCsv(string csv)
    {
        var doors = new List<DungeonDoor>();
        foreach (string line in csv.Split('\n').Skip(1))
        {
            string[] c = line.Trim().Split(',');
            if (c.Length >= 6 && uint.TryParse(c[0], out uint id) && int.TryParse(c[1], out int row)
                && float.TryParse(c[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && float.TryParse(c[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                && float.TryParse(c[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                doors.Add(new DungeonDoor(id, row, c[2], x, y, z));
        }
        return doors;
    }
}
