// SPDX-License-Identifier: GPL-3.0-or-later

using DaocAssets.Nif;
using SharpGLTF.Schema2;

namespace DaocAssets.Tests;

/// <summary>
/// Collision volumes (shapes under "collidee", "collider", "collision …" nodes) reach the exported model as
/// "eodcollide_" nodes for the client to collide with; other helpers (climb, pick, shadow) still don't; the
/// node-name rule keeps "collisionswitch" (which holds visible parts) out.
/// </summary>
public sealed class CollisionExportTests
{
    private static NifMesh Tri(string name) => new()
    {
        Name = name, Vertices = [new(0, 0, 0), new(40, 0, 0), new(0, 0, 40)], Triangles = [(0, 1, 2)],
    };

    [Fact]
    public void ACollisionVolumeIsExportedUnderItsPrefixAndOtherHelpersAreNot()
    {
        var model = new NifModel
        {
            Version = new NifVersion(4, 2, 1, 0),
            Meshes =
            [
                Tri("wall") with { TextureFile = "stone.dds" },
                Tri("hull") with { Helper = true, Collision = true },
                Tri("ladder") with { Helper = true },                       // a climb volume: not exported
                Tri("tower") with { TextureFile = "stone.dds", Collision = true },   // drawn and solid
            ],
        };
        ModelRoot gltf = GltfExporter.BuildScene(model).ToGltf2();
        string[] nodes = gltf.LogicalNodes.Select(n => n.Name).ToArray();

        Assert.Contains(GltfExporter.CollisionPrefix + "hull", nodes);
        Assert.DoesNotContain(nodes, n => n.Contains("ladder"));
        Assert.Contains("wall", nodes);
        Assert.Contains("tower", nodes);   // a textured shape under a collision node stays a drawn one
        Mesh hull = gltf.LogicalNodes.Single(n => n.Name == GltfExporter.CollisionPrefix + "hull").Mesh;
        Assert.Equal(1, hull.Primitives.Single().GetIndices().Count / 3);
        Assert.Null(hull.Primitives.Single().GetVertexAccessor("NORMAL"));   // positions only
    }

    [Theory]
    [InlineData("collidee", true)]
    [InlineData("collidee016", true)]
    [InlineData("collideeH", true)]
    [InlineData("collider fence", true)]
    [InlineData("collision tower", true)]
    [InlineData("collisionswitch", false)]
    [InlineData("pickee", false)]
    [InlineData("climb", false)]
    public void WhichNodesAreCollision(string name, bool collision) =>
        Assert.Equal(collision, NifFile.IsCollisionName(name));
}
