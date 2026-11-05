// SPDX-License-Identifier: GPL-3.0-or-later
//
// A player's Catacombs appearance on its figure (roadmap M7b): the character's appearance bytes
// (DaocProtocol CharacterLook: face, hair style and colour, eye colour, skin tone) picked from the
// race's options file, models/fig3_<n>.json, which mpk figures writes from the install's fig3 tables.
// The face sliders drive the head's morph targets (models/fig3_<n>.json "sliders": each slider's low and
// high target). Not yet: the skin tone (read, not drawn: open question W30), mood, tattoos.

using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public static class FigureLook
{
    // The worn slots' mesh names: "<slot><n>" (n from 1), "<slot><n>_<k>" for a part's extra shapes.
    private static readonly System.Text.RegularExpressions.Regex WornMesh =
        new(@"^(body|lbody|legs|boots|arms|gloves|cloak)(\d+)(_\d+)?$");

    // The items table (models/cata_items.json), parsed once per path.
    private static readonly System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement?> Items = new();

    /// <summary>
    /// Dresses a Catacombs figure in worn items (models/cata_items.json, from mpk figures): each item's
    /// slots show the mesh variant it picks, wearing its texture; slots without an item keep the figure's
    /// own (models/fig3_&lt;n&gt;.json "defaults"). The body mesh is 7 for a robe, else the torso item's
    /// extension, else the number in its texture's name. Under body mesh 7 the arms and legs hide and the
    /// gloves and boots take mesh 3; under 8 the arms hide and the gloves take mesh 3 (the client's rule, as
    /// published by OfflineDAoC, GPL-3.0). A cloak shows only when worn and not hidden; with the hood up,
    /// its mesh 2. A variant the race lacks falls back to the default. Each piece wears its item's dye
    /// (<see cref="ItemPaint.Dye"/>, models/colours.json), and a cloak its guild emblem. Returns a description
    /// for the log.
    /// </summary>
    public static string Dress(Node3D figure, string optionsPath, string itemsPath, string modelsPath, string coloursPath, int model,
        System.Collections.Generic.IReadOnlyList<WornItem> worn, bool hoodUp, bool cloakHidden, params string[] dirs)
    {
        if (Options(optionsPath) is not { } o)
            return "";
        System.Text.Json.JsonElement? items = Json(Items, itemsPath);
        // The model's own look (models/cata_models.json): its skin and mesh per slot, before any item.
        System.Text.Json.JsonElement? row = Json(Items, modelsPath) is { } models
            && models.TryGetProperty(model.ToString(System.Globalization.CultureInfo.InvariantCulture), out var r) ? r : null;
        string side = o.TryGetProperty("female", out var fem) && fem.GetBoolean() ? "f" : "m";
        var chosen = new System.Collections.Generic.Dictionary<string, int>();
        var textures = new System.Collections.Generic.Dictionary<string, string>();
        var have = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<int>>();
        foreach (var p in o.GetProperty("variants").EnumerateObject())
            have[p.Name] = p.Value.EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
        foreach (var p in (row is { } rd && rd.TryGetProperty("defaults", out var md) ? md : o.GetProperty("defaults")).EnumerateObject())
            chosen[p.Name] = p.Value.GetInt32();
        chosen["cloak"] = 0;
        if (row is { } rs && rs.TryGetProperty("skins", out var skins))
            foreach (var p in skins.EnumerateObject())
                if (p.Name is "body" or "legs" or "boots" or "arms" or "gloves" or "lbody")
                    textures[p.Name] = p.Value.GetString() ?? "";

        bool cloakWorn = false;
        var itemOf = new System.Collections.Generic.Dictionary<string, WornItem>();   // the item that dresses each slot
        foreach (WornItem item in worn)
        {
            if (items is not { } table || !table.TryGetProperty(item.Model.ToString(System.Globalization.CultureInfo.InvariantCulture), out var entry)
                || !entry.TryGetProperty(side, out var pieces))
                continue;
            foreach (var piece in pieces.EnumerateObject())
            {
                string slot = piece.Name;
                int v = piece.Value[0].GetInt32();
                if (slot == "body")
                    v = entry.GetProperty("robe").GetBoolean() ? 7
                        : item.Extension is > 0 and <= 8 ? item.Extension
                        : Math.Max(1, entry.GetProperty("name_variant").GetInt32());
                chosen[slot] = v;
                textures[slot] = piece.Value[1].GetString() ?? "";
                itemOf[slot] = item;
                cloakWorn |= slot == "cloak";
            }
        }
        int body = chosen.GetValueOrDefault("body");
        bool hooded = false;
        if (body == 7) { chosen["arms"] = 0; chosen["legs"] = 0; chosen["gloves"] = 3; chosen["boots"] = 3; }
        else if (body == 8) { chosen["arms"] = 0; chosen["gloves"] = 3; }
        if (!cloakWorn || cloakHidden) chosen["cloak"] = 0;
        else if (hoodUp && have.TryGetValue("cloak", out var cloaks) && cloaks.Contains(2)) { chosen["cloak"] = 2; hooded = true; }
        // A mesh the race lacks: its default (an item on a race without that shape).
        foreach (string slot in chosen.Keys.ToList())
            if (chosen[slot] > 0 && have.TryGetValue(slot, out var h) && !h.Contains(chosen[slot]))
                chosen[slot] = slot == "cloak" ? h.Min() : o.GetProperty("defaults").TryGetProperty(slot, out var d) ? d.GetInt32() : 1;

        var loaded = new System.Collections.Generic.Dictionary<string, ImageTexture?>();
        foreach (Node n in figure.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)n;
            if (hooded && mi.Name.ToString().StartsWith("hair", StringComparison.Ordinal))
                mi.Visible = false;    // the hood covers the hair
            var m = WornMesh.Match(mi.Name.ToString());
            if (!m.Success)
                continue;
            string slot = m.Groups[1].Value;
            mi.Visible = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) == chosen.GetValueOrDefault(slot);
            if (!mi.Visible || !textures.TryGetValue(slot, out string? texName))
                continue;
            if (!loaded.TryGetValue(texName, out ImageTexture? tex))
                loaded[texName] = tex = ModelTexturing.Load(System.IO.Path.GetFileNameWithoutExtension(texName), dirs);
            // From the imported material each time, so a dye or emblem taken off doesn't linger.
            if (tex is null || mi.Mesh?.SurfaceGetMaterial(0) is not BaseMaterial3D original)
                continue;
            WornItem? by = itemOf.TryGetValue(slot, out WornItem it) ? it : null;
            Color dye = (by is { } d ? ItemPaint.Dye(coloursPath, d.Dye) : null) ?? Colors.White;
            if (slot == "cloak" && by?.Emblem is { } emblem)
            {
                mi.MaterialOverride = ItemPaint.Material(coloursPath, tex, emblem, itemLayout: false, original.AlbedoColor, dirs);
                continue;
            }
            var mat = (BaseMaterial3D)original.Duplicate();
            mat.AlbedoTexture = tex;
            mat.AlbedoColor = original.AlbedoColor * dye;
            mi.MaterialOverride = mat;
        }
        return " wearing " + string.Join(" ", chosen.Where(kv => kv.Value > 0)
            .Select(kv => $"{kv.Key} {kv.Value}{(textures.TryGetValue(kv.Key, out string? t) ? $"={t}" : "")}" +
                (itemOf.TryGetValue(kv.Key, out WornItem w) && (w.Emblem is not null || w.Dye > 0)
                    ? w.Emblem is { } e ? $" emblem {e.Logo}/{e.Pattern}/{e.Primary}/{e.Secondary}" : $" dye {w.Dye}" : "")));
    }

    // Item models (models/<nif>.glb), loaded and textured once each, duplicated per figure.
    private static readonly System.Collections.Generic.Dictionary<string, Node3D?> ItemModels = new();

    /// <summary>
    /// Hangs a Catacombs figure's weapons, shield and helm on its skeleton (models/cata_gear.json, from mpk
    /// figures), replacing any it had. A weapon in use goes in the hand by its grip ("Bip01 R Held"; the left
    /// hand and ranged "Bip01 L Held") when <paramref name="drawn"/> (attack mode) or when it doesn't sheathe
    /// (staves, most polearms and bows). Otherwise a sheathable weapon, in use or not, hangs by its belt frame
    /// on the opposite hip (right hand: "Bip01 L Belt") or, two-handed and ranged, by its back frame
    /// ("Bip01 L/R Back"), so a spare two-hander stays on the back while sword and shield are drawn. A shield
    /// in use is on "Bip01 L Shield" either way, an unused one not drawn; the helm on "Bip01 Helmet", hiding
    /// the hair, unless hidden or under a raised hood. Rules as OfflineDAoC (GPL-3.0) published them from the
    /// client. Each wears its dye, and an emblem item (a shield whose base slot is the emblem pattern) its
    /// guild emblem over its own surface (<see cref="ItemPaint"/>). Returns a log description.
    /// </summary>
    public static string Gear(Node3D figure, string optionsPath, string gearPath, string coloursPath, string modelsDir,
        System.Collections.Generic.IReadOnlyList<WornItem> worn, byte activeWeapons, bool helmHidden, bool hoodUp, bool drawn,
        params string[] dirs)
    {
        if (Options(optionsPath) is not { } o || Json(Items, gearPath) is not { } gear
            || figure.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is not Skeleton3D skel)
        {
            GD.PrintErr($"[gear] nothing hung: options {Options(optionsPath) is not null}, gear table {Json(Items, gearPath) is not null}, " +
                $"skeleton {figure.FindChildren("*", "Skeleton3D", true, false).Count > 0}");
            return "";
        }
        foreach (Node old in skel.GetChildren())
            if (old.Name.ToString().StartsWith("Gear_", StringComparison.Ordinal))
            {
                skel.RemoveChild(old);
                old.QueueFree();
            }
        string prefix = o.TryGetProperty("prefix", out var pf) ? pf.GetString() ?? "" : "";
        (byte? right, byte? left) = WornSlot.Active(activeWeapons);
        var hung = new System.Collections.Generic.List<string>();
        bool helmed = false;
        foreach (WornItem item in worn)
        {
            if (!gear.GetProperty("items").TryGetProperty(item.Model.ToString(System.Globalization.CultureInfo.InvariantCulture), out var entry))
                continue;
            string? nif = null, bone = null, frame = "held";
            string? retexture = null;
            if (item.Slot == WornSlot.Helm)
            {
                if (helmHidden || hoodUp || !entry.GetProperty("helms").TryGetProperty(prefix, out var h))
                    continue;
                (nif, bone) = (h.GetString(), "Bip01 Helmet");
                retexture = entry.TryGetProperty("helm_texture", out var ht) ? ht.GetString() : null;
            }
            else if (item.Slot is >= WornSlot.RightHand and <= WornSlot.Ranged)
            {
                nif = entry.GetProperty("nif").GetString();
                retexture = entry.TryGetProperty("texture", out var tt) ? tt.GetString() : null;
                bool inUse = item.Slot == right || item.Slot == left;
                bool lefty = item.Slot is WornSlot.LeftHand or WornSlot.Ranged;
                if (entry.GetProperty("shield").GetBoolean())
                    bone = inUse ? "Bip01 L Shield" : null;
                else if (inUse && (drawn || !entry.GetProperty("belt").GetBoolean()))
                    bone = lefty ? "Bip01 L Held" : "Bip01 R Held";
                else if (entry.GetProperty("belt").GetBoolean())
                    (bone, frame) = item.Slot switch
                    {
                        WornSlot.RightHand => ("Bip01 L Belt", "belt"),
                        WornSlot.LeftHand => ("Bip01 R Belt", "belt"),
                        WornSlot.TwoHand => ("Bip01 L Back", "back"),
                        _ => ("Bip01 R Back", "back"),
                    };
            }
            if (nif is null || bone is null)
                continue;
            if (skel.FindBone(bone) < 0 || !gear.GetProperty("nifs").TryGetProperty(nif, out var frames)
                || ItemModel(modelsDir, nif, dirs) is not { } template)
            {
                GD.PrintErr($"[gear] item {item.Model} slot {item.Slot}: {(skel.FindBone(bone) < 0 ? $"no bone '{bone}' in {skel.Name}" : $"no model {nif}")}");
                continue;
            }
            var f = frames.GetProperty(frame);
            float F(int i) => f[i].GetSingle();
            var model = (Node3D)template.Duplicate();
            Texture2D? swap = retexture is not null ? ModelTexturing.Load(System.IO.Path.GetFileNameWithoutExtension(retexture), dirs) : null;
            Color dye = ItemPaint.Dye(coloursPath, item.Dye) ?? Colors.White;
            foreach (Node mn in model.FindChildren("*", "MeshInstance3D", true, false))
            {
                if (mn is not MeshInstance3D mmi || mmi.Mesh?.SurfaceGetMaterial(0) is not BaseMaterial3D imported)
                    continue;
                // An emblem item: its base texture is the emblem pattern; the surface is the item's own texture
                // (objects.csv "Apply Texture") or its detail layer, on the third UV set.
                if (imported.ResourceName.StartsWith("cloakpattern", StringComparison.OrdinalIgnoreCase))
                {
                    Texture2D? surface = swap ?? (ModelTexturing.Layer(imported, "detail") is { } detail ? ModelTexturing.Load(detail, dirs) : null);
                    mmi.MaterialOverride = ItemPaint.Material(coloursPath, surface, item.Emblem, itemLayout: true, dye, dirs);
                }
                else if ((swap is not null || dye != Colors.White) && mmi.GetActiveMaterial(0)?.Duplicate() is BaseMaterial3D mat)
                {
                    if (swap is not null)
                        mat.AlbedoTexture = swap;
                    mat.AlbedoColor *= dye;
                    mmi.MaterialOverride = mat;
                }
            }
            model.Transform = new Transform3D(new Basis(new Vector3(F(0), F(1), F(2)), new Vector3(F(3), F(4), F(5)), new Vector3(F(6), F(7), F(8))),
                new Vector3(F(9), F(10), F(11)));
            var attach = new BoneAttachment3D { Name = $"Gear_{item.Slot}", BoneName = bone };
            skel.AddChild(attach);
            attach.AddChild(model);
            helmed |= item.Slot == WornSlot.Helm;
            hung.Add($"{nif}@{bone}" + (item.Emblem is { } em ? $" emblem {em.Logo}/{em.Pattern}/{em.Primary}/{em.Secondary}" : item.Dye > 0 ? $" dye {item.Dye}" : ""));
        }
        if (helmed)
            foreach (Node n in figure.FindChildren("hair*", "MeshInstance3D", true, false))
                ((MeshInstance3D)n).Visible = false;   // under the helm (its "Helm Hair" squash shapes: W33)
        return hung.Count == 0 ? "" : " holding " + string.Join(" ", hung);
    }

    /// <summary>
    /// The combat idle for the weapon in use (the right hand's: a one-hander, two-hander or bow; else the left's),
    /// by its objects.csv "Combat Stance" (cata_gear.json "stance"): 2 two-hand, 3 polearm, 4 staff, 5 bow,
    /// 6 crossbow, 13 hand to hand, 14 scythe, anything else (and no weapon) the one-hand "combat_idle". The
    /// figures carry these clips from canims.csv (mpk figures).
    /// </summary>
    public static string CombatIdle(string gearPath, System.Collections.Generic.IReadOnlyList<WornItem> worn, byte activeWeapons)
    {
        (byte? right, byte? left) = WornSlot.Active(activeWeapons);
        int stance = 0;
        if (Json(Items, gearPath) is { } gear)
            foreach (byte? slot in new[] { right, left })
                if (slot is { } sl && worn.FirstOrDefault(w => w.Slot == sl) is { Model: > 0 } item
                    && gear.GetProperty("items").TryGetProperty(item.Model.ToString(System.Globalization.CultureInfo.InvariantCulture), out var entry)
                    && !entry.GetProperty("shield").GetBoolean())
                {
                    stance = entry.GetProperty("stance").GetInt32();
                    break;
                }
        return stance switch
        {
            2 => "combat_idle_2h", 3 => "combat_idle_pole", 4 => "combat_idle_staff", 5 => "combat_idle_bow",
            6 => "combat_idle_xbow", 13 => "combat_idle_h2h", 14 => "combat_idle_scy", _ => "combat_idle",
        };
    }

    private static Node3D? ItemModel(string modelsDir, string nif, string[] dirs)
    {
        string path = System.IO.Path.Combine(modelsDir, nif + ".glb");
        if (ItemModels.TryGetValue(path, out Node3D? cached))
            return cached;
        Node3D? result = null;
        var doc = new GltfDocument();
        var state = new GltfState();
        if (Godot.FileAccess.FileExists(path) && doc.AppendFromFile(path, state) == Error.Ok && doc.GenerateScene(state) is Node3D m)
        {
            ModelTexturing.TagLayers(state);
            ModelTexturing.Apply(m, dirs);
            result = m;
        }
        ItemModels[path] = result;
        return result;
    }

    private static System.Text.Json.JsonElement? Json(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement?> cache, string path)
    {
        if (!cache.TryGetValue(path, out System.Text.Json.JsonElement? e))
        {
            e = Godot.FileAccess.FileExists(path)
                ? System.Text.Json.JsonDocument.Parse(Godot.FileAccess.GetFileAsString(path)).RootElement : null;
            cache[path] = e;
        }
        return e;
    }

    private static System.Text.Json.JsonElement? Options(string optionsPath)
    {
        if (!Looks.TryGetValue(optionsPath, out System.Text.Json.JsonElement? opts))
        {
            opts = Godot.FileAccess.FileExists(optionsPath)
                ? System.Text.Json.JsonDocument.Parse(Godot.FileAccess.GetFileAsString(optionsPath)).RootElement : null;
            Looks[optionsPath] = opts;
        }
        return opts;
    }
    // Each options file, parsed once (null: missing).
    private static readonly System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement?> Looks = new();

    /// <summary>
    /// Dresses a Catacombs figure in a character's appearance: the chosen hair style shows (the other
    /// styles hide; none for a bald style), wearing its colour's texture and tint; the head wears the
    /// chosen face texture with the eye colour texture multiplied over it (same UVs). Materials are per-instance copies. An index the race doesn't offer keeps the figure's own.
    /// <paramref name="optionsPath"/> is the race's models/fig3_<n>.json (mpk figures); <paramref name="dirs"/>
    /// are searched for textures. Returns a short description for the log ("" if the figure has no options).
    /// </summary>
    public static string Apply(Node3D figure, string optionsPath, CharacterLook look, params string[] dirs)
    {
        if (Options(optionsPath) is not { } o)
            return "";
        static System.Text.Json.JsonElement? Pick(System.Text.Json.JsonElement list, int i) =>
            i >= 0 && i < list.GetArrayLength() && list[i].ValueKind != System.Text.Json.JsonValueKind.Null ? list[i] : null;
        static Color Rgb(System.Text.Json.JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        ImageTexture? Tex(System.Text.Json.JsonElement? name) =>
            name?.GetString() is { } n ? ModelTexturing.Load(System.IO.Path.GetFileNameWithoutExtension(n), dirs) : null;

        System.Text.Json.JsonElement hairList = o.GetProperty("hair");
        int style = look.Hair < hairList.GetArrayLength() ? look.Hair : o.GetProperty("default_hair").GetInt32();
        System.Text.Json.JsonElement chosen = hairList[style];
        string? node = chosen.GetProperty("node").GetString();
        System.Text.Json.JsonElement? colour = Pick(chosen.GetProperty("colours"), look.HairColour);
        ImageTexture? face = Tex(Pick(o.GetProperty("faces"), look.Face));
        ImageTexture? eyes = Tex(Pick(o.GetProperty("eyes"), look.EyeColour));

        foreach (Node n in figure.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)n;
            string name = mi.Name.ToString();
            if (Part(name, "head"))
            {
                if (mi.GetActiveMaterial(0)?.Duplicate() is not BaseMaterial3D m)
                    continue;
                if (face is not null) m.AlbedoTexture = face;
                if (eyes is not null)
                {
                    m.DetailEnabled = true;
                    m.DetailAlbedo = eyes;
                    m.DetailUVLayer = BaseMaterial3D.DetailUV.UV1;
                    m.DetailBlendMode = BaseMaterial3D.BlendModeEnum.Mul;
                }
                mi.MaterialOverride = m;
                SetSliders(mi, o, look);
            }
            else if (name.StartsWith("hair", StringComparison.Ordinal))
            {
                string styleNode = name.Split('_')[0];
                mi.Visible = styleNode == node;
                if (mi.Visible && colour is { } c && mi.GetActiveMaterial(0)?.Duplicate() is BaseMaterial3D m)
                {
                    if (Tex(c.GetProperty("tex")) is { } t) m.AlbedoTexture = t;
                    m.AlbedoColor = Rgb(c.GetProperty("tint"));
                    mi.MaterialOverride = m;
                }
            }
        }
        return $" (face {look.Face}, hair {style}{(node is null ? " bald" : "")} colour {look.HairColour}, eyes {look.EyeColour}, skin {look.SkinTone} not drawn, " +
            $"sliders {look.Slider(0)}{look.Slider(1)}{look.Slider(2)}{look.Slider(3)})";

        static void SetSliders(MeshInstance3D head, System.Text.Json.JsonElement options, CharacterLook look)
        {
            // Eye size and lip size both 0: a character never customised (ours, made with custom mode 0:
            // step 1, "generated"); retail-made ones store 0x44 (all neutral). Drawn neutral, not all-low.
            if (!options.TryGetProperty("sliders", out System.Text.Json.JsonElement sliders) || (look.EyeSize == 0 && look.LipSize == 0))
                return;
            int shapes = head.GetBlendShapeCount();
            for (int k = 0; k < sliders.GetArrayLength() && k < 4; k++)
            {
                // 0..8, 4 neutral: below 4 blends toward the low target, above toward the high one.
                float v = Mathf.Clamp(look.Slider(k), 0, 8);
                int low = sliders[k].GetProperty("low").GetInt32(), high = sliders[k].GetProperty("high").GetInt32();
                if (low >= 0 && low < shapes) head.SetBlendShapeValue(low, Mathf.Clamp((4 - v) / 4, 0, 1));
                if (high >= 0 && high < shapes) head.SetBlendShapeValue(high, Mathf.Clamp((v - 4) / 4, 0, 1));
            }
        }

        static bool Part(string name, string label) => name == label || name.StartsWith(label + "_", StringComparison.Ordinal);
    }
}
