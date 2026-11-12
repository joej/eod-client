// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: Vitals, death and release (roadmap M8). The server says we died (0xAE with our object id) and
// opens the release timer (0xF3, 900 s); the character drops (its Death clip) and can't move, and every 0xA9
// says it is dead (ClientSession.IsDead). A panel counts the timer down and offers Release ("&release", as
// typing /release does); the server then moves us to the bind point (0x04, or 0xB7 to another region) and
// revives us (0x89). Another player's 0xAE drops that figure until its revive. 0xAD (our vitals) feeds the
// status line and the health in each 0xA9.

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private bool _dead;
    private StatusUpdate? _vitals;
    private double _timerEnds = -1;           // when the server's countdown (the release timer) runs out
    private string _timerTitle = "";
    private PanelContainer _deathPanel = null!;
    private Label _deathTimer = null!;
    private Button _releaseButton = null!;

    private void BuildDeathPanel(CanvasLayer overlay)
    {
        _deathPanel = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(320, 0) };
        _deathPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.05f, 0.05f, 0.92f), BorderColor = new Color(0.9f, 0.35f, 0.3f, 0.6f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 16, ContentMarginTop = 12, ContentMarginRight = 16, ContentMarginBottom = 12,
            CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4,
        });
        _deathPanel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        _deathPanel.Position = new Vector2(-160, 270);
        var box = new VBoxContainer();
        box.AddChild(new Label { Text = "You have died.", HorizontalAlignment = HorizontalAlignment.Center });
        _deathTimer = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.75f) };
        box.AddChild(_deathTimer);
        _releaseButton = new Button { Text = "Release", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        _releaseButton.Pressed += Release;
        box.AddChild(_releaseButton);
        _deathPanel.AddChild(box);
        overlay.AddChild(_deathPanel);
    }

    private void SubscribeDeath()
    {
        _bridge.StatusChanged += s =>
        {
            _vitals = s;
            if (_session is not null)
                _session.HealthPercent = s.HealthPercent;
        };
        _bridge.PlayerDied += d =>
        {
            GD.Print($"[world] #{d.VictimId} died (killer #{d.KillerId})");
            if (d.VictimId == _world.Position.ObjectId)
                Die();
            else if (_entities.TryGetValue(d.VictimId, out Entity? e))
            {
                DropEntity(e, true);
                RestylePlate(d.VictimId);   // the dead colour; it stays the target (retail keeps it)
            }
        };
        _bridge.PlayerRevived += r =>
        {
            GD.Print($"[world] #{r.ObjectId} revived");
            if (r.ObjectId == _world.Position.ObjectId)
                Revive();
            else if (_entities.TryGetValue(r.ObjectId, out Entity? e))
            {
                DropEntity(e, false);
                RestylePlate(r.ObjectId);
            }
        };
        _bridge.TimerChanged += t =>
        {
            (_timerEnds, _timerTitle) = t.IsClose ? (-1, "") : (_seconds + t.Seconds, t.Title);
            GD.Print(t.IsClose ? "[world] timer closed" : $"[world] timer \"{t.Title}\" {t.Seconds} s");
        };
    }

    private double _devReleaseAfter = -1;     // --release SECONDS (dev)
    private double _diedAt = -1;

    private void Die()
    {
        _dead = true;
        _diedAt = _seconds;
        if (_session is not null)
            _session.IsDead = true;
        // Retail clears its target on death (C->S 0xB0 with 0).
        if (_target != 0)
            SetTarget(0);
        (_autoRun, _walkTo) = (false, 0);
        PlayOnce("death");
        _releaseButton.Disabled = false;
        _releaseButton.Text = "Release";
        _deathPanel.Visible = true;
        _note = "   dead";
    }

    private void Revive()
    {
        _dead = false;
        if (_session is not null)
            _session.IsDead = false;
        _deathPanel.Visible = false;
        _timerEnds = -1;
        _note = "";
        _bodyClip = "";   // let the frame loop pick the idle again
        PlayBodyClip("idle");
    }

    private void Release()
    {
        if (_session is null || _closedReason is not null || !_dead)
            return;
        _releaseButton.Disabled = true;
        _releaseButton.Text = "Releasing…";
        _ = Send(() => _session.ReleaseAsync(), "release");
    }

    // Another player falls (its Death clip, held) or gets up again.
    private static void DropEntity(Entity e, bool dead)
    {
        e.Dead = dead;
        if (dead)
            (e.MoveSpeed, e.Destination, e.Strafe) = (0f, null, 0);   // the dead don't walk on
        if (e.Player is null)
            return;
        if (dead && e.Player.HasAnimation("death"))
        {
            e.Player.GetAnimation("death").LoopMode = Animation.LoopModeEnum.None;
            e.Player.SpeedScale = 1f;
            e.Player.Play("death", customBlend: 0.1);
            e.Clip = "death";
        }
        else if (!dead)
            e.Clip = "";   // the next frame picks its idle or walk
    }

    // Each frame while the panel shows: the countdown.
    private void UpdateDeathPanel()
    {
        if (!_deathPanel.Visible)
            return;
        if (_devReleaseAfter >= 0 && _diedAt >= 0 && _seconds >= _diedAt + _devReleaseAfter)
        {
            _devReleaseAfter = -1;
            GD.Print("[world] --release");
            Release();
        }
        double left = _timerEnds < 0 ? -1 : Mathf.Max(0, _timerEnds - _seconds);
        _deathTimer.Text = left < 0 ? "Type /release or press Release."
            : $"{(_timerTitle.Length > 0 ? _timerTitle : "Release")}: {(int)left / 60}:{(int)left % 60:00}";
    }

    private string VitalsLine => _vitals is not StatusUpdate v ? ""
        : $"   health {v.Health}/{v.MaxHealth} ({v.HealthPercent}%)   endurance {v.EndurancePercent}%" +
          (v.MaxMana > 0 ? $"   power {v.ManaPercent}%" : "");
}
