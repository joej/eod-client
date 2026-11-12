// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the zones' night lamps (lights.csv; built in WorldView.Zone.LightSteps) follow the game clock as retail's do
// (daoc-re RE/82): they fade on over 3 s from 17:00 and off over 3 s from 7:00, outdoors and in dungeons, and entering the
// world at night finds them already on. Without a game time (offline, before the server's 0x7E) it is day: they stay off.

using DaocProtocol.Session;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const uint LampsOnAt = 17 * 3600_000, LampsOffAt = 7 * 3600_000;   // game time of day, ms
    private const float LampFadeSeconds = 3f;

    // Retail lamps against Godot's lights: the sun's fitted strength (ApplySkyTime), both being retail fixed-function
    // diffuse lights added in display space (ours; not fitted for lamps on their own).
    private const float LampStrength = 1.85f;

    private float _lampLevel;          // 0 off .. 1 on
    private bool _lampsKnown;          // the first game time seen: on or off at once
    private int _lampCount = -1;       // lamps last given the level (a zone streamed in adds more)
    private double _lampsCheckedAt = -1;

    private static bool LampsLit(uint msOfDay) => msOfDay >= LampsOnAt || msOfDay < LampsOffAt;

    private void UpdateLamps(double delta)
    {
        float target = GameMillisOfDay is uint ms && LampsLit(ms) ? 1f : 0f;
        float level = !_lampsKnown && GameMillisOfDay is not null ? target
            : Mathf.MoveToward(_lampLevel, target, (float)delta / LampFadeSeconds);
        _lampsKnown |= GameMillisOfDay is not null;
        bool fading = level != _lampLevel;
        if (fading && level is 0f or 1f)
            GD.Print($"[world] lamps {(level > 0 ? "on" : "off")} at {(GameMillisOfDay is uint t ? GameClock.Format(t) : "-")}");
        _lampLevel = level;
        // Applied while fading, and twice a second for lamps a newly streamed zone brought.
        if (!fading && _seconds - _lampsCheckedAt < 0.5)
            return;
        _lampsCheckedAt = _seconds;
        Godot.Collections.Array<Node> lamps = GetTree().GetNodesInGroup(LampGroup);
        if (!fading && lamps.Count == _lampCount)
            return;
        _lampCount = lamps.Count;
        foreach (Node n in lamps)
            if (n is OmniLight3D lamp)
            {
                lamp.LightEnergy = lamp.GetMeta("energy").AsSingle() * LampStrength * level;
                lamp.Visible = level > 0;
            }
    }
}
