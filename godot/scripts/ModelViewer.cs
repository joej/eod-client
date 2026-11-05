// SPDX-License-Identifier: GPL-3.0-or-later
//
// A standalone viewer for a converted model: loads a .glb at runtime, frames it, and lets you
// orbit with the mouse. No network — this is the asset-pipeline proof (NIF -> glb -> Godot).
//
// Run it from the project directory, pointing at a converted model:
//   Godot --path godot --scene scenes/ModelViewer.tscn -- --model private/assets/x/model.glb
// Run it from the repo root; the --model path may be relative to there, or absolute. (Or set the
// Model export in the editor.) Left-drag orbits, wheel zooms.
// --textures <dir> points at a folder of extracted .dds (defaults to the .glb's own folder, where
// tools/pipeline.sh puts them); each material's <name>.dds is loaded and set as its albedo.
// --shot <path.png> renders one frame of the framed model and writes it to <path>, then quits —
// a headless-friendly way to capture the converted model (combine with --headless for CI).
// A classic figure (e.g. bcommonm.glb) holds every body-part variant as its own mesh, named
// "<Slot><n>[b]:<id>" (Body1:0, Legs2:36, HeadA1:19). By default the viewer shows the first variant
// of each slot and hides the low-detail bodies (Lod*) and cloaks; --parts Body2,Legs1,... shows
// exactly the named variants instead (the part before ':').
// A Catacombs race figure (cata_fig<N>.glb) shows every hair style and mesh variant until dressed; --row
// <model id> (default: the first using it) gives the model's own skins, and --look takes the
// seven appearance bytes in hex, in the character list's order (eye size, lip size, eye colour, hair
// colour, face, hair style, mood), e.g. --look 44,44,20,01,00,20,00; with no --look the bytes are 0.
// --wear dresses it in worn items by model id (objects.csv), each optionally with its slot and extension:
// --wear 81,82,83,84,85 (a studded set), --wear 41:6,57 (a chain hauberk on body mesh 6, a cloak),
// --wear 10@3,11@59,21@824 (a short sword, a round shield, a studded helm); --active 10 (0x15's active
// weapons byte in hex: sword and shield in use; default FF, none); a third field is the item's dye (a tints.csv
// row) or guild emblem number (logo << 9 | pattern << 7 | primary << 3 | secondary): --wear 26@57:1:2600 is a
// cloak with logo 5 on pattern 1; --hood puts a cloak's hood up; --drawn
// shows them in attack mode (weapons in hand; with no --anim, the weapon's combat idle).

using System;
using System.Linq;
using Godot;

namespace EodClient;

public partial class ModelViewer : Node3D
{
    /// <summary>Path to a .glb to load. Overridden by a --model &lt;path&gt; command-line argument.</summary>
    [Export(PropertyHint.GlobalFile, "*.glb,*.gltf")]
    public string Model { get; set; } = "";

    private Camera3D _camera = null!;
    private Node3D _pivot = null!;
    private Label _status = null!;
    private float _yaw = 0.6f;
    private float _pitch = 0.4f;
    private float _distance = 4f;
    private bool _dragging;

    public override void _Ready()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.12f, 0.13f, 0.16f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.45f, 0.45f, 0.5f),
            },
        });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45, 35, 0) });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-20, -120, 0), LightEnergy = 0.4f });

        _pivot = new Node3D();
        AddChild(_pivot);
        _camera = new Camera3D();
        AddChild(_camera);

        var overlay = new CanvasLayer();
        _status = new Label { Position = new Vector2(16, 16) };
        overlay.AddChild(_status);
        AddChild(overlay);

        string path = ModelPathFromArgs() ?? Model;
        LoadModel(path);

        // --shot captures the framed model to a PNG and exits — a verifiable, headless-friendly
        // render of the converted glb. The viewport image is only valid once a frame has drawn,
        // so this awaits the post-draw signal before grabbing it.
        string? shot = ShotPathFromArgs();
        if (!string.IsNullOrEmpty(shot))
            _ = SaveShotAndQuit(shot);
    }

    private static string? ModelPathFromArgs() => ResolvePath(ArgValue("--model"));
    private static string? TextureDirFromArgs() => ResolvePath(ArgValue("--textures"));
    private static string? ShotPathFromArgs() => ResolvePath(ArgValue("--shot"));

    private async System.Threading.Tasks.Task SaveShotAndQuit(string path)
    {
        // Let the model and camera settle, then wait for the frame to finish drawing.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image img = GetViewport().GetTexture().GetImage();
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            DirAccess.MakeDirRecursiveAbsolute(dir);
        Error err = img.SavePng(path);
        GD.Print(err == Error.Ok ? $"[viewer] shot -> {path}" : $"[viewer] shot failed: {err}");
        GetTree().Quit(err == Error.Ok ? 0 : 1);
    }

    private static string? ArgValue(string name)
    {
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }

    // Godot's process working directory is the project dir (set by --path), not the shell's, so a
    // repo-relative path like "private/assets/x.glb" would resolve under godot/ and not be found.
    // Resolve a relative path against PWD (the shell's directory at launch) so both relative (from
    // wherever you ran the command) and absolute paths work.
    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || System.IO.Path.IsPathRooted(path))
            return path;
        string cwd = OS.GetEnvironment("PWD");
        string baseDir = string.IsNullOrEmpty(cwd) ? System.IO.Directory.GetCurrentDirectory() : cwd;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, path));
    }

    private void LoadModel(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _status.Text = "No model. Pass --model <path-to.glb> or set the Model property.";
            FrameBounds(new Aabb(new Vector3(-1, -1, -1), new Vector3(2, 2, 2)));
            return;
        }
        if (!Godot.FileAccess.FileExists(path))
        {
            _status.Text = $"Model not found:\n{path}";
            GD.PrintErr($"[viewer] model not found: {path}");
            return;
        }

        // Load a .glb from an arbitrary path at runtime (not an imported res:// resource).
        var doc = new GltfDocument();
        var state = new GltfState();
        Error err = doc.AppendFromFile(path, state);
        if (err != Error.Ok)
        {
            _status.Text = $"Failed to load ({err}):\n{path}";
            return;
        }
        ModelTexturing.TagLayers(state);     // the extra texture layers (dark/detail maps, face decals)
        Node scene = doc.GenerateScene(state);
        _pivot.AddChild(scene);

        // Texture from a folder of extracted .dds. Default to the model's own folder, since the
        // asset pipeline (tools/pipeline.sh) gathers textures beside the .glb; --textures overrides.
        string textureDir = TextureDirFromArgs() ?? System.IO.Path.GetDirectoryName(path) ?? "";
        int textured = ModelTexturing.Apply(scene, textureDir);
        string parts = ChooseFigureParts(scene, ArgValue("--parts"));
        string look = ApplyLook(scene, path, textureDir);
        string anim = PlayClip(scene, ArgValue("--anim") ?? _combatIdle, ArgValue("--anim-time"));

        Aabb bounds = ComputeBounds(scene);
        FrameBounds(bounds);
        GD.Print($"[viewer] loaded {System.IO.Path.GetFileName(path)} bounds={bounds.Size} textured={textured}");
        _status.Text = $"{System.IO.Path.GetFileName(path)}\n" +
            $"size {bounds.Size.X:F1} x {bounds.Size.Y:F1} x {bounds.Size.Z:F1}" +
            (textured > 0 ? $" · {textured} textured" : " · untextured") + "\n" +
            (parts.Length > 0 ? $"parts: {parts}\n" : "") +
            (anim.Length > 0 ? $"clip: {anim}\n" : "") +
            (look.Length > 0 ? $"look:{look}\n" : "") +
            "left-drag orbit · wheel zoom";
    }

    // A Catacombs figure (models/cata<id>.glb beside catacombs.csv): dressed in --look's appearance bytes.
    private static string ApplyLook(Node scene, string path, string textureDir)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string dir = System.IO.Path.GetDirectoryName(path) ?? "";
        // A race figure, cata_fig<N>.glb; --row <model id> picks whose default skins it wears (else the
        // first model id drawn with it).
        if (!name.StartsWith("cata_fig", StringComparison.Ordinal) || !int.TryParse(name[8..], out int fig3) || scene is not Node3D figure)
            return "";
        var rows = WorldView.ReadCataFigures(System.IO.Path.Combine(dir, "catacombs.csv"));
        int id = int.TryParse(ArgValue("--row"), out int r) ? r : rows.Where(kv => kv.Value == fig3).Select(kv => kv.Key).DefaultIfEmpty(0).Min();
        byte[] b = (ArgValue("--look") ?? "0,0,0,0,0,0,0").Split(',')
            .Select(x => Convert.ToByte(x.Trim(), 16)).Concat(new byte[7]).Take(7).ToArray();
        var look = new DaocProtocol.Packets.CharacterLook(b[0], b[1], b[2], b[3], b[4], b[5], b[6]);
        string lib = System.IO.Path.GetDirectoryName(dir) ?? "";
        string options = System.IO.Path.Combine(dir, $"fig3_{fig3}.json");
        var worn = (ArgValue("--wear") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => (Slot: w.Contains('@') ? byte.Parse(w.Split('@')[0]) : (byte)0, Parts: w.Split('@')[^1].Split(':')))
            .Select(w => (w.Slot, w.Parts, Paint: w.Parts.Length > 2 ? int.Parse(w.Parts[2]) : 0))
            .Select(w => new DaocProtocol.Packets.WornItem(w.Slot, ushort.Parse(w.Parts[0]), w.Parts.Length > 1 ? byte.Parse(w.Parts[1]) : (byte)0,
                (ushort)(w.Paint & 0xFFFF), 0, w.Paint > 0xFFFF))
            .ToList();
        byte active = Convert.ToByte(ArgValue("--active") ?? "FF", 16);
        bool hood = OS.GetCmdlineUserArgs().Contains("--hood");
        return FigureLook.Apply(figure, options, look, System.IO.Path.Combine(lib, "overrides"), textureDir, lib) +
            FigureLook.Dress(figure, options, System.IO.Path.Combine(dir, "cata_items.json"), System.IO.Path.Combine(dir, "cata_models.json"),
                System.IO.Path.Combine(dir, "colours.json"),
                id, worn, hood, false, System.IO.Path.Combine(lib, "overrides"), textureDir, lib) +
            FigureLook.Gear(figure, options, System.IO.Path.Combine(dir, "cata_gear.json"), System.IO.Path.Combine(dir, "colours.json"), dir, worn, active, false, hood,
                Drawn, System.IO.Path.Combine(lib, "overrides"), textureDir, lib) + $" (row {id})" +
            (Drawn ? $" drawn, {(_combatIdle = FigureLook.CombatIdle(System.IO.Path.Combine(dir, "cata_gear.json"), worn, active))}" : "");
    }

    private static bool Drawn => OS.GetCmdlineUserArgs().Contains("--drawn");
    private static string? _combatIdle;   // set by dressing with --drawn: the clip to play when --anim isn't given

    // Plays one of the model's animation clips (a figure converted with mpk figure has idle, walk, run,
    // back): --anim names it (default idle, else the first); --anim-time T holds it at T seconds, so a
    // --shot shows a chosen frame. Returns "name (n clips)", or "" for a model with none.
    private static string PlayClip(Node scene, string? want, string? at)
    {
        if (scene.FindChildren("*", "AnimationPlayer", true, false).FirstOrDefault() is not AnimationPlayer player)
            return "";
        string[] clips = player.GetAnimationList();
        if (clips.Length == 0)
            return "";
        string clip = want is not null && clips.Contains(want) ? want : clips.Contains("idle") ? "idle" : clips[0];
        player.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
        player.Play(clip);
        if (float.TryParse(at, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float t))
        {
            player.Seek(t, true);
            player.Pause();
        }
        return $"{clip} ({string.Join(", ", clips)})";
    }

    private static readonly System.Text.RegularExpressions.Regex PartName =
        new(@"^([A-Za-z]+?)(\d+)(b?)(?:[:_]\d+)?$");

    // For a figure with part variants (mesh names like "Body1:0"): shows one variant per slot and
    // hides the rest. Returns the shown parts, or "" when the model isn't a figure.
    private static string ChooseFigureParts(Node scene, string? wanted)
    {
        var meshes = FindMeshes(scene).ToList();
        var named = meshes.Select(m => (Mesh: m, Match: PartName.Match(m.Name.ToString())))
            .Where(x => x.Match.Success).ToList();
        if (named.Count < meshes.Count / 2 || named.Count < 4)
            return "";
        var show = new System.Collections.Generic.HashSet<MeshInstance3D>();
        if (!string.IsNullOrEmpty(wanted))
        {
            var want = wanted.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var x in named)
                if (want.Contains(PartOf(x.Mesh)))
                    show.Add(x.Mesh);
        }
        else
        {
            foreach (var slot in named.GroupBy(x => x.Match.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
            {
                if (slot.Key.Equals("Lod", StringComparison.OrdinalIgnoreCase)
                    || slot.Key.Equals("Cloak", StringComparison.OrdinalIgnoreCase))
                    continue;
                var first = slot.OrderBy(x => x.Match.Groups[3].Value)          // "1" before "1b"
                    .ThenBy(x => int.Parse(x.Match.Groups[2].Value)).First();
                show.Add(first.Mesh);
            }
        }
        foreach (var x in named)
            x.Mesh.Visible = show.Contains(x.Mesh);
        return string.Join(", ", show.Select(PartOf).Order());
    }

    // "Body1:0" (or "Body1_0" once Godot has sanitised the ':') -> "Body1".
    private static string PartOf(MeshInstance3D m)
    {
        var x = PartName.Match(m.Name.ToString());
        return x.Success ? x.Groups[1].Value + x.Groups[2].Value + x.Groups[3].Value : m.Name.ToString();
    }

    // Recentre the model on the pivot and set the orbit distance to fit it in view.
    private void FrameBounds(Aabb bounds)
    {
        Vector3 centre = bounds.Position + bounds.Size / 2f;
        foreach (Node child in _pivot.GetChildren())
            if (child is Node3D n)
                n.Position -= centre;

        float radius = Mathf.Max(bounds.Size.X, Mathf.Max(bounds.Size.Y, bounds.Size.Z)) * 0.5f;
        _distance = Mathf.Max(radius * 2.5f, 1f);
        // --orbit "YAW,PITCH,ZOOM[,BONE]" (degrees, degrees, distance factor, a skeleton bone to look at):
        // fixed views for shots, e.g. "90,5,0.2,Bip01 Head" = a figure's head from its side.
        if (ArgValue("--orbit")?.Split(',') is { Length: >= 3 } o
            && float.TryParse(o[0], System.Globalization.CultureInfo.InvariantCulture, out float yaw)
            && float.TryParse(o[1], System.Globalization.CultureInfo.InvariantCulture, out float pitch)
            && float.TryParse(o[2], System.Globalization.CultureInfo.InvariantCulture, out float zoom))
        {
            (_yaw, _pitch, _distance) = (Mathf.DegToRad(yaw), Mathf.DegToRad(pitch), _distance * zoom);
            if (o.Length > 3 && _pivot.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>()
                    .FirstOrDefault(k => k.FindBone(o[3]) >= 0) is { } skeleton)
            {
                Vector3 at = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone(o[3])).Origin;
                foreach (Node child in _pivot.GetChildren())
                    if (child is Node3D n)
                        n.GlobalPosition -= at - _pivot.GlobalPosition;
            }
        }
        UpdateCamera();
    }

    private static Aabb ComputeBounds(Node node)
    {
        Aabb total = default;
        bool any = false;
        foreach (MeshInstance3D mi in FindMeshes(node))
        {
            if (!mi.IsVisibleInTree())
                continue;
            Aabb a = mi.GlobalTransform * mi.GetAabb();
            total = any ? total.Merge(a) : a;
            any = true;
        }
        return any ? total : new Aabb(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.One);
    }

    private static System.Collections.Generic.IEnumerable<MeshInstance3D> FindMeshes(Node node)
    {
        if (node is MeshInstance3D mi)
            yield return mi;
        foreach (Node child in node.GetChildren())
            foreach (MeshInstance3D m in FindMeshes(child))
                yield return m;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left:
                _dragging = mb.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _distance = Mathf.Max(_distance * 0.9f, 0.2f);
                UpdateCamera();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _distance *= 1.1f;
                UpdateCamera();
                break;
            case InputEventMouseMotion motion when _dragging:
                _yaw -= motion.Relative.X * 0.01f;
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * 0.01f, -1.5f, 1.5f);
                UpdateCamera();
                break;
        }
    }

    private void UpdateCamera()
    {
        var offset = new Vector3(
            _distance * Mathf.Cos(_pitch) * Mathf.Sin(_yaw),
            _distance * Mathf.Sin(_pitch),
            _distance * Mathf.Cos(_pitch) * Mathf.Cos(_yaw));
        _camera.Position = offset;
        _camera.LookAt(Vector3.Zero);
    }
}
