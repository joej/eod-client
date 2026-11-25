// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: Classic or Modern lighting (ClassicLighting, Settings → Lighting; --lighting classic|modern overrides the
// setting, for paired shots). With Classic, every mesh that enters the world is drawn with the classic light (converted
// the frame after it's added, when whatever built it has set its materials), the sun casts no shadows, and the sky's
// ambient and sun colours go to the shaders as global parameters.

using System.Collections.Generic;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private readonly List<MeshInstance3D> _toLight = [];
    private const float PlayerLightHeight = 128;   // units above the feet

    // Before anything of the world is built: the shaders made from here on are classic or not.
    private void StartLighting()
    {
        string[] args = OS.GetCmdlineUserArgs();
        int at = System.Array.IndexOf(args, "--lighting");
        bool modern = at >= 0 && at + 1 < args.Length ? args[at + 1] == "modern" : ClientSettings.ModernLighting;
        ClassicLighting.On = !modern;
        GD.Print($"[world] lighting: {(modern ? "modern" : "classic")}");
        if (!ClassicLighting.On)
            return;
        GetTree().NodeAdded += QueueForLight;
        RenderingServer.GlobalShaderParameterSet("classic_units_per_metre", 1f / RegionMetresPerUnit);
        // Until a sky file sets them (none in a dungeon): the default environment's grey ambient and a white sun overhead.
        PushClassicLight(new Color(0.6f, 0.6f, 0.6f), Colors.White, Vector3.Up);
    }

    private void StopLighting()
    {
        if (ClassicLighting.On)
            GetTree().NodeAdded -= QueueForLight;
    }

    private void QueueForLight(Node node)
    {
        if (node is MeshInstance3D mi && IsAncestorOf(mi))
            _toLight.Add(mi);
    }

    private void UpdateLighting()
    {
        // The client's own light at the player (daoc-re RE/100 §8), following the character: at its x, y and about 128
        // units above the feet (inferred by daoc-re from z). Retail lights the player's own figure with 0.5 of it instead
        // (not built: ours lights it fully).
        if (ClassicLighting.On && _body is not null)
            RenderingServer.GlobalShaderParameterSet("classic_player", _body.GlobalPosition + Vector3.Up * (PlayerLightHeight * RegionMetresPerUnit));
        if (_toLight.Count == 0)
            return;
        foreach (MeshInstance3D mi in _toLight)
            ClassicLighting.Convert(mi);
        _toLight.Clear();
    }

    /// <summary>Sun shadows: the setting's, never in a dungeon, and never with Classic lighting (retail casts none).</summary>
    private bool SunShadowsOn => ClientSettings.SunShadows && !_inDungeon && !ClassicLighting.On;

    // The classic light's inputs, in display space: ambient and sun colours (already times their amounts), and the
    // direction toward the sun (Godot world space).
    private static void PushClassicLight(Color ambient, Color sun, Vector3 towardSun)
    {
        if (!ClassicLighting.On)
            return;
        RenderingServer.GlobalShaderParameterSet("classic_ambient", new Vector3(ambient.R, ambient.G, ambient.B));
        RenderingServer.GlobalShaderParameterSet("classic_sun", new Vector3(sun.R, sun.G, sun.B));
        RenderingServer.GlobalShaderParameterSet("classic_sun_dir", towardSun.Normalized());
    }
}
