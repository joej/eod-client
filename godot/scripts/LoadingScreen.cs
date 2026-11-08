// SPDX-License-Identifier: GPL-3.0-or-later
//
// The screen shown while the server moves the character to another region (a city gate, a dungeon, a
// teleporter), drawn as retail draws it from the user's own install (converted by `mpk loading` into
// <library>/loading): one of the destination region's pictures, picked at random (gamedata.mpk loading.dat),
// a 1024 x 256 banner stretched to the screen's width and centred vertically; a random tip from
// data/loading/tips.dat under it in Minion Pro 20 (the region's name when there are no tips); and the
// progress bar (data/loadbar1.tga empty, loadbar2.tga filled). Where retail puts the tip and the bar is
// unmeasured (open question W49): here the tip sits under the banner and the bar under the tip.
// Without converted files it is a plain dark screen with the region's name.

using System.Collections.Generic;
using DaocAssets.Zone;
using Godot;

namespace EodClient;

public partial class LoadingScreen : CanvasLayer
{
    private static Dictionary<int, (string Name, bool Instance, string[] Stems)>? _regions;
    private static string[] _tips = [];
    private static string? _dir;

    private TextureRect _banner = null!;
    private Label _text = null!;
    private TextureProgressBar _bar = null!;
    private ProgressBar? _plainBar;

    /// <summary>Reads the converted table once (library/loading), if there is one.</summary>
    private static void LoadTable(string? libraryDir)
    {
        string? dir = libraryDir is null ? null : System.IO.Path.Combine(libraryDir, "loading");
        if (_dir == dir)
            return;
        _dir = dir;
        _regions = null;
        _tips = [];
        if (dir is null || !System.IO.File.Exists(System.IO.Path.Combine(dir, "loading.csv")))
            return;
        _regions = LoadingScreens.ReadCsv(System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "loading.csv")));
        string tips = System.IO.Path.Combine(dir, "tips.txt");
        if (System.IO.File.Exists(tips))
            _tips = [.. System.IO.File.ReadAllLines(tips, System.Text.Encoding.Latin1).Select(l => l.Trim()).Where(l => l.Length > 0)];
    }

    /// <summary>A loading screen for <paramref name="region"/> (0: not known yet), on top of everything.</summary>
    public static LoadingScreen Create(string? libraryDir, ushort region)
    {
        LoadTable(libraryDir);
        var screen = new LoadingScreen { Layer = 100 };
        screen.Build();
        screen.SetRegion(region);
        return screen;
    }

    private void Build()
    {
        var black = new ColorRect { Color = Colors.Black };
        black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(black);
        _banner = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale };
        AddChild(_banner);
        _text = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _text.AddThemeColorOverride("font_color", new Color(0.86f, 0.80f, 0.66f));
        if (_dir is not null && Font(System.IO.Path.Combine(_dir, "minionpro-regular.ttf")) is FontFile font)
            _text.AddThemeFontOverride("font", font);
        AddChild(_text);

        Texture2D? empty = _dir is null ? null : ModelTexturing.Load("loadbar1", _dir);
        Texture2D? full = _dir is null ? null : ModelTexturing.Load("loadbar2", _dir);
        _bar = new TextureProgressBar { TextureUnder = empty, TextureProgress = full, NinePatchStretch = true, MaxValue = 1, Step = 0.001 };
        AddChild(_bar);
        if (empty is null || full is null)
        {
            _bar.Visible = false;
            _plainBar = new ProgressBar { MaxValue = 1, Step = 0.001, ShowPercentage = false };
            AddChild(_plainBar);
        }
    }

    // The viewport exists only once the screen is in the tree.
    public override void _Ready()
    {
        GetViewport().SizeChanged += Layout;
        Layout();
    }

    public override void _ExitTree()
    {
        if (GetViewport() is Viewport v)
            v.SizeChanged -= Layout;
    }

    private static FontFile? Font(string path)
    {
        if (!System.IO.File.Exists(path))
            return null;
        var font = new FontFile();
        return font.LoadDynamicFont(path) == Error.Ok ? font : null;
    }

    /// <summary>Picks the region's picture and the text. Called again once the region is known.</summary>
    public void SetRegion(ushort region)
    {
        string name = "";
        _banner.Texture = null;
        if (_regions is not null && region != 0 && _regions.TryGetValue(region, out var r))
        {
            name = r.Name;
            if (r.Stems.Length > 0 && _dir is not null)
                _banner.Texture = ModelTexturing.Load(r.Stems[System.Random.Shared.Next(r.Stems.Length)], _dir);
        }
        if (name.Length == 0 && region != 0)
            name = DaocProtocol.World.Zones.All.FirstOrDefault(z => z.RegionId == region)?.Name ?? "";
        _text.Text = _tips.Length > 0 ? _tips[System.Random.Shared.Next(_tips.Length)]
            : name.Length > 0 ? name : "Loading…";
        GD.Print($"[loading] region {region}{(name.Length > 0 ? $" ({name})" : "")}: {(_banner.Texture is null ? "no picture" : "picture")}");
        Layout();
    }

    /// <summary>0 to 1.</summary>
    public void SetProgress(double done)
    {
        _bar.Value = done;
        if (_plainBar is not null)
            _plainBar.Value = done;
    }

    // Banner: the full width, a quarter as tall (1024 x 256), centred. Text and bar below it, scaled with it.
    private void Layout()
    {
        if (_banner is null || !IsInsideTree() || GetViewport() is not Viewport v)
            return;
        Vector2 size = v.GetVisibleRect().Size;
        float scale = size.X / 1024f, bannerHeight = 256f * scale, top = (size.Y - bannerHeight) / 2;
        _banner.Position = new Vector2(0, top);
        _banner.Size = new Vector2(size.X, bannerHeight);
        _banner.Visible = _banner.Texture is not null;
        float below = _banner.Texture is null ? size.Y / 2 - 20 * scale : top + bannerHeight + 16 * scale;
        _text.AddThemeFontSizeOverride("font_size", Mathf.Max(14, Mathf.RoundToInt(20 * scale)));
        _text.Position = new Vector2(size.X * 0.1f, below);
        _text.Size = new Vector2(size.X * 0.8f, 0);
        var barSize = new Vector2(Mathf.Min(size.X * 0.9f, 1000 * scale), 43 * scale);
        var barAt = new Vector2((size.X - barSize.X) / 2, below + 70 * scale);
        _bar.Position = barAt;
        _bar.Size = barSize;
        if (_plainBar is not null)
        {
            _plainBar.Position = barAt;
            _plainBar.Size = new Vector2(barSize.X, 8);
        }
    }
}
