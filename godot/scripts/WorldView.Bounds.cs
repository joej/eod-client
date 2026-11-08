// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: zone edge walls (roadmap M8). Each zone's bounds.csv (from the zone's bound.csv, see
// DaocAssets.Zone.ZoneBounds) holds invisible walls along its coasts and map edges. The character's every
// horizontal step goes through them, at any height: running into one slides along it, a corner stops.
// Never drawn, no message, and only the character collides (not the camera, not NPCs), as in retail.

using System.Collections.Generic;
using DaocAssets.Zone;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private readonly List<BoundSegment> _walls = [];   // the loaded zones' walls, gathered per step
    private double _wallNoteAt;                       // when the log last noted a wall (throttle)

    private static IEnumerable<bool> BoundSteps(ZoneParts parts)
    {
        string csv = parts.Dir.Length == 0 ? "" : parts.Dir.PathJoin("bounds.csv");
        if (csv.Length == 0 || !Godot.FileAccess.FileExists(csv))
            yield break;
        parts.Bounds = ZoneBounds.ReadCsv(Godot.FileAccess.GetFileAsString(csv));
        yield return true;
    }

    // Where a step from `from` to `to` (Godot metres) ends against the loaded zones' walls; height unchanged.
    private Vector3 BoundedStep(Vector3 from, Vector3 to)
    {
        _walls.Clear();
        foreach (ZoneParts z in _zones.Values)
            _walls.AddRange(z.Bounds);
        if (_walls.Count == 0)
            return to;
        (float fx, float fy, float fz) = ToRegion(from);
        (float tx, float ty, float tz) = ToRegion(to);
        (float x, float y) = ZoneBounds.Step(_walls, fx, fy, tx, ty);
        if (x == tx && y == ty)
            return to;
        if (_seconds >= _wallNoteAt)
        {
            _wallNoteAt = _seconds + 5;
            GD.Print($"[world] held by a zone edge wall at ({x:0}, {y:0})");
        }
        Vector3 r = ToGodot(x, y, tz);
        return new Vector3(r.X, to.Y, r.Z);
    }
}
