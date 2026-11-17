// SPDX-License-Identifier: GPL-3.0-or-later
//
// A dungeon's layout (roadmap M11): 280 zones are built from rooms instead of a heightmap. Their data archive holds
// - dungeon.chunk: one piece name per line ("bdung1hall1.NIF"); the line number is the piece index;
// - dungeon.place: one row per room, "piece, x, y, z, angle, axisX, axisY, axisZ[, sky, waterHeight, waterType, …]";
//   the row number is the room id (portals.csv, dungeon.water.csv and dungeon.prop name rooms by it);
// - dungeon.prop: "piece, x, y, z, angle, axisX, axisY, axisZ, unused, room, scale": a door, plug or ornament placed in
//   its room's frame (the offsets are a few hundred units, beside rooms thousands apart).
// How the client places them (daoc-re's dungeon note, 2026-11-10 and 2026-11-11): every room hangs under one node turned
// 180° about the vertical at (32000, 32000, 16000), the capital cities' rule with height 16000 instead of 8000
// (ZoneLayout.ParseCity, which the cities' NPCs confirm); a room is turned by its angle about its axis, then moved to
// its x, y, z; a prop is turned and moved the same way inside its room, then scaled. The piece names go through the
// dungeon's nifproxy.csv when the model loads (AssetLibrary.Dungeon), not here.
// All 280 dungeon zones have the zone offset (8192, 8192), so whether the origin is region or zone-relative (zone-local
// 23808) can't be told apart; the region value is used, as for the cities.
// Checked 2026-11-17 against where the server stands NPCs (OpenDAoC database, mpk dungeoncheck --points): a floor of the
// placed rooms is under 392 of 392 in Coruscating Mine (220), 313 of 318 in Muire Tomb (221) and 369 of 370 in zone 060;
// the dungeon's mirror image (which fits the portals equally well) holds 5, 105 and 26.
// The install (2026-11-17): 13,832 rooms in 282 archives, every piece index valid; every room axis is vertical, or has an
// angle of 0 (up to 0.003): rooms only turn about the vertical.

using System.Globalization;
using System.Numerics;

namespace DaocAssets.Zone;

/// <summary>One room of a dungeon: a piece placed in the dungeon's frame.</summary>
/// <param name="Piece">The piece file as dungeon.chunk names it (before nifproxy.csv).</param>
/// <param name="Sky">dungeon.place column 9: the zone's sky model shows while you are in this room.</param>
/// <param name="WaterHeight">Column 10: the room's water surface height (0 when the row has none).</param>
/// <param name="WaterType">Column 11 as written (4 water, 6 the second liquid, −1 or 0 the default).</param>
public readonly record struct DungeonRoom(
    int Id, string Piece, float X, float Y, float Z, float Angle, float AxisX, float AxisY, float AxisZ,
    bool Sky = false, float WaterHeight = 0, int WaterType = 0);

/// <summary>A prop (door, plug, ornament) placed in its room's frame.</summary>
public readonly record struct DungeonProp(
    string Piece, int Room, float X, float Y, float Z, float Angle, float AxisX, float AxisY, float AxisZ, float Scale);

/// <summary>A dungeon zone's rooms and props, and their placement as zone fixtures.</summary>
public sealed class DungeonLayout
{
    /// <summary>The node every room hangs under, in region coordinates (it is turned 180° about the vertical).</summary>
    public static readonly (float X, float Y, float Z) Origin = (32000, 32000, 16000);

    public required IReadOnlyList<DungeonRoom> Rooms { get; init; }
    public required IReadOnlyList<DungeonProp> Props { get; init; }

    /// <summary>Rows that didn't parse or name a piece that isn't in dungeon.chunk (skipped), for the report.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    /// <summary>True for a dungeon zone's archive (it has <c>dungeon.chunk</c> and <c>dungeon.place</c>).</summary>
    public static bool IsDungeon(Mpk.MpkArchive dat) =>
        dat.TryGetEntry("dungeon.chunk", out _) && dat.TryGetEntry("dungeon.place", out _);

    /// <summary>The layout in a dungeon zone's data archive.</summary>
    public static DungeonLayout Read(Mpk.MpkArchive dat) =>
        Parse(Text(dat, "dungeon.chunk") ?? "", Text(dat, "dungeon.place") ?? "", Text(dat, "dungeon.prop") ?? "");

    public static DungeonLayout Parse(string chunk, string place, string prop)
    {
        string[] pieces = chunk.Split('\n').Select(l => l.Trim()).ToArray();
        // A trailing newline leaves one empty name at the end; an empty line elsewhere still counts as an index.
        var rooms = new List<DungeonRoom>();
        var props = new List<DungeonProp>();
        var skipped = new List<string>();

        int row = 0;
        foreach (string line in place.Split('\n'))
        {
            if (line.Trim().Length == 0)
                continue;
            int id = row++;   // the room id is the row number among the non-empty rows (ours: no blank lines in the install)
            float[]? f = Floats(line, 8);
            if (f is null || Piece(pieces, f[0]) is not string name)
            {
                skipped.Add($"dungeon.place room {id}: {line.Trim()}");
                continue;
            }
            float[] all = AllFloats(line);
            rooms.Add(new DungeonRoom(id, name, f[1], f[2], f[3], f[4], f[5], f[6], f[7],
                Sky: all.Length > 8 && all[8] == 1,
                WaterHeight: all.Length > 9 ? all[9] : 0,
                WaterType: all.Length > 10 ? (int)all[10] : 0));
        }

        foreach (string line in prop.Split('\n'))
        {
            if (line.Trim().Length == 0)
                continue;
            float[]? f = Floats(line, 11);
            if (f is null || Piece(pieces, f[0]) is not string name || f[9] < 0 || f[9] >= row)
            {
                skipped.Add($"dungeon.prop: {line.Trim()}");
                continue;
            }
            props.Add(new DungeonProp(name, (int)f[9], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[10] > 0 ? f[10] : 1));
        }
        return new DungeonLayout { Rooms = rooms, Props = props, Skipped = skipped };
    }

    /// <summary>
    /// The rooms and props as zone fixtures (region coordinates), in the manifest's rotation form: a turn of Tilt radians
    /// about (AxisX, AxisY, AxisZ) in the zone frame (<see cref="ZonePlacement.Rotation"/>). A prop whose room was
    /// skipped is skipped too.
    /// </summary>
    public ZoneLayout ToZoneLayout()
    {
        var fixtures = new List<ZoneFixture>();
        var roomAt = new Dictionary<int, (Quaternion Turn, Vector3 At)>();
        foreach (DungeonRoom r in Rooms)
        {
            Quaternion turn = Top * Turn(r.Angle, r.AxisX, r.AxisY, r.AxisZ);
            Vector3 at = Vector3.Transform(ModelFrame(r.X, r.Y, r.Z), Top);
            roomAt[r.Id] = (turn, at);
            fixtures.Add(Fixture(r.Piece, $"room {r.Id}", turn, at, 1) with { Room = r.Id });
        }
        foreach (DungeonProp p in Props)
        {
            if (!roomAt.TryGetValue(p.Room, out var room))
                continue;
            Quaternion turn = room.Turn * Turn(p.Angle, p.AxisX, p.AxisY, p.AxisZ);
            Vector3 at = room.At + Vector3.Transform(ModelFrame(p.X, p.Y, p.Z), room.Turn);
            fixtures.Add(Fixture(p.Piece, $"prop in room {p.Room}", turn, at, p.Scale) with { Room = p.Room });
        }
        return new ZoneLayout { Fixtures = fixtures };
    }

    /// <summary>Where a point given in the dungeon's own frame (the frame rooms are placed in: dungeon.place,
    /// dungeon.water) lands, in region coordinates.</summary>
    public static Vector3 FramePoint(Vector3 p)
    {
        Vector3 g = Vector3.Transform(ModelFrame(p.X, p.Y, p.Z), Top);
        return new Vector3(Origin.X + g.X, Origin.Y + g.Z, Origin.Z + g.Y);
    }

    /// <summary>Where a point of a room's piece (NIF space, the piece's own units) lands, in region coordinates.</summary>
    public static Vector3 RegionPoint(DungeonRoom room, Vector3 nifPoint)
    {
        Quaternion turn = Top * Turn(room.Angle, room.AxisX, room.AxisY, room.AxisZ);
        Vector3 g = Vector3.Transform(ModelFrame(room.X, room.Y, room.Z), Top)
            + Vector3.Transform(ModelFrame(nifPoint.X, nifPoint.Y, nifPoint.Z), turn);
        return new Vector3(Origin.X + g.X, Origin.Y + g.Z, Origin.Z + g.Y);
    }

    // The node above the rooms: 180° about the vertical, as a capital city's pieces are placed (angle 180 in the manifest).
    private static readonly Quaternion Top = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI);

    // Positions and turns are composed in a model's own frame (glTF / Godot axes, Y up), where the manifest's rotation
    // acts: a NIF point (x, y, z) is (x, z, −y) there (GltfExporter), and a placed model's frame vector (gx, gy, gz)
    // is the region offset (gx, gz, gy) (WorldView.ToGodot). A capital's piece, turned 180°, lands at
    // (32000 − x, 32000 + y, 8000 + z): the rule its NPCs confirm.
    private static Vector3 ModelFrame(float x, float y, float z) => new(x, z, -y);

    // A room's or prop's own turn, as the manifest turns a fixture by its 3D Angle about its axis (ZonePlacement.Rotation).
    // The angle turns the other way from a fixture's 3D Angle (ZonePlacement.Rotation) — Sandbox's builder also negates
    // it. Measured 2026-11-17 (mpk dungeoncheck): with −angle, 26,950 of 27,984 portals.csv links across all 282 dungeon
    // archives have their two doorways within 64 units (the median link in zone 221 under 1); with +angle, half the rooms
    // of zone 221 stand up to 1,600 units off their neighbours' doorways.
    private static Quaternion Turn(float angle, float ax, float ay, float az) =>
        angle == 0 || (ax == 0 && ay == 0 && az == 0)
            ? Quaternion.Identity
            : Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(ax, az, ay)), -angle);

    private static ZoneFixture Fixture(string piece, string label, Quaternion turn, Vector3 at, float scale)
    {
        turn = Quaternion.Normalize(turn);
        if (turn.W < 0)
            turn = Quaternion.Negate(turn);
        float half = MathF.Acos(Math.Clamp(turn.W, -1f, 1f));
        float s = MathF.Sin(half);
        float tilt = 2 * half;
        // The manifest's axis is in the zone frame: model frame (gx, gy, gz) is zone (gx, gz, gy).
        (float ax, float ay, float az) = s < 1e-6f ? (0f, 0f, 0f) : (turn.X / s, turn.Z / s, turn.Y / s);
        if (s < 1e-6f)
            tilt = 0;
        return new ZoneFixture(piece, label, Origin.X + at.X, Origin.Y + at.Z, Origin.Z + at.Y, 0, scale,
            Tilt: tilt, AxisX: ax, AxisY: ay, AxisZ: az);
    }

    private static string? Piece(string[] pieces, float index)
    {
        int i = (int)index;
        return i == index && i >= 0 && i < pieces.Length && pieces[i].Length > 0 ? pieces[i] : null;
    }

    // The first n comma-separated numbers of a row, or null when it has fewer.
    private static float[]? Floats(string line, int n)
    {
        float[] all = AllFloats(line);
        return all.Length >= n ? all[..n] : null;
    }

    private static float[] AllFloats(string line)
    {
        var values = new List<float>();
        foreach (string cell in line.Split(','))
        {
            if (!float.TryParse(cell.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                break;
            values.Add(v);
        }
        return values.ToArray();
    }

    private static string? Text(Mpk.MpkArchive dat, string name) =>
        dat.TryGetEntry(name, out Mpk.MpkEntry e) ? System.Text.Encoding.Latin1.GetString(dat.Extract(e)) : null;
}
