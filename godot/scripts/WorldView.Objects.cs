// SPDX-License-Identifier: GPL-3.0-or-later
//
// WorldView: the server's own objects (S→C 0xD9: carts, banners, items on the ground, doors that aren't part of a zone
// model), drawn from their model number through models/objects.csv (`mpk objects`: objects.csv → items.csv → the model,
// converted into the library). Turned as a zone fixture with the same angle would be (heading 0-4095 as 0-360°): that
// a server heading means what fixtures.csv's angle means is assumed, to check against retail.
// Solid when objects.csv marks it ("Colideable": carts, tents, fences, statues; WorldObjects): walled by its collision
// volumes, or by its drawn shape when it has none (30 of the 206, e.g. the festival carts and tents: assumed), and stood on.

using System.Collections.Generic;
using DaocProtocol.Packets;
using Godot;

namespace EodClient;

public partial class WorldView
{
    private Dictionary<int, DaocAssets.Figures.ObjectModel>? _objectModels;  // model number → glb under models/, solid
    private readonly Dictionary<string, Node3D?> _objectTemplates = [];  // loaded once per glb
    private readonly Dictionary<ushort, Node3D> _objectNodes = [];

    // The object's model, placed; null when there's no library or the model isn't drawable (logged once per object).
    private Node3D? PlaceWorldObject(ObjectCreate o)
    {
        string lib = _libraryPath ?? "";
        if (lib.Length == 0)
            return null;
        if (_objectModels is null)
        {
            string table = lib.PathJoin("models").PathJoin("objects.csv");
            _objectModels = Godot.FileAccess.FileExists(table)
                ? DaocAssets.Figures.WorldObjects.ReadTable(Godot.FileAccess.GetFileAsString(table)) : [];
            GD.Print($"[object] {_objectModels.Count} drawable object models{(_objectModels.Count == 0 ? " (run make objects)" : "")}");
        }
        if (!_objectModels.TryGetValue(o.Model, out DaocAssets.Figures.ObjectModel entry))
            return null;
        string glb = entry.Model;
        if (!_objectTemplates.TryGetValue(glb, out Node3D? template))
        {
            string path = lib.PathJoin("models").PathJoin(glb + ".glb");
            template = Godot.FileAccess.FileExists(path) ? ModelTexturing.LoadGlb(path, TexDirs("items")) : null;
            _objectTemplates[glb] = template;
        }
        if (template is null)
            return null;
        var node = (Node3D)template.Duplicate();
        node.Name = $"object{o.ObjectId}";
        node.Position = ToGodot(o.X, o.Y, o.Z);
        node.Rotation = new Vector3(0, -Mathf.DegToRad((o.Heading & 0x0FFF) * 360f / 4096f), 0);
        node.SetMeta("glb", glb);   // for the wall log and the inspect readout
        _entityRoot.AddChild(node);
        MakeSolid(node, entry.Solid, drawnWalls: true);
        return node;
    }

    private void DrawWorldObject(ObjectCreate o)
    {
        RemoveWorldObjectNode(o.ObjectId);   // sent again (moved, changed): replace it
        Node3D? node = PlaceWorldObject(o);
        if (node is not null)
            _objectNodes[o.ObjectId] = node;
        GD.Print($"[object] {o.ObjectId} \"{o.Name}\" model {o.Model} at {o.X} {o.Y} {o.Z} heading {o.Heading & 0x0FFF}" +
                 (node is null ? " (no model to draw)" : ""));
    }

    private void RemoveWorldObjectNode(ushort objectId)
    {
        if (_objectNodes.Remove(objectId, out Node3D? node))
            node.QueueFree();
    }
}
