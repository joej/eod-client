// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: weapon trails (roadmap M12), as the retail client draws them (daoc-re RE/75 "Styled hit", RE/82 "Weapon
// trail"). A styled hit (0xBC result 0x0B) draws a ribbon behind the attacker's right-hand weapon, between the weapon
// model's TRAILBASE and TRAILTIP nodes (cata_gear.json "trail"; FigureLook marks the model); without both there is none.
// - The style's styles.csv row (an anytime style: its weapon's generic row; no row: the client's defaults) gives the
//   texture (Imagenum -> stylemap.csv: 1 shadow, 10 metaledge, 11 flameedge; another number, none), the colour (its RGB,
//   alpha 0.99) and the duration.
// - Up to 33 pairs of points, relative to the figure's root (the trail moves with the figure), newest first; each frame
//   adds the weapon's two ends. Base u 0, tip u 1. Every older pair's v grows by 3 per second, sliding it along the
//   texture; a new pair's v is (0.2 - t) x 5 in the first 0.2 s (fade-in), (t - (duration - 0.1)) x 5 in the last 0.1 s,
//   else 0. The trail goes at t >= duration.
// Ours: a pair is added every drawn frame (retail: every new animation frame); one trail per attacker, a new styled hit
// replacing it; the Buildup column and Hit Effect aren't used (their meaning isn't traced); how the alpha-less textures
// blend is inferred (shaders/weapon_trail.gdshader).

using DaocAssets.Figures;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private const int TrailPairs = 33;
    private const float TrailScroll = 3f, TrailFadeIn = 0.2f, TrailFadeOut = 0.1f;

    private sealed class Trail
    {
        public required ushort Owner;
        public required Node3D OwnerNode;
        public required Node3D Weapon;
        public required Vector3 Base, Tip;              // in the weapon model's space
        public required MeshInstance3D Mesh;
        public required ImmediateMesh Strip;
        public required ShaderMaterial Material;
        public required Color Colour;
        public required double Born;
        public required float Duration;
        public readonly List<(Vector3 Base, Vector3 Tip, float V)> Pairs = [];
    }

    private StyleLooks? _styleLooks;
    private Dictionary<int, Texture2D?>? _trailTextures;
    private Shader? _trailShader;
    private readonly List<Trail> _trails = [];

    private StyleLooks Styles
    {
        get
        {
            if (_styleLooks is null)
            {
                string path = (_libraryPath ?? "").PathJoin("icons").PathJoin("styles.csv");
                _styleLooks = StyleLooks.Parse(Godot.FileAccess.FileExists(path) ? Godot.FileAccess.GetFileAsString(path) : "");
            }
            return _styleLooks;
        }
    }

    // A style's look for this attack: by the attacker's weapon stance (an anytime style's generic row).
    private StyleLook StyleOf(CombatAnimation c) => Styles.Find(c.StyleId, FigureLook.Stance(GearPath, c.WeaponModel));

    private Texture2D? TrailTexture(int imagenum)
    {
        if (_trailTextures is null)
        {
            _trailTextures = [];
            string dir = (_libraryPath ?? "").PathJoin("models").PathJoin("trails");
            string csv = dir.PathJoin("stylemap.csv");
            if (Godot.FileAccess.FileExists(csv))
                foreach (string line in Godot.FileAccess.GetFileAsString(csv).Split('\n').Skip(1))
                {
                    string[] c = line.TrimEnd('\r').Split(',');
                    if (c.Length >= 2 && int.TryParse(c[0], out int id))
                        _trailTextures[id] = ModelTexturing.Load(System.IO.Path.GetFileNameWithoutExtension(c[1]), dir);
                }
        }
        return _trailTextures.GetValueOrDefault(imagenum);
    }

    // The attacker's right-hand weapon with trail ends, or null.
    private static Node3D? TrailWeapon(Node3D owner)
    {
        foreach (Node n in owner.FindChildren("Gear_*", "BoneAttachment3D", true, false))
            if (n is BoneAttachment3D { BoneName: "Bip01 R Held" } attach)
                foreach (Node child in attach.GetChildren())
                    if (child is Node3D model && model.HasMeta(FigureLook.TrailMeta))
                        return model;
        return null;
    }

    private void StartTrail(CombatAnimation c)
    {
        if (c.Result != CombatAnimation.StyledHit || c.StyleId == 0 || FxOwnerNode(c.AttackerId) is not Node3D owner
            || TrailWeapon(owner) is not Node3D weapon || weapon.GetMeta(FigureLook.TrailMeta).AsVector3Array() is not { Length: 2 } ends)
            return;
        StyleLook look = StyleOf(c);
        GD.Print($"[trail] #{c.AttackerId} style {look.Id}: image {look.Imagenum}, {look.DurationMs} ms, rgb {look.Red},{look.Green},{look.Blue}; " +
                 $"ends {weapon.GlobalTransform * ends[0]} -> {weapon.GlobalTransform * ends[1]}, owner at {owner.GlobalPosition}");
        StopTrail(c.AttackerId);
        _trailShader ??= GD.Load<Shader>("res://shaders/weapon_trail.gdshader");
        Texture2D? texture = TrailTexture(look.Imagenum);
        var material = new ShaderMaterial { Shader = _trailShader };
        material.SetShaderParameter("textured", texture is not null);
        if (texture is not null)
            material.SetShaderParameter("trail_texture", texture);
        var strip = new ImmediateMesh();
        var mesh = new MeshInstance3D { Name = "WeaponTrail", Mesh = strip, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        owner.AddChild(mesh);
        _trails.Add(new Trail
        {
            Owner = c.AttackerId, OwnerNode = owner, Weapon = weapon, Base = ends[0], Tip = ends[1], Mesh = mesh, Strip = strip,
            Material = material, Colour = new Color(look.Red / 255f, look.Green / 255f, look.Blue / 255f, 0.99f), Born = _seconds,
            Duration = Math.Max(look.DurationMs, 1) / 1000f,
        });
    }

    private void StopTrail(ushort owner)
    {
        for (int i = _trails.Count - 1; i >= 0; i--)
            if (_trails[i].Owner == owner)
            {
                if (IsInstanceValid(_trails[i].Mesh))
                    _trails[i].Mesh.QueueFree();
                _trails.RemoveAt(i);
            }
    }

    private void UpdateTrails(float dt)
    {
        for (int i = _trails.Count - 1; i >= 0; i--)
        {
            Trail tr = _trails[i];
            float t = (float)(_seconds - tr.Born);
            if (t >= tr.Duration || !IsInstanceValid(tr.OwnerNode) || !IsInstanceValid(tr.Weapon) || !IsInstanceValid(tr.Mesh))
            {
                if (IsInstanceValid(tr.Mesh))
                    tr.Mesh.QueueFree();
                _trails.RemoveAt(i);
                continue;
            }
            for (int k = 0; k < tr.Pairs.Count; k++)
                tr.Pairs[k] = tr.Pairs[k] with { V = tr.Pairs[k].V + dt * TrailScroll };
            float v = t < TrailFadeIn ? (TrailFadeIn - t) * 5 : t > tr.Duration - TrailFadeOut ? (t - (tr.Duration - TrailFadeOut)) * 5 : 0;
            Transform3D toOwner = tr.OwnerNode.GlobalTransform.AffineInverse() * tr.Weapon.GlobalTransform;
            tr.Pairs.Insert(0, (toOwner * tr.Base, toOwner * tr.Tip, v));
            if (tr.Pairs.Count > TrailPairs)
                tr.Pairs.RemoveRange(TrailPairs, tr.Pairs.Count - TrailPairs);
            tr.Strip.ClearSurfaces();
            if (tr.Pairs.Count < 2)
                continue;
            tr.Strip.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip, tr.Material);
            foreach ((Vector3 b, Vector3 tip, float pv) in tr.Pairs)
            {
                tr.Strip.SurfaceSetColor(tr.Colour);
                tr.Strip.SurfaceSetUV(new Vector2(0, pv));
                tr.Strip.SurfaceAddVertex(b);
                tr.Strip.SurfaceSetColor(tr.Colour);
                tr.Strip.SurfaceSetUV(new Vector2(1, pv));
                tr.Strip.SurfaceAddVertex(tip);
            }
            tr.Strip.SurfaceEnd();
        }
    }
}
