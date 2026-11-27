// SPDX-License-Identifier: GPL-3.0-or-later
//
// A model's material animations as the client reads them: "<model>.anim.json" beside its .glb. Each track names the glTF
// node of its mesh (GltfExporter.AnimatedNodeName), what it drives (alpha, color with its target colour, uv with its key
// group, texture with its operation, vis), its keys ([time, value...]) and its controller's flags, start and stop.

using System.Text.Json;

namespace DaocAssets.Nif;

public static class NifMaterialAnimJson
{
    /// <summary>The file name beside a model's .glb.</summary>
    public static string PathFor(string glbPath) => Path.ChangeExtension(glbPath, null) + ".anim.json";

    public static string ToJson(NifModel model)
    {
        var meshes = model.Meshes.ToDictionary(m => m.Name, m => GltfExporter.AnimatedNodeName(m));
        var tracks = model.MaterialAnims.Where(a => meshes.ContainsKey(a.Mesh)).Select(a => new Dictionary<string, object>
        {
            ["node"] = meshes[a.Mesh],
            ["kind"] = a.Kind,
            ["target"] = a.Target,
            ["keys"] = a.Keys.Select(k => new[] { k.Time }.Concat(k.Value).ToArray()).ToArray(),
            ["flags"] = a.Flags,
            ["start"] = a.StartTime,
            ["stop"] = a.StopTime,
        }).ToList();
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["tracks"] = tracks });
    }
}
