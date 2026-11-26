// SPDX-License-Identifier: GPL-3.0-or-later
//
// Firelight (Modern particles only; ours, retail has none): one warm light at a model's fire, lighting the logs, ground and
// tents around it, its strength wavering as a flame does. It is part of the model's template (NifParticles), so every
// placed copy has its own: a plain light in the "firelight" group (groups survive the template's Duplicate, scripts
// needn't), which WorldView updates each frame through Update: hidden under Classic particles, flickering under Modern,
// each copy at its own point in the flicker. It fades out with distance, so a zone's far fires cost nothing.

using Godot;

namespace EodClient;

public static class FireLight
{
    public const string Group = "firelight";
    private const float Energy = 2.5f;

    /// <summary>The light for a model's glowing systems: at their middle, raised to flame height (at least 0.6 m above
    /// them, or a light in the embers only grazes the ground), in their colour, reaching about five times as far as the
    /// fire itself spreads (4 to 12 m).</summary>
    public static OmniLight3D For(System.Collections.Generic.IReadOnlyList<(Vector3 At, Color Colour, float Reach)> fire)
    {
        Vector3 at = Vector3.Zero;
        Color colour = Colors.Black;
        float reach = 0;
        foreach ((Vector3 a, Color c, float r) in fire)
        {
            at += a / fire.Count;
            colour += c / fire.Count;
            reach = Mathf.Max(reach, r);
        }
        float top = Mathf.Max(colour.R, Mathf.Max(colour.G, colour.B));
        var light = new OmniLight3D
        {
            Name = "FireLight",
            Position = at + Vector3.Up * 0.6f,
            LightColor = top > 0 ? colour / top : new Color(1f, 0.6f, 0.3f),
            OmniRange = Mathf.Clamp(reach * 5, 4, 12),
            OmniAttenuation = 1.2f,
            LightEnergy = Energy,
            LightSpecular = 0,
            ShadowEnabled = false,
            DistanceFadeEnabled = true,
            DistanceFadeBegin = 40,
            DistanceFadeLength = 15,
            Visible = false,
        };
        light.AddToGroup(Group, persistent: true);
        return light;
    }

    /// <summary>Every firelight in the tree: shown under Modern particles, each flickering from its own phase.</summary>
    public static void Update(SceneTree tree)
    {
        bool on = NifParticles.ModernOn;
        float now = Time.GetTicksMsec() / 1000f;
        foreach (Node n in tree.GetNodesInGroup(Group))
        {
            if (n is not OmniLight3D light)
                continue;
            if (light.Visible != on)
                light.Visible = on;
            if (!on)
                continue;
            // Its own phase, from where it stands; two slow waves and a quick one: between about 75% and 115%.
            Vector3 p = light.GlobalPosition;
            float t = now + (p.X * 12.9898f + p.Z * 78.233f) % 100f;
            float wave = 0.55f * Mathf.Sin(t * 2.1f) + 0.3f * Mathf.Sin(t * 5.3f + 1.7f) + 0.15f * Mathf.Sin(t * 13.7f + 0.4f);
            light.LightEnergy = Energy * (0.95f + 0.2f * wave);
        }
    }
}
