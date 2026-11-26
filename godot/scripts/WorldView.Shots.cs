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

    // Every pose shot: retail's field of view. A pose whose camera stands on the character (a first-person shot matched with retail's, which hides your own body)
    // doesn't show the character: offline, its stand-in capsule (a bright blue back stripe) filled the frame.
    private void HideBodyAtCamera()
    {
        Vector3 d = _camera.GlobalPosition - _body.GlobalPosition;
        d.Y = 0;
        _body.Visible = d.Length() > 3f;
        // Pose shots use retail's fixed field of view, whatever the player's setting, so they compare with retail's.
        _camera.Fov = ClientSettings.RetailFieldOfView;
    }
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
        ScheduleUiDump();
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
            HideBodyAtCamera();
        }
        // --keeps FILE: replay a keep packet file (`mpk keeps`) as if the server had sent it.
        int keepsArg = System.Array.IndexOf(args, "--keeps");
        if (keepsArg >= 0 && keepsArg + 1 < args.Length)
            ReplayKeeps(ResolveArgPath(args[keepsArg + 1]));
        int set = System.Array.IndexOf(args, "--shots");
        int dir = System.Array.IndexOf(args, "--shots-out");
        if (set >= 0 && set + 1 < args.Length && dir >= 0 && dir + 1 < args.Length)
        {
            // The window opens focused, so whatever the person types elsewhere would land here (a stray F10 opened
            // Settings over a shot, Shift+F12 switched the render mode): a shot run takes no keyboard or mouse.
            GetWindow().SetFlag(Window.Flags.NoFocus, true);
            GetViewport().GuiDisableInput = true;
            SetProcessInput(false);
            SetProcessUnhandledInput(false);
            SetProcessUnhandledKeyInput(false);
            _ = RunShotSet(ResolveArgPath(args[set + 1]), ResolveArgPath(args[dir + 1]));
        }
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
        // --target NAME|nearest|self: select that creature (the nearest of that name, or the nearest of any) 4 s in, as a
        // click would: to check the target windows. --target-at SECONDS picks another time (after a --command /jump).
        int ta = System.Array.IndexOf(args, "--target");
        if (ta >= 0 && ta + 1 < args.Length)
        {
            string name = args[ta + 1];
            int tat = System.Array.IndexOf(args, "--target-at");
            double targetAt = tat >= 0 && tat + 1 < args.Length && double.TryParse(args[tat + 1], System.Globalization.CultureInfo.InvariantCulture, out double tAt) ? tAt : 4.0;
            GetTree().CreateTimer(targetAt).Timeout += () =>
            {
                if (name == "self")   // --target self: ourselves (GM commands that act on the target)
                {
                    GD.Print($"[target] --target: self (#{SelfId})");
                    SetTarget(SelfId);
                    return;
                }
                ushort found = _entities.Where(kv => kv.Value.Name.Length > 0 && (name == "nearest"
                        || kv.Value.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(kv => kv.Value.Node.Position.DistanceTo(_body.Position)).Select(kv => kv.Key).FirstOrDefault();
                GD.Print(found == 0 ? $"[target] --target: no \"{name}\" in view; nearest: " + string.Join(", ", _entities
                        .OrderBy(kv => kv.Value.Node.Position.DistanceTo(_body.Position)).Take(6)
                        .Select(kv => $"#{kv.Key} \"{kv.Value.Name}\"{(kv.Value.IsPlayer ? " (player)" : "")}"))
                    : $"[target] --target: {_entities[found].Name} (#{found})");
                if (found != 0)
                    SetTarget(found);
            };
        }
        // --train LINE: with --interact TRAINER, raise LINE (a specialisation's name) by one level once the window arrives.
        int trainArg = System.Array.IndexOf(args, "--train");
        if (trainArg >= 0 && trainArg + 1 < args.Length)
            _devTrain = args[trainArg + 1];
        // --buy-sell: with --interact MERCHANT, once its window opens buy the cheapest usable item, appraise it and sell it back.
        _devBuySell = System.Array.IndexOf(args, "--buy-sell") >= 0;
        int ia = System.Array.IndexOf(args, "--interact");
        if (ia >= 0 && ia + 1 < args.Length)
        {
            string name = args[ia + 1];
            // --interact-at SECONDS: later than the default 3 s (after a --command /jump).
            int iat = System.Array.IndexOf(args, "--interact-at");
            double interactAt = iat >= 0 && iat + 1 < args.Length && double.TryParse(args[iat + 1], System.Globalization.CultureInfo.InvariantCulture, out double iAt) ? iAt : 3.0;
            GetTree().CreateTimer(interactAt).Timeout += () =>
            {
                ushort found = _entities.Where(kv => kv.Value.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kv => kv.Value.Node.Position.DistanceTo(_body.Position)).Select(kv => kv.Key).FirstOrDefault();
                GD.Print(found == 0 ? $"[target] --interact: no \"{name}\" in view"
                    : $"[target] --interact: {name} (#{found}), {_entities[found].Node.Position.DistanceTo(_body.Position) / RegionMetresPerUnit:0} units away");
                if (found != 0)
                    (_walkTo, _walkUntil) = (found, _seconds + 30);
            };
        }
        // --object MODEL[,MODEL...] places server objects (0xD9) with those models 300 units apart east of the character
        // (--object-dx N: N units apart instead, negative for west),
        // through the path a server's object takes (OnWorldObject): to check one's look offline.
        int objArg = System.Array.IndexOf(args, "--object");
        if (objArg >= 0 && objArg + 1 < args.Length)
        {
            string[] ids = args[objArg + 1].Split(',');
            int dxArg = System.Array.IndexOf(args, "--object-dx");
            int dx = dxArg >= 0 && dxArg + 1 < args.Length && int.TryParse(args[dxArg + 1], out int d) ? d : 300;
            GetTree().CreateTimer(1).Timeout += () =>
            {
                var p = _world.Position;
                for (int k = 0; k < ids.Length; k++)
                    if (ushort.TryParse(ids[k], out ushort model))
                        OnWorldObject(new ObjectCreate((ushort)(60000 + k), 0, (ushort)p.Z, (uint)(p.X + dx * (k + 1)), (uint)p.Y, model, $"test {model}"));
            };
        }
        // --ui-drag PIXELS: 5 s in, retail's interface-size drag from the top-left corner, PIXELS to the right (synthetic mouse
        // events through the normal input path), to check the live rebuild.
        int uiDrag = System.Array.IndexOf(args, "--ui-drag");
        if (uiDrag >= 0 && uiDrag + 1 < args.Length && float.TryParse(args[uiDrag + 1], out float dragBy))
            GetTree().CreateTimer(5).Timeout += () =>
            {
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = new Vector2(10, 10) });
                Input.ParseInputEvent(new InputEventMouseMotion { Position = new Vector2(10 + dragBy, 10) });
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = new Vector2(10 + dragBy, 10) });
            };
        // --panel-click X Y: 5 s in, a left click (press and release) at window pixel X, Y: e.g. on a dev panel, to check
        // it stays where it is.
        int panelClick = System.Array.IndexOf(args, "--panel-click");
        if (panelClick >= 0 && panelClick + 2 < args.Length && float.TryParse(args[panelClick + 1], out float clickX)
            && float.TryParse(args[panelClick + 2], out float clickY))
            GetTree().CreateTimer(5).Timeout += () =>
            {
                var at = new Vector2(clickX, clickY);
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = at, GlobalPosition = at });
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = at, GlobalPosition = at });
            };
        // --time H[:MM] starts the game clock there (the server's time ignored), to see the sky at that hour; offline too.
        int timeAt = System.Array.IndexOf(args, "--time");
        if (timeAt >= 0 && timeAt + 1 < args.Length)
        {
            string[] hm = args[timeAt + 1].Split(':');
            if (float.TryParse(hm[0], out float hour))
            {
                float minutes = hm.Length > 1 && float.TryParse(hm[1], out float m) ? m : 0;
                (_gameTime, _gameTimeAt, _timeForced, _skyTimeAt, _skyTime) =
                    (new DaocProtocol.Packets.GameTime((uint)((hour * 60 + minutes) * 60_000), 24), _seconds, true, -1, null);
            }
        }
        // --light AMBIENT SUN [noshadow]: the two light strengths ApplySkyTime uses (to fit retail shots), and sun shadows off.
        int lightAt = System.Array.IndexOf(args, "--light");
        if (lightAt >= 0 && lightAt + 2 < args.Length && float.TryParse(args[lightAt + 1], System.Globalization.CultureInfo.InvariantCulture, out float la)
            && float.TryParse(args[lightAt + 2], System.Globalization.CultureInfo.InvariantCulture, out float ls))
        {
            (_ambientEnergy, _sunEnergy) = (la, ls);
            if (lightAt + 3 < args.Length && args[lightAt + 3] == "noshadow")
                _sun.ShadowEnabled = false;
            ApplySkyTime();
        }
        // --spell ID: our character casts spell ID on itself (offline too): a 2.5 s cast from 2 s in, then it lands as a hit,
        // to see the spell's effects (WorldView.SpellFx) in a shot or a clip.
        int spellAt = System.Array.IndexOf(args, "--spell");
        if (spellAt >= 0 && spellAt + 1 < args.Length && ushort.TryParse(args[spellAt + 1], out ushort spellId))
        {
            GetTree().CreateTimer(2).Timeout += () => OnSpellCast(new DaocProtocol.Packets.SpellCast(SelfId, spellId, 25));
            GetTree().CreateTimer(4.5).Timeout += () =>
                OnSpellLanded(new DaocProtocol.Packets.SpellEffect(SelfId, spellId, SelfId, 0, false, 1));
        }
        // --no-panels hides the dev panels (status, layers, inspect), as F12 does: shots for the site.
        if (System.Array.IndexOf(args, "--no-panels") >= 0)
            _dev.PanelsVisible = false;
        // --modern-particles draws particles as Settings → Modern particles does (soft), without saving the setting.
        if (System.Array.IndexOf(args, "--modern-particles") >= 0)
            NifParticles.ForceModern = true;
        // --torch lights the character's torch on entering (to check its look).
        if (System.Array.IndexOf(args, "--torch") >= 0)
            GetTree().CreateTimer(1).Timeout += () => SetTorch(true, announce: true);
        // --elements opens Interface Elements and the command window (to see them in a --shot).
        if (System.Array.IndexOf(args, "--elements") >= 0)
            GetTree().CreateTimer(1).Timeout += () =>
            {
                _showCommand = true;
                ShowElements(true);
            };
        // --main-menu opens the menu bar's MENU (to see it in a --shot).
        if (System.Array.IndexOf(args, "--main-menu") >= 0)
            GetTree().CreateTimer(1).Timeout += ToggleMainMenu;
        // --stats PAGE opens the stats panel at a page (attributes, inventory, skills, styles, spells, group), as its
        // menu-bar button would; late enough for the server's lists to have arrived.
        int si = System.Array.IndexOf(args, "--stats");
        if (si >= 0 && si + 1 < args.Length && System.Array.FindIndex(StatsTabNames, n => n.Equals(args[si + 1], System.StringComparison.OrdinalIgnoreCase)) is int page and >= 0)
            GetTree().CreateTimer(3).Timeout += () => ToggleStatsPage(page);
        // --move FROM TO (repeatable) moves an item between inventory slots as a drag would, 3 s apart from 5 s after
        // entering: e.g. --move 10 40 --move 40 10 takes the right-hand weapon off and puts it back.
        double moveAt = 5;
        for (int mi = System.Array.IndexOf(args, "--move"); mi >= 0 && mi + 2 < args.Length;
             mi = System.Array.IndexOf(args, "--move", mi + 1))
            if (byte.TryParse(args[mi + 1], out byte from) && ushort.TryParse(args[mi + 2], out ushort to))
            {
                GetTree().CreateTimer(moveAt).Timeout += () => MoveItem(from, to);
                moveAt += 3;
            }
        // --select SLOT [info|repair|craft|salvage|use1|use2] right-clicks an inventory item (the mini_info window) at 4 s,
        // then presses one of its buttons at 6 s: e.g. --select 10 info opens the server's description of the right-hand weapon.
        int sel = System.Array.IndexOf(args, "--select");
        if (sel >= 0 && sel + 1 < args.Length && byte.TryParse(args[sel + 1], out byte selSlot))
        {
            GetTree().CreateTimer(4).Timeout += () => SelectItem(selSlot);
            string action = sel + 2 < args.Length ? args[sel + 2].ToLowerInvariant() : "";
            System.Action? press = action switch
            {
                "info" => () => ItemAction(DetailRequest.InventoryItem), "repair" => () => ItemAction(DetailRequest.Repair),
                "craft" => () => ItemAction(DetailRequest.SelfCraft), "salvage" => () => ItemAction(DetailRequest.Salvage),
                "use1" => () => UseSelected(1), "use2" => () => UseSelected(2), _ => null,
            };
            if (press is not null)
                GetTree().CreateTimer(6).Timeout += press;
        }
        // --select-skill NAME [info] right-clicks that spell or style on its sheet at 5 s (the item window), then presses INFO at 8 s.
        int ss = System.Array.IndexOf(args, "--select-skill");
        if (ss >= 0 && ss + 1 < args.Length)
        {
            string skillName = args[ss + 1];
            GetTree().CreateTimer(5).Timeout += () => SelectSkill(skillName);
            if (ss + 2 < args.Length && args[ss + 2].Equals("info", System.StringComparison.OrdinalIgnoreCase))
                GetTree().CreateTimer(8).Timeout += () => ItemAction(DetailRequest.InventoryItem);
        }
        // --group invite|disband SECONDS (repeatable) presses that Group-page button at that time (after a --target); --accept-dialogs
        // answers every server dialog yes as it arrives (a group invite): together, two clients form a group unattended.
        for (int gr = System.Array.IndexOf(args, "--group"); gr >= 0 && gr + 2 < args.Length; gr = System.Array.IndexOf(args, "--group", gr + 1))
        {
            if (!double.TryParse(args[gr + 2], System.Globalization.CultureInfo.InvariantCulture, out double groupAt))
                continue;
            string what = args[gr + 1].ToLowerInvariant();
            GetTree().CreateTimer(groupAt).Timeout += () =>
            {
                if (what == "invite")
                    GroupSend(() => _session!.InviteToGroupAsync(), "invite");
                else if (what == "disband")
                    GroupSend(() => _session!.DisbandFromGroupAsync(), "disband");
            };
        }
        _acceptDialogs = System.Array.IndexOf(args, "--accept-dialogs") >= 0;
        // --zoom NOTCHES (repeatable) turns the mouse wheel that many notches (negative: in), 1 s apart from 2 s after
        // entering: to check the zoom and first person without a mouse.
        int zoomDelay = 2;
        for (int zi = System.Array.IndexOf(args, "--zoom"); zi >= 0 && zi + 1 < args.Length;
             zi = System.Array.IndexOf(args, "--zoom", zi + 2), zoomDelay++)
            if (float.TryParse(args[zi + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float notches))
                GetTree().CreateTimer(zoomDelay).Timeout += () =>
                {
                    Zoom(notches);
                    GD.Print($"[camera] zoomed {notches:+0;-0} notches: {(_firstPerson ? "first person" : $"{_camDistance / RegionMetresPerUnit:0} units")}");
                };
        // --type TEXT (repeatable) types a line into the chat box and presses Enter, 2 s apart from 3 s after entering: the
        // chat rules (mode switches, %t) as a player meets them.
        int typeDelay = 3;
        for (int ti = System.Array.IndexOf(args, "--type"); ti >= 0 && ti + 1 < args.Length;
             ti = System.Array.IndexOf(args, "--type", ti + 2), typeDelay += 2)
        {
            string typed = args[ti + 1];
            GetTree().CreateTimer(typeDelay).Timeout += () =>
            {
                GD.Print($"[world] dev typed: \"{typed}\"");
                OnChatSubmitted(typed);
            };
        }
        // --press KEY (repeatable; Godot key names with modifiers, e.g. Tab, Ctrl+Tab, Shift+F1) presses each key in turn
        // through the normal input path, 1 s apart from 6 s after entering (--press-at SECONDS moves the start): to check
        // key handling, such as the targeting keys, headless.
        int pa = System.Array.IndexOf(args, "--press-at");
        double pressAt = pa >= 0 && pa + 1 < args.Length && double.TryParse(args[pa + 1], System.Globalization.CultureInfo.InvariantCulture, out double pAt) ? pAt : 6.0;
        for (int pi = System.Array.IndexOf(args, "--press"); pi >= 0 && pi + 1 < args.Length;
             pi = System.Array.IndexOf(args, "--press", pi + 2), pressAt += 1)
        {
            Key key = OS.FindKeycodeFromString(args[pi + 1]);
            GetTree().CreateTimer(pressAt).Timeout += () =>
            {
                GD.Print($"[world] dev pressed: {OS.GetKeycodeString(key)}");
                var down = new InputEventKey
                {
                    Keycode = key & (Key)KeyModifierMask.CodeMask, Pressed = true,
                    ShiftPressed = (key & (Key)KeyModifierMask.MaskShift) != 0, AltPressed = (key & (Key)KeyModifierMask.MaskAlt) != 0,
                    CtrlPressed = (key & (Key)KeyModifierMask.MaskCtrl) != 0,
                };
                Input.ParseInputEvent(down);
                var up = (InputEventKey)down.Duplicate();
                up.Pressed = false;
                Input.ParseInputEvent(up);
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
        // --craft ID[,ID...]: from --craft-at SECONDS (default 10) after entering, send each recipe id (C→S 0xED) 15 s
        // apart, so one craft can finish before the next.
        int craftArg = System.Array.IndexOf(args, "--craft");
        if (craftArg >= 0 && craftArg + 1 < args.Length)
        {
            int cat = System.Array.IndexOf(args, "--craft-at");
            double craftAt = cat >= 0 && cat + 1 < args.Length && double.TryParse(args[cat + 1], System.Globalization.CultureInfo.InvariantCulture, out double cAt) ? cAt : 10.0;
            string[] ids = args[craftArg + 1].Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
            for (int k = 0; k < ids.Length; k++)
            {
                if (!ushort.TryParse(ids[k], out ushort recipe))
                {
                    GD.PrintErr($"[world] --craft: not a recipe id: {ids[k]}");
                    continue;
                }
                GetTree().CreateTimer(craftAt + 15.0 * k).Timeout += () =>
                {
                    GD.Print($"[world] --craft: {recipe}");
                    if (_session is not null)
                        _ = _session.CraftAsync(recipe);
                };
            }
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
        // --corpse SECONDS: that long after entering, the --npc creatures are removed as the server removes a dead NPC
        // (0xA2 type 0), so they lie as corpses; the corpse count is logged then, and 1, 10, 19 and 21 s later.
        int corpseArg = System.Array.IndexOf(args, "--corpse");
        if (npcArg >= 0 && corpseArg >= 0 && corpseArg + 1 < args.Length && double.TryParse(args[corpseArg + 1],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double corpseAt))
            GetTree().CreateTimer(corpseAt).Timeout += () =>
            {
                for (ushort id = 60000; _entities.ContainsKey(id); id++)
                    OnObjectRemoved(id, DaocProtocol.Packets.RemoveObject.WithDeathEffect);
                foreach (double after in new[] { 0.0, 1, 10, 19, 21 })
                    GetTree().CreateTimer(after).Timeout += () =>
                        GD.Print($"[corpse] +{after:0} s: {_corpses.Count} corpses, entities 60000+: {_entities.Keys.Count(k => k >= 60000)}");
            };
        // --drop METRES lifts the character that far once the world is up, to check falling and landing.
        int drop = System.Array.IndexOf(args, "--drop");
        if (drop >= 0 && drop + 1 < args.Length && float.TryParse(args[drop + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float metres))
            _dropPending = metres;
        // --drops N: lift it N times, each a second after landing (two big falls kill: OpenDAoC caps one at 99%).
        int drops = System.Array.IndexOf(args, "--drops");
        if (drops >= 0 && drops + 1 < args.Length && int.TryParse(args[drops + 1], out int n))
            (_dropAgain, _dropsLeft) = (_dropPending, n - 1);
        // --motion-log: how far each NPC's own movement between updates lands from where the next update puts it.
        _motionLog = args.Contains("--motion-log");
        // --walkto X Y: run to that region point (auto-run, as the player would); --shot-on-arrival saves a shot there.
        int walk = System.Array.IndexOf(args, "--walkto");
        if (walk >= 0 && walk + 2 < args.Length && float.TryParse(args[walk + 1], System.Globalization.CultureInfo.InvariantCulture, out float wx)
            && float.TryParse(args[walk + 2], System.Globalization.CultureInfo.InvariantCulture, out float wy))
        {
            // --walkto-at SECONDS: start walking then rather than at once (e.g. to move during a cast).
            int wat = System.Array.IndexOf(args, "--walkto-at");
            double walkAt = wat >= 0 && wat + 1 < args.Length && double.TryParse(args[wat + 1], System.Globalization.CultureInfo.InvariantCulture, out double wAt) ? wAt : 0;
            bool shot = args.Contains("--shot-on-arrival");
            if (walkAt <= 0)
                (_walkPoint, _walkUntil, _shotOnArrival) = (ToGodot(wx, wy, 0), _seconds + 300, shot);
            else
                GetTree().CreateTimer(walkAt).Timeout += () =>
                    (_walkPoint, _walkUntil, _shotOnArrival) = (ToGodot(wx, wy, 0), _seconds + 300, shot);
        }
        // --inspect-at X Y [--inspect-time S, default 8] (repeatable): the inspect readout for that screen point (in pixels of
        // the window), logged, as a left click on it shows it: which model, mesh and material a shot shows there.
        int it = System.Array.IndexOf(args, "--inspect-time");
        double inspectAt = it >= 0 && it + 1 < args.Length && double.TryParse(args[it + 1], System.Globalization.CultureInfo.InvariantCulture, out double iT) ? iT : 8;
        for (int ii = System.Array.IndexOf(args, "--inspect-at"); ii >= 0 && ii + 2 < args.Length;
             ii = System.Array.IndexOf(args, "--inspect-at", ii + 1))
            if (float.TryParse(args[ii + 1], System.Globalization.CultureInfo.InvariantCulture, out float ix)
                && float.TryParse(args[ii + 2], System.Globalization.CultureInfo.InvariantCulture, out float iy))
                GetTree().CreateTimer(inspectAt).Timeout += () =>
                {
                    Inspect(new Vector2(ix, iy));
                    GD.Print($"[inspect] at {ix:0},{iy:0}: {_dev.InspectText.Replace("\n", " | ")}");
                };
        // --drop-item SLOT [--drop-at SECONDS, default 4]: drop that inventory item on the ground, as dragging it out over
        // the world does (with its question; --accept-dialogs answers it).
        int di = System.Array.IndexOf(args, "--drop-item");
        if (di >= 0 && di + 1 < args.Length && byte.TryParse(args[di + 1], out byte dropSlot))
        {
            int dat = System.Array.IndexOf(args, "--drop-at");
            double dropAt = dat >= 0 && dat + 1 < args.Length && double.TryParse(args[dat + 1], System.Globalization.CultureInfo.InvariantCulture, out double dAt) ? dAt : 4;
            GetTree().CreateTimer(dropAt).Timeout += () => DropItem(dropSlot);
        }
        // --cancel-effect NAME [--cancel-at SECONDS, default 20]: cancel the character's effect of that name (C→S 0xF8), as
        // a click on its icon would.
        int ce = System.Array.IndexOf(args, "--cancel-effect");
        if (ce >= 0 && ce + 1 < args.Length)
        {
            string effect = args[ce + 1];
            int cat = System.Array.IndexOf(args, "--cancel-at");
            double cancelAt = cat >= 0 && cat + 1 < args.Length && double.TryParse(args[cat + 1], System.Globalization.CultureInfo.InvariantCulture, out double cAt) ? cAt : 20;
            GetTree().CreateTimer(cancelAt).Timeout += () =>
            {
                var icon = _bridge.Effects.Values.FirstOrDefault(i => i.Name.Equals(effect, System.StringComparison.OrdinalIgnoreCase));
                GD.Print(icon is null ? $"[effects] --cancel-effect: no \"{effect}\"" : $"[effects] cancelling {icon.Name} (tooltip {icon.TooltipId})");
                if (icon is not null && _session is not null)
                    _ = _session.CancelEffectAsync(icon.TooltipId);
            };
        }
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
    // (region units, degrees; '#' starts a comment), or "!open-doors" (every loaded door opens, and the set waits until they are open). The camera visits each pose in turn, the frame
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
            else if (f is ["!open-doors"])
                poses.Add((f[0], []));   // an action, not a pose: every loaded door opens (as a 0x99 would)
            else
                GD.PrintErr($"[shots] skipped line: {raw}");
        }
        // Let the zone finish loading and settle for a moment first.
        for (int k = 0; k < 10; k++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        foreach ((string name, float[] c) in poses)
        {
            if (name == "!open-doors")
            {
                double longest = 0;
                foreach ((uint id, (Node3D model, string door)) in _zones.Values.SelectMany(z => z.Doors)
                             .Concat(_keepDoors.Select(k => new System.Collections.Generic.KeyValuePair<uint, (Node3D, string)>(k.Key, (k.Value.Model, k.Value.Door)))).ToList())
                {
                    OnDoorChanged(new DoorState(id, DoorState.Open, 0, 0xFF));
                    if (DoorPlayer(model, door) is AnimationPlayer p && p.HasAnimation(door))
                        longest = System.Math.Max(longest, p.GetAnimation(door).Length);
                }
                await ToSignal(GetTree().CreateTimer(longest + 0.5), SceneTreeTimer.SignalName.Timeout);   // until every door is open
                continue;
            }
            SetChase(false);
            _camera.GlobalPosition = ToGodot(c[0], c[1], c[2]);
            _camYaw = -Mathf.DegToRad(c[3]);
            _camPitch = Mathf.DegToRad(c[4]);
            HideBodyAtCamera();
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
