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
    }

    private static string? ModelPathFromArgs() => ResolvePath(ArgValue("--model"));
    private static string? TextureDirFromArgs() => ResolvePath(ArgValue("--textures"));

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
        Node scene = doc.GenerateScene(state);
        _pivot.AddChild(scene);

        // Texture from a folder of extracted .dds. Default to the model's own folder, since the
        // asset pipeline (tools/pipeline.sh) gathers textures beside the .glb; --textures overrides.
        string textureDir = TextureDirFromArgs() ?? System.IO.Path.GetDirectoryName(path) ?? "";
        int textured = ModelTexturing.Apply(scene, textureDir);

        Aabb bounds = ComputeBounds(scene);
        FrameBounds(bounds);
        GD.Print($"[viewer] loaded {System.IO.Path.GetFileName(path)} bounds={bounds.Size} textured={textured}");
        _status.Text = $"{System.IO.Path.GetFileName(path)}\n" +
            $"size {bounds.Size.X:F1} x {bounds.Size.Y:F1} x {bounds.Size.Z:F1}" +
            (textured > 0 ? $" · {textured} textured" : " · untextured") + "\n" +
            "left-drag orbit · wheel zoom";
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
        UpdateCamera();
    }

    private static Aabb ComputeBounds(Node node)
    {
        Aabb total = default;
        bool any = false;
        foreach (MeshInstance3D mi in FindMeshes(node))
        {
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
