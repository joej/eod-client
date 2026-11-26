// SPDX-License-Identifier: GPL-3.0-or-later
//
// Particle systems in a model: fire, smoke, mist, sparks, fountains, spell effects. The client has no particle table of
// its own; each effect is a NIF's own NetImmerse/Gamebryo particle system (a NiParticles shape, NiAutoNormalParticles or
// NiRotatingParticles in 4.x, driven by a NiParticleSystemController with a chain of modifiers), which the client runs as
// stock Gamebryo does. This reads them as data for the client to rebuild: the emitter's place, the emission (rate, speed,
// direction cone, lifetime, start box), the particle size and count, the modifiers (gravity, colour over life, grow and
// fade, spin, bomb) and the texture and blend mode the shape draws with.
//
// Layouts: NifTools' nif.xml (GPL-3.0); the controller's layout is the same in 4.x and 10.1.0.0. The reading of the
// fields (the controller size as the particle radius; the declination as a tilt from the emitter's up axis, the planar
// angle as a turn about it; the NiParticleRotation speeds DAoC writes in degrees) follows the Offline DAoC / DAoC Sandbox
// pipeline (GPL-3.0, nif_static.py particle_systems and nif_particles.gd).

using System.Numerics;

namespace DaocAssets.Nif;

/// <summary>
/// One particle system of a model. Positions and the emitter are in the model's NIF space (row-vector matrix, units);
/// speeds in units per second, angles in radians, times in seconds.
/// </summary>
public sealed record NifParticleSystem
{
    public required string Name { get; init; }
    /// <summary>Where particles are born: the emitter node's world transform in the model (NIF space).</summary>
    public required Matrix4x4 Emitter { get; init; }
    /// <summary>The texture the particles draw with (the shape's, or its parents'), or null.</summary>
    public string? Texture { get; init; }
    /// <summary>The NiAlphaProperty flags that apply (bit 0 blend, bits 1-4 source, 5-8 destination factor), or 0.</summary>
    public ushort AlphaFlags { get; init; }
    /// <summary>The particle radius.</summary>
    public float Size { get; init; }
    public float Speed { get; init; }
    public float SpeedRandom { get; init; }
    /// <summary>Tilt of the emission direction from the emitter's up axis, and the cone around it.</summary>
    public float Declination { get; init; }
    public float DeclinationVariation { get; init; }
    /// <summary>Turn of the emission direction about the emitter's up axis, and its variation.</summary>
    public float PlanarAngle { get; init; }
    public float PlanarAngleVariation { get; init; }
    public float EmitRate { get; init; }
    public float Lifetime { get; init; }
    public float LifetimeRandom { get; init; }
    /// <summary>The box (full sizes along the emitter's axes) particles are born in.</summary>
    public Vector3 StartRandom { get; init; }
    /// <summary>The system's particle count: the most alive at once.</summary>
    public int MaxParticles { get; init; }
    public float EmitStart { get; init; }
    public float EmitStop { get; init; }
    /// <summary>The controller's own time span and flags (bits 1-2: 0 loop, 1 reverse, 2 clamp = play once).</summary>
    public float StartTime { get; init; }
    public float StopTime { get; init; }
    public ushort ControllerFlags { get; init; }
    /// <summary>The controller's emit flags (low byte: emit at the birth rate; high byte: spawn on death).</summary>
    public ushort EmitFlags { get; init; }
    public IReadOnlyList<NifParticleModifier> Modifiers { get; init; } = [];
    /// <summary>When the system is shown: the visibility keys of the NiVisController on it or its nearest animated parent
    /// (an effect's later bursts are hidden shapes shown partway in), or null: always.</summary>
    public NifVisibility? Visibility { get; init; }
}

/// <summary>A NiVisController's keys (time in seconds, shown or not) and its controller's span and flags.</summary>
public sealed record NifVisibility((float Time, bool Visible)[] Keys, ushort Flags, float StartTime, float StopTime);

/// <summary>A particle modifier (the controller's chain).</summary>
public abstract record NifParticleModifier;

/// <summary>NiGravity: a pull along a direction (planar) or toward a point (spherical), in units per second squared.</summary>
public sealed record NifParticleGravity(float Decay, float Force, bool Spherical, Vector3 Position, Vector3 Direction) : NifParticleModifier;

/// <summary>NiParticleColorModifier: the colour (RGBA, 0..1) over each particle's life (key times 0..1 of the life).</summary>
public sealed record NifParticleColors((float Time, Vector4 Color)[] Keys) : NifParticleModifier;

/// <summary>NiParticleGrowFade: seconds to grow from nothing after birth and to shrink to nothing before death.</summary>
public sealed record NifParticleGrowFade(float Grow, float Fade) : NifParticleModifier;

/// <summary>NiParticleRotation: each particle spins (radians per second).</summary>
public sealed record NifParticleSpin(bool RandomAxis, Vector3 Axis, float Speed) : NifParticleModifier;

/// <summary>NiParticleBomb: a push away from a point or plane for a while.</summary>
public sealed record NifParticleBomb(float Decay, float Duration, float DeltaV, float Start, uint DecayType, uint Symmetry,
    Vector3 Position, Vector3 Direction) : NifParticleModifier;

/// <summary>NiParticleMeshModifier: each particle draws a small mesh (a card) instead of a sprite: the first mesh's size
/// (its two largest spans, units), texture and alpha flags.</summary>
public sealed record NifParticleMeshes(int Count, float Width = 0, float Height = 0, string? Texture = null, ushort AlphaFlags = 0)
    : NifParticleModifier;

/// <summary>A particle mesh's look, as a reader resolves it: its two largest spans, texture and alpha flags.</summary>
internal sealed record NifParticleMeshLook(float Width, float Height, string? Texture, ushort AlphaFlags);

/// <summary>Reads particle blocks (both NIF versions) and puts each system together.</summary>
internal static class NifParticleReader
{
    // The blocks as read; refs are block indices (-1 none).
    internal sealed record Controller(int Next, ushort Flags, float StartTime, float StopTime, float Speed, float SpeedRandom,
        float Declination, float DeclinationVariation, float PlanarAngle, float PlanarAngleVariation, float Size,
        float EmitStart, float EmitStop, float EmitRate, float Lifetime, float LifetimeRandom, ushort EmitFlags,
        Vector3 StartRandom, int Emitter, int Particles, int Modifier);

    internal sealed record Modifier(int Next, NifParticleModifier? Value, int ColorData = -1);

    internal sealed record ColorData((float Time, Vector4 Color)[] Keys);

    internal sealed record VisController(int Next, ushort Flags, float StartTime, float StopTime, int Data);

    internal sealed record VisData((float Time, bool Visible)[] Keys);

    // A mesh modifier's meshes, kept until the reader can resolve them.
    internal sealed record MeshRefs(int[] Meshes) : NifParticleModifier;

    /// <summary>A particle shape found in the tree: where it is, what it draws with, and its first controller.</summary>
    /// <summary>A particle shape found in the tree: where it is, its first controller, whether it or a parent is hidden,
    /// the first visibility controller on it or a parent (-1 none), and what it draws with.</summary>
    internal sealed record Shape(string Name, Matrix4x4 World, int Controller, bool Hidden, string? Texture, ushort AlphaFlags,
        int VisController = -1);

    internal static bool IsParticleShape(string type) =>
        type is "NiParticles" or "NiAutoNormalParticles" or "NiRotatingParticles" or "NiParticleMeshes";

    /// <summary>Reads a particle-system block's body (after its type string / block marker); null for other types.</summary>
    internal static object? Read(ref NifCursor c, string type) => type switch
    {
        "NiParticleSystemController" => ReadController(ref c),
        "NiGravity" => ReadModifier(ref c, (ref NifCursor k) =>
        {
            float decay = k.F32(), force = k.F32();
            uint field = k.U32();
            Vector3 pos = k.Vec3(), dir = k.Vec3();
            return new NifParticleGravity(decay, force, field == 1, pos, dir);
        }),
        "NiParticleColorModifier" => ReadColorModifier(ref c),
        "NiParticleGrowFade" => ReadModifier(ref c, (ref NifCursor k) => new NifParticleGrowFade(k.F32(), k.F32())),
        "NiParticleRotation" => ReadModifier(ref c, (ref NifCursor k) =>
        {
            bool random = k.U8() != 0;
            Vector3 axis = k.Vec3();
            return new NifParticleSpin(random, axis, k.F32());
        }),
        "NiParticleBomb" => ReadModifier(ref c, (ref NifCursor k) =>
        {
            float decay = k.F32(), duration = k.F32(), deltaV = k.F32(), start = k.F32();
            uint decayType = k.U32(), symmetry = k.U32();
            return new NifParticleBomb(decay, duration, deltaV, start, decayType, symmetry, k.Vec3(), k.Vec3());
        }),
        "NiParticleMeshModifier" => ReadModifier(ref c, (ref NifCursor k) =>
        {
            uint n = k.U32();
            if (n > 1000) throw new NifFormatException($"Implausible particle mesh count {n}.");
            var meshes = new int[n];
            for (int i = 0; i < n; i++) meshes[i] = k.I32();
            return new MeshRefs(meshes);
        }),
        "NiColorData" => new ColorData(ReadColorKeys(ref c)),
        "NiVisController" => ReadVisController(ref c),
        "NiVisData" => ReadVisData(ref c),
        _ => null,
    };

    private delegate NifParticleModifier Body(ref NifCursor c);

    // NiParticleModifier: Next Modifier, Controller (4.0.0.2 on), then the type's own fields.
    private static Modifier ReadModifier(ref NifCursor c, Body body)
    {
        int next = c.I32();
        c.I32();
        return new Modifier(next, body(ref c));
    }

    private static Modifier ReadColorModifier(ref NifCursor c)
    {
        int next = c.I32();
        c.I32();
        return new Modifier(next, null, c.I32());
    }

    private static Controller ReadController(ref NifCursor c)
    {
        // NiTimeController: Next Controller, Flags, Frequency, Phase, Start Time, Stop Time, Target.
        int next = c.I32();
        ushort flags = c.U16();
        c.F32(); c.F32();
        float start = c.F32(), stop = c.F32();
        c.I32();
        float speed = c.F32(), speedRandom = c.F32();
        float decl = c.F32(), declVar = c.F32(), planar = c.F32(), planarVar = c.F32();
        c.Vec3();                                         // Unknown Normal
        c.F32(); c.F32(); c.F32(); c.F32();               // Unknown Color
        float size = c.F32(), emitStart = c.F32(), emitStop = c.F32();
        c.U8();                                           // Unknown Byte (reset)
        float rate = c.F32(), life = c.F32(), lifeRandom = c.F32();
        ushort emitFlags = c.U16();
        Vector3 startRandom = c.Vec3();
        int emitter = c.I32();
        c.U16(); c.F32(); c.U32(); c.U32(); c.U16();      // unknowns (spawn generations and the like)
        int particles = c.U16();
        c.U16();                                          // Num Valid
        for (int i = 0; i < particles * 40; i++) c.U8();  // the particles as saved (velocity, lifetime, ...)
        c.I32();                                          // Unknown Link
        int modifier = c.I32();                           // Particle Extra: the first modifier
        c.I32();                                          // Unknown Link 2
        c.U8();                                           // Trailer
        return new Controller(next, flags, start, stop, speed, speedRandom, decl, declVar, planar, planarVar, size,
            emitStart, emitStop, rate, life, lifeRandom, emitFlags, startRandom, emitter, particles, modifier);
    }

    // NiVisController: NiTimeController, then its NiVisData.
    private static VisController ReadVisController(ref NifCursor c)
    {
        int next = c.I32();
        ushort flags = c.U16();
        c.F32(); c.F32();
        float start = c.F32(), stop = c.F32();
        c.I32();
        return new VisController(next, flags, start, stop, c.I32());
    }

    // NiVisData: count, then keys of time + a byte (no key type).
    private static VisData ReadVisData(ref NifCursor c)
    {
        uint n = c.U32();
        if (n > 100_000) throw new NifFormatException($"Implausible key count {n}.");
        var keys = new (float, bool)[n];
        for (int i = 0; i < n; i++)
            keys[i] = (c.F32(), c.U8() != 0);
        return new VisData(keys);
    }

    /// <summary>The first visibility controller on a controller chain starting at <paramref name="first"/>, or -1.</summary>
    internal static int FindVisController(int first, Func<int, object?> raw, Func<int, int> nextController)
    {
        int seen = 0;
        for (int i = first; i >= 0 && seen++ < 32; i = nextController(i))
            if (raw(i) is VisController)
                return i;
        return -1;
    }

    // KeyGroup<Color4>: count, key type (if any keys), keys of time + RGBA (+ tangents for quadratic, + TBC).
    private static (float, Vector4)[] ReadColorKeys(ref NifCursor c)
    {
        uint n = c.U32();
        if (n == 0)
            return [];
        if (n > 10_000) throw new NifFormatException($"Implausible colour key count {n}.");
        uint type = c.U32();
        if (type is < 1 or > 5) throw new NifFormatException($"Key type {type}.");
        var keys = new (float, Vector4)[n];
        for (int i = 0; i < n; i++)
        {
            float t = c.F32();
            keys[i] = (t, new Vector4(c.F32(), c.F32(), c.F32(), c.F32()));
            if (type == 2) for (int k = 0; k < 8; k++) c.F32();
            if (type == 3) for (int k = 0; k < 3; k++) c.F32();
        }
        return keys;
    }

    /// <summary>
    /// The finished systems: for each visible particle shape, its NiParticleSystemController (found along the shape's
    /// controller chain) with its modifiers. <paramref name="raw"/> gives a block's parsed particle data (or null),
    /// <paramref name="nextController"/> the next controller after a controller block of another type, and
    /// <paramref name="world"/> a node's world transform in the model. Without an emitter node, particles are born at the
    /// shape itself.
    /// </summary>
    internal static List<NifParticleSystem> Assemble(IEnumerable<Shape> shapes, Func<int, object?> raw,
        Func<int, int> nextController, Func<int, Matrix4x4?> world, Func<int, NifParticleMeshLook?>? meshLook = null)
    {
        var systems = new List<NifParticleSystem>();
        foreach (Shape s in shapes)
        {
            // A hidden shape shows only when a visibility controller brings it in (an effect's later burst).
            NifVisibility? vis = null;
            if (s.VisController >= 0 && raw(s.VisController) is VisController vc && raw(vc.Data) is VisData vd && vd.Keys.Length > 0)
                vis = new NifVisibility(vd.Keys, vc.Flags, vc.StartTime, vc.StopTime);
            if (s.Hidden && vis is null)
                continue;
            Controller? ctl = null;
            int seen = 0;
            for (int i = s.Controller; i >= 0 && seen++ < 32; )
            {
                if (raw(i) is Controller found) { ctl = found; break; }
                i = nextController(i);
            }
            if (ctl is null)
                continue;
            var mods = new List<NifParticleModifier>();
            seen = 0;
            for (int m = ctl.Modifier; m >= 0 && seen++ < 32; )
            {
                if (raw(m) is not Modifier mod)
                    break;
                if (mod.Value is MeshRefs refs)
                {
                    NifParticleMeshLook? look = refs.Meshes.Select(i => meshLook?.Invoke(i)).FirstOrDefault(l => l is not null);
                    mods.Add(look is null ? new NifParticleMeshes(refs.Meshes.Length)
                        : new NifParticleMeshes(refs.Meshes.Length, look.Width, look.Height, look.Texture, look.AlphaFlags));
                }
                else if (mod.Value is not null)
                    mods.Add(mod.Value);
                else if (raw(mod.ColorData) is ColorData cd && cd.Keys.Length > 0)
                    mods.Add(new NifParticleColors(cd.Keys));
                m = mod.Next;
            }
            Matrix4x4 at = (ctl.Emitter >= 0 ? world(ctl.Emitter) : null) ?? s.World;
            systems.Add(new NifParticleSystem
            {
                Name = s.Name, Emitter = at, Texture = s.Texture, AlphaFlags = s.AlphaFlags,
                Size = ctl.Size, Speed = ctl.Speed, SpeedRandom = ctl.SpeedRandom,
                Declination = ctl.Declination, DeclinationVariation = ctl.DeclinationVariation,
                PlanarAngle = ctl.PlanarAngle, PlanarAngleVariation = ctl.PlanarAngleVariation,
                EmitRate = ctl.EmitRate, Lifetime = ctl.Lifetime, LifetimeRandom = ctl.LifetimeRandom,
                StartRandom = ctl.StartRandom, MaxParticles = ctl.Particles, EmitStart = ctl.EmitStart, EmitStop = ctl.EmitStop,
                StartTime = ctl.StartTime, StopTime = ctl.StopTime, ControllerFlags = ctl.Flags, EmitFlags = ctl.EmitFlags,
                Modifiers = mods, Visibility = vis,
            });
        }
        return systems;
    }
}
