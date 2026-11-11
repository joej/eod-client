// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: Targeting and interacting (roadmap M9, pulled forward for the teleporters). Each creature and
// player has a pick shape on its own collision layer. A left click on one targets it (C->S 0xB0; the server
// answers "You target [X]." and examines it); a right click without dragging interacts (C->S 0x7A: an NPC
// talks, a merchant opens). Esc clears the target. An NPC's words to the player (a "##" pop-up line) open a
// small window whose [keywords] whisper back when clicked ("&whis keyword"), as retail's do.

using Godot;

namespace EodClient;

public partial class WorldView
{
    // Collision layer 3: pick shapes of creatures and players. Kept out of the ground ray, the camera arm and
    // the shadow ray, so nobody stands on an NPC's head or has the camera pulled in by one.
    private const uint EntityLayer = 1 << 2;

    private ushort _target;                  // the object we target (0: none)
    private Vector2? _pendingInteract;       // a right click to resolve in the physics frame
    private Vector2 _rightFrom;
    private float _rightMoved;               // how far the mouse moved while the right button was down

    // --interact: the creature to walk up to and talk to (dev; 0 none), and when to give up.
    private ushort _walkTo;
    private double _walkUntil;
    private Vector3? _walkPoint;     // --walkto X Y (dev): run there (region units), then stop
    private bool _shotOnArrival;     // --shot-on-arrival: a screenshot 3 s after --walkto arrives

    // Runs the character (auto-run, so the server sees ordinary running) toward _walkTo, then talks to it once
    // within the server's reach (OpenDAoC INTERACT_DISTANCE 192 units).
    private void WalkToTarget()
    {
        if (_walkPoint is Vector3 point)
        {
            Vector3 to = point - _body.Position;
            to.Y = 0;
            if (to.Length() < 2f || _seconds > _walkUntil)
            {
                GD.Print($"[world] --walkto: {(to.Length() < 2f ? "arrived" : "gave up")} at {ToRegion(_body.Position)}");
                (_walkPoint, _autoRun) = (null, false);
                if (_shotOnArrival)
                    _shotAt = _seconds + 3;
                return;
            }
            _bodyYaw = Mathf.Atan2(-to.X, -to.Z);
            _autoRun = true;
            return;
        }
        if (_walkTo == 0)
            return;
        if (!_entities.TryGetValue(_walkTo, out Entity? e) || _seconds > _walkUntil)
        {
            GD.Print($"[target] --interact: gave up on #{_walkTo}");
            (_walkTo, _autoRun) = (0, false);
            return;
        }
        Vector3 d = e.Node.Position - _body.Position;
        d.Y = 0;
        if (d.Length() < 140 * RegionMetresPerUnit)
        {
            ushort id = _walkTo;
            (_walkTo, _autoRun) = (0, false);
            Interact(id);
            return;
        }
        _bodyYaw = Mathf.Atan2(-d.X, -d.Z);   // the body faces its -Z
        _autoRun = true;
    }

    private PanelContainer _npcWindow = null!;
    private RichTextLabel _npcText = null!;

    // A pick shape for a spawned entity: a capsule round where its figure stands, scaled by the NPC's size.
    private static void AddPickShape(Node3D node, ushort objectId, byte size)
    {
        float s = Mathf.Clamp(size / 50f, 0.3f, 6f);
        var body = new StaticBody3D { CollisionLayer = EntityLayer, CollisionMask = 0 };
        body.SetMeta("entity", objectId);
        body.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f * s, Height = 2f * s },
            Position = new Vector3(0, 1f * s, 0),
        });
        node.AddChild(body);
    }

    // The entity under a screen point, or null.
    private ushort? PickEntity(Vector2 screen)
    {
        Vector3 from = _camera.ProjectRayOrigin(screen);
        var ray = PhysicsRayQueryParameters3D.Create(from, from + _camera.ProjectRayNormal(screen) * 400f);
        ray.CollisionMask = EntityLayer;
        Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(ray);
        if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider || !collider.HasMeta("entity"))
            return null;
        ushort id = (ushort)collider.GetMeta("entity").AsInt32();
        // Something solid in front (a wall) hides it.
        var wall = PhysicsRayQueryParameters3D.Create(from, hit["position"].AsVector3());
        wall.CollisionMask = ~NotSolid;
        return GetWorld3D().DirectSpaceState.IntersectRay(wall).Count > 0 ? null : id;
    }

    private void SetTarget(ushort id)
    {
        if (id == _target)
            return;
        ushort old = _target;
        _target = id;
        _statsDirty = true;   // the Group page marks the targeted member and dims its buttons by the target
        // Retail draws no ring: the target's plate turns white at full opacity, and a target pick closes the item window
        // (targeting-and-chat "Differences", "Keys").
        RestylePlate(old);
        RestylePlate(id);
        if (id != 0)
            CloseMiniInfo();
        if (_session is not null)
            _ = Send(() => _session.SetTargetAsync(id), "target");
    }

    private void Interact(ushort id)
    {
        if (_session is null)
            return;
        SetTarget(id);
        _ = Send(() => _session.InteractAsync(id), "interact");
    }

    private static async System.Threading.Tasks.Task Send(System.Func<System.Threading.Tasks.Task> send, string what)
    {
        try { await send(); }
        catch (System.Exception e) { GD.PrintErr($"[target] {what} failed: {e.Message}"); }
    }

    // A target that leaves view (removed by the server) is no longer shown; the server drops it too.
    private void ForgetTarget(ushort id)
    {
        if (id != _target)
            return;
        _target = 0;
    }

    private string TargetLine => _target != 0 && _entities.TryGetValue(_target, out Entity? e)
        ? $"   target: {(e.Name.Length > 0 ? e.Name : $"#{_target}")}" : "";

    // ----- An NPC's words: a small window with clickable [keywords] -----

    private void BuildNpcWindow(CanvasLayer overlay)
    {
        _npcWindow = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(440, 0) };
        _npcWindow.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.11f, 0.92f), BorderColor = new Color(0.55f, 0.85f, 1f, 0.5f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 14, ContentMarginTop = 12, ContentMarginRight = 14, ContentMarginBottom = 10,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        });
        // Below the status panel, a little right of centre (retail opens it mid-screen).
        _npcWindow.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _npcWindow.Position = new Vector2(-220, 270);
        var box = new VBoxContainer();
        _npcText = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, CustomMinimumSize = new Vector2(400, 0) };
        _npcText.MetaClicked += meta => Whisper(meta.AsString());
        var close = new Button { Text = "Close", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
        close.Pressed += () => _npcWindow.Visible = false;
        box.AddChild(_npcText);
        box.AddChild(close);
        _npcWindow.AddChild(box);
        overlay.AddChild(_npcWindow);
    }

    private void ShowNpcWords(string text)
    {
        _npcText.Clear();
        AddLinkedText(_npcText, text);
        _npcWindow.Visible = true;
        // --keyword WORD (dev): click that keyword once it is offered; "A|B" clicks A, then B when it shows.
        string? word = _devKeyword?.Split('|')[0];
        if (word is not null && text.Contains($"[{word}]", System.StringComparison.OrdinalIgnoreCase))
        {
            int bar = _devKeyword!.IndexOf('|');
            _devKeyword = bar < 0 ? null : _devKeyword[(bar + 1)..];
            GD.Print($"[target] --keyword: {word}");
            // A pause first: the server refuses a second line said within a second or so ("Slow down!").
            GetTree().CreateTimer(1.5).Timeout += () => Whisper(word);
        }
    }

    private string? _devKeyword;

    // Text with each [keyword] as a link (its meta is the keyword).
    private static void AddLinkedText(RichTextLabel label, string text)
    {
        int at = 0;
        while (at < text.Length)
        {
            int open = text.IndexOf('[', at), shut = open < 0 ? -1 : text.IndexOf(']', open + 1);
            if (open < 0 || shut < 0)
            {
                label.AddText(text[at..]);
                break;
            }
            label.AddText(text[at..open]);
            string word = text[(open + 1)..shut];
            label.PushMeta(word);
            label.PushColor(new Color(0.55f, 0.85f, 1f));
            label.AddText($"[{word}]");
            label.Pop();
            label.Pop();
            at = shut + 1;
        }
    }

    // A clicked keyword is whispered to the target (the NPC that said it), as retail sends "&whis keyword".
    private void Whisper(string keyword)
    {
        if (_session is null || _closedReason is not null)
            return;
        _ = Send(() => _session.SendCommandAsync($"/whis {keyword}"), "whisper");
    }
}
