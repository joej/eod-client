// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: Development arguments and screenshots: --shots pose sets, --attack, --drop, P to save.

using DaocProtocol.Net;
using DaocProtocol.Packets;
using DaocProtocol.Session;
using DaocProtocol.World;
using Godot;

namespace EodClient;

public partial class WorldView
{
    // Saves the current frame to <progress>/shot_<timestamp>.png. The progress dir is the repo's
    // git-ignored private/progress (where the roadmap/eveofdarkness workflow expects shots), resolved
    // from the library path if given, else from the project directory; only if neither is under the
    // repo does it fall back to Godot's writable user:// dir. The viewport image is only valid after a
    // frame is drawn, so await the post-draw signal first.
    // Developer options for repeatable views of the real client (rendered comparisons):
    //   --campose X Y Z YAW PITCH  start in free-fly with the camera at region coordinates X Y Z,
    //                              looking along YAW (degrees, 0 = north, 90 = east) and PITCH (degrees)
    //   --shot SECONDS             save a screenshot (as P does) after that many seconds in the world
    //   --view LABEL               view distance for this run (classic, x1.5, x2, x4), not saved
    private double _shotAt = -1;
    private int _viewSetting;

    // --view LABEL: a view distance for this run only (the saved setting is untouched).
    private static int? ViewOverride()
    {
        string[] args = OS.GetCmdlineUserArgs();
        int v = System.Array.IndexOf(args, "--view");
        if (v < 0 || v + 1 >= args.Length)
            return null;
        int idx = System.Array.FindIndex(ClientSettings.ViewDistances, x => x.Label == args[v + 1]);
        return idx >= 0 ? idx : null;
    }
    private void ApplyDevArgs()
    {
        string[] args = OS.GetCmdlineUserArgs();
        // --jumps: show the jump points' doorways (the dev overlay's "Jump points" switch).
        if (System.Array.IndexOf(args, "--jumps") >= 0 && !_jumpsVisible)
            _flipJumpsToggle?.Invoke();   // ticks the box, which shows them
        int i = System.Array.IndexOf(args, "--campose");
        if (i >= 0 && i + 5 < args.Length && Floats(string.Join(' ', args[(i + 1)..(i + 6)])) is { Length: 5 } c)
        {
            SetChase(false);
            _camera.GlobalPosition = ToGodot(c[0], c[1], c[2]);
            // Godot yaw 0 looks along -Z (north, since DAoC y runs south); DAoC-style yaw turns clockwise.
            _camYaw = -Mathf.DegToRad(c[3]);
            _camPitch = Mathf.DegToRad(c[4]);
        }
        int set = System.Array.IndexOf(args, "--shots");
        int dir = System.Array.IndexOf(args, "--shots-out");
        if (set >= 0 && set + 1 < args.Length && dir >= 0 && dir + 1 < args.Length)
            _ = RunShotSet(ResolveArgPath(args[set + 1]), ResolveArgPath(args[dir + 1]));
        // --travel ZONE opens the travel window, types the zone number and goes (after 2 s and 5 s), to check
        // the window and the jump without a mouse.
        int travelArg = System.Array.IndexOf(args, "--travel");
        if (travelArg >= 0 && travelArg + 1 < args.Length)
        {
            string zoneText = args[travelArg + 1];
            GetTree().CreateTimer(2.0).Timeout += () =>
            {
                TravelDialog dialog = OpenTravel();
                dialog.Filter(zoneText);
                GetTree().CreateTimer(3.0).Timeout += () => { if (IsInstanceValid(dialog)) dialog.GoFirst(); };
            };
        }
        // --attack SECONDS presses the attack key (H) after 2 s and again SECONDS later, to check attack mode (the
        // server's 0x74 answers, drawn weapons, the combat idle) without a keyboard; --shot can catch it drawn.
        int attackArg = System.Array.IndexOf(args, "--attack");
        if (attackArg >= 0 && attackArg + 1 < args.Length && double.TryParse(args[attackArg + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double attackFor))
            GetTree().CreateTimer(2.0).Timeout += () =>
            {
                _ = SendAttack(true);
                GetTree().CreateTimer(attackFor).Timeout += () => _ = SendAttack(false);
            };
        // --interact NAME: 3 s after entering, right-click the nearest creature of that name (targets it and talks),
        // as a check of targeting and an NPC's window without a mouse.
        // --keyword WORD: then click that [keyword] in the NPC's window ("A|B": A, then B in the answer).
        int kw = System.Array.IndexOf(args, "--keyword");
        if (kw >= 0 && kw + 1 < args.Length)
            _devKeyword = args[kw + 1];
        // --target NAME|nearest: select that creature (the nearest of that name, or the nearest of any) 4 s in, as a
        // click would: to check the target windows.
        int ta = System.Array.IndexOf(args, "--target");
        if (ta >= 0 && ta + 1 < args.Length)
        {
            string name = args[ta + 1];
            GetTree().CreateTimer(4.0).Timeout += () =>
            {
                ushort found = _entities.Where(kv => kv.Value.Name.Length > 0 && (name == "nearest"
                        || kv.Value.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(kv => kv.Value.Node.Position.DistanceTo(_body.Position)).Select(kv => kv.Key).FirstOrDefault();
                GD.Print(found == 0 ? $"[target] --target: no \"{name}\" in view" : $"[target] --target: {_entities[found].Name} (#{found})");
                if (found != 0)
                    SetTarget(found);
            };
        }
        int ia = System.Array.IndexOf(args, "--interact");
        if (ia >= 0 && ia + 1 < args.Length)
        {
            string name = args[ia + 1];
            GetTree().CreateTimer(3.0).Timeout += () =>
            {
                ushort found = _entities.Where(kv => kv.Value.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kv => kv.Value.Node.Position.DistanceTo(_body.Position)).Select(kv => kv.Key).FirstOrDefault();
                GD.Print(found == 0 ? $"[target] --interact: no \"{name}\" in view"
                    : $"[target] --interact: {name} (#{found}), {_entities[found].Node.Position.DistanceTo(_body.Position) / RegionMetresPerUnit:0} units away");
                if (found != 0)
                    (_walkTo, _walkUntil) = (found, _seconds + 30);
            };
        }
        // --command TEXT (repeatable) sends each slash command in turn, 2 s apart from 2 s after entering: e.g.
        // a GM "/jump to X Y Z REGION" to set up a --campose/--shot check somewhere exact.
        int delay = 2;
        for (int ci = System.Array.IndexOf(args, "--command"); ci >= 0 && ci + 1 < args.Length;
             ci = System.Array.IndexOf(args, "--command", ci + 2), delay += 2)
        {
            string command = args[ci + 1];
            GetTree().CreateTimer(delay).Timeout += () =>
            {
                GD.Print($"[world] dev command: {command}");
                if (LocalCommand(command))
                    return;   // the client's own (/loc, /map, /quickbar)
                if (command.StartsWith("/useskill ", System.StringComparison.OrdinalIgnoreCase))
                    UseSkillByName(command["/useskill ".Length..].Trim());   // the client's own command
                else
                    _ = SendTravel(command);
            };
        }
        // --npc MODEL[:STATE][,MODEL...] spawns Albion NPCs with those models 5 m apart in front of the character, through the
        // same path a server's 0xDA takes (OnNpcSpawned): to check a creature as the game draws it, offline.
        int npcArg = System.Array.IndexOf(args, "--npc");
        if (npcArg >= 0 && npcArg + 1 < args.Length)
        {
            string[] models = args[npcArg + 1].Split(',');
            // MODEL:STATE gives it a quest glyph (available, finish, lesson, lore, pending), sent as the server's
            // 0xDA flag bits.
            for (int k = 0; k < models.Length; k++)
            {
                string[] part = models[k].Split(':');
                if (!ushort.TryParse(part[0], out ushort model))
                    continue;
                var quest = part.Length > 1 && System.Enum.TryParse(part[1], true, out DaocProtocol.Packets.QuestIndicator q)
                    ? q : DaocProtocol.Packets.QuestIndicator.None;
                byte flags2 = quest switch { DaocProtocol.Packets.QuestIndicator.Available => 0x08, DaocProtocol.Packets.QuestIndicator.Finish => 0x10, _ => 0 };
                byte flags3 = quest switch { DaocProtocol.Packets.QuestIndicator.Lesson => 0x01, DaocProtocol.Packets.QuestIndicator.Lore => 0x02, DaocProtocol.Packets.QuestIndicator.Pending => 0x20, _ => 0 };
                float x = _world.Position.X + 200 * (k - (models.Length - 1) / 2f), y = _world.Position.Y - 300;
                OnNpcSpawned(new DaocProtocol.Packets.NpcCreate((ushort)(60000 + k), 0, 0, (ushort)_world.Position.Z,
                    (uint)x, (uint)y, model, 50, 1, 0x40, $"npc {model}", "") { Flags2 = flags2, Flags3 = flags3 });
            }
        }
        // --drop METRES lifts the character that far once the world is up, to check falling and landing.
        int drop = System.Array.IndexOf(args, "--drop");
        if (drop >= 0 && drop + 1 < args.Length && float.TryParse(args[drop + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float metres))
            _dropPending = metres;
        // --drops N: lift it N times, each a second after landing (two big falls kill: OpenDAoC caps one at 99%).
        int drops = System.Array.IndexOf(args, "--drops");
        if (drops >= 0 && drops + 1 < args.Length && int.TryParse(args[drops + 1], out int n))
            (_dropAgain, _dropsLeft) = (_dropPending, n - 1);
        // --walkto X Y: run to that region point (auto-run, as the player would); --shot-on-arrival saves a shot there.
        int walk = System.Array.IndexOf(args, "--walkto");
        if (walk >= 0 && walk + 2 < args.Length && float.TryParse(args[walk + 1], System.Globalization.CultureInfo.InvariantCulture, out float wx)
            && float.TryParse(args[walk + 2], System.Globalization.CultureInfo.InvariantCulture, out float wy))
            (_walkPoint, _walkUntil, _shotOnArrival) = (ToGodot(wx, wy, 0), _seconds + 300, args.Contains("--shot-on-arrival"));
        // --map: open the map window; --map-region: on the region map.
        if (args.Contains("--map") || args.Contains("--map-region"))
        {
            _mapRegion = args.Contains("--map-region");
            ToggleMap();
        }
        // --borders: tick the "Zone borders" switch.
        if (args.Contains("--borders") && !_bordersVisible)
            _flipBordersToggle?.Invoke();
        // --release SECONDS: after dying, release that long after (as the Release button).
        int release = System.Array.IndexOf(args, "--release");
        if (release >= 0 && release + 1 < args.Length && double.TryParse(args[release + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double after))
            _devReleaseAfter = after;
        int s = System.Array.IndexOf(args, "--shot");
        if (s >= 0 && s + 1 < args.Length && double.TryParse(args[s + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double at))
            _shotAt = at;
    }

    // A fixed shot set (--shots FILE --shots-out DIR): each line of FILE is "name X Y Z YAW PITCH"
    // (region units, degrees; '#' starts a comment). The camera visits each pose in turn, the frame
    // is saved as DIR/name.png, and the client quits when the set is done. Run the same set before and
    // after an asset change and compare: the world checked the same way every time, not by walking.
    private async System.Threading.Tasks.Task RunShotSet(string file, string dir)
    {
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var poses = new System.Collections.Generic.List<(string Name, float[] P)>();
        foreach (string raw in System.IO.File.ReadAllLines(file))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            string[] f = line.Split((char[])[' ', '\t'], System.StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 6 && Floats(string.Join(' ', f[1..])) is { Length: 5 } c)
                poses.Add((f[0], c));
            else
                GD.PrintErr($"[shots] skipped line: {raw}");
        }
        // Let the zone finish loading and settle for a moment first.
        for (int k = 0; k < 10; k++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        foreach ((string name, float[] c) in poses)
        {
            SetChase(false);
            _camera.GlobalPosition = ToGodot(c[0], c[1], c[2]);
            _camYaw = -Mathf.DegToRad(c[3]);
            _camPitch = Mathf.DegToRad(c[4]);
            _nextStatus = 0;   // the shot's status text is the pose's, not up to 0.25 s old
            await WaitForZones();   // the zones around the pose, streamed in
            for (int k = 0; k < 4; k++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Image img = GetViewport().GetTexture().GetImage();
            string path = dir.PathJoin(name + ".png");
            Error err = img.SavePng(path);
            GD.Print(err == Error.Ok ? $"[shots] {name} -> {path}" : $"[shots] {name} failed: {err}");
        }
        GD.Print($"[shots] {poses.Count} shot(s) done");
        // Quit on the next frame, not from inside this async task: quitting mid-continuation aborted Godot on
        // macOS while it shut down (SIGABRT in a mutex during NSApplication terminate, 2026-11-09).
        if (_session is null)
            GetTree().CallDeferred(SceneTree.MethodName.Quit, 0);
        else
            QuitPressed?.Invoke();
    }

    // Like CharacterScreen's paths: relative to the shell's working directory, not godot/.
    private static string ResolveArgPath(string path)
    {
        if (System.IO.Path.IsPathRooted(path)) return path;
        string cwd = OS.GetEnvironment("PWD");
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(string.IsNullOrEmpty(cwd) ? System.IO.Directory.GetCurrentDirectory() : cwd, path));
    }

    private async System.Threading.Tasks.Task SaveScreenshot()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image img = GetViewport().GetTexture().GetImage();

        string dir = ScreenshotDir();
        DirAccess.MakeDirRecursiveAbsolute(dir);
        string stamp = Time.GetDatetimeStringFromSystem().Replace(":", "-").Replace("T", "_");
        string path = dir.PathJoin($"shot_{stamp}.png");

        Error err = img.SavePng(path);
        // Show only the file NAME in the overlay, never the full local path: these shots are
        // captured for the eveofdarkness.org panel, and an on-screen "/Users/…" path would have to
        // be cropped out before any could be published. The full path still goes to the console log.
        _note = err == Error.Ok ? $"   saved {path.GetFile()}" : $"   screenshot failed: {err}";
        GD.Print(err == Error.Ok ? $"[world] screenshot -> {path}" : $"[world] screenshot failed: {err}");
    }

    private string ScreenshotDir()
    {
        // 1. Sibling of the library path: <...>/private/library -> <...>/private/progress.
        if (!string.IsNullOrEmpty(_libraryPath))
        {
            string? parent = System.IO.Path.GetDirectoryName(_libraryPath.TrimEnd('/'));
            if (!string.IsNullOrEmpty(parent))
                return parent.PathJoin("progress");
        }

        // 2. Repo's private/progress, found by walking up from the Godot project dir (res://). This is
        //    the usual case for a no-assets launch (no --model): shots still land where the roadmap
        //    and eveofdarkness workflow look for them, not in Godot's hidden user:// dir.
        string projectDir = ProjectSettings.GlobalizePath("res://");
        for (var d = new System.IO.DirectoryInfo(projectDir); d is not null; d = d.Parent)
        {
            string progress = System.IO.Path.Combine(d.FullName, "private", "progress");
            if (System.IO.Directory.Exists(progress))
                return progress;
        }

        // 3. Last resort: Godot's writable user data dir.
        return ProjectSettings.GlobalizePath("user://progress");
    }
}
