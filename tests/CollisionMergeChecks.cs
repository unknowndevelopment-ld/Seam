using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeWalker.GameFiles;
using Seam;
using SharpDX;

internal static class CollisionMergeChecks
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        int passed = 0;
        Action<bool, string> check = (value, label) => { if (!value) throw new InvalidOperationException("Collision merge regression: " + label); passed++; Console.WriteLine("  OK " + label); };
        var first = Write(directory, "first", "custom_collision.ybn", Graph(42, false));
        var second = Write(directory, "second", "custom_collision.ybn", Graph(42, true));
        var plan = new AutomaticMergePlan(); var output = CollisionMerger.Merge(new[] { first, second }, plan).Single();
        var loaded = Load(output.Data);
        var leaves = Leaves(loaded.Bounds).ToArray();
        check(leaves.OfType<BoundGeometry>().Sum(g => g.Polygons.Length) == 3, "automatic collision union removes exact duplicate box, sphere and capsule polygons");
        check(leaves.Length == 2 && plan.RemovedPolygons == 3 && plan.DuplicateBounds == 1, "duplicate polygon component is consolidated while the additional component remains");
        check(leaves.OfType<BoundGeometry>().Single().Polygons.Select(p => p.Type).OrderBy(p => p).SequenceEqual(new[] { BoundPolygonType.Sphere, BoundPolygonType.Capsule, BoundPolygonType.Box }.OrderBy(p => p)), "all original primitive polygon types survive collision serialization");

        var differentMaterial = Write(directory, "material", "custom_collision.ybn", Graph(43, false));
        plan = new AutomaticMergePlan(); output = CollisionMerger.Merge(new[] { first, differentMaterial }, plan).Single();
        leaves = Leaves(Load(output.Data).Bounds).ToArray();
        check(leaves.OfType<BoundGeometry>().Sum(g => g.Polygons.Length) == 6 && plan.RemovedPolygons == 0, "matching primitive geometry with different collision material remains distinct");
        check(leaves.OfType<BoundGeometry>().SelectMany(g => g.Polygons).Select(p => p.Material.Data1).Distinct().OrderBy(v => v).SequenceEqual(new uint[] { 42, 43 }), "collision union retains both material definitions");

        var sourceA = Write(directory, "transformA", "transformed.ybn", PrimitiveGraph(new Vector3(10, 2, 3), false));
        var sourceB = Write(directory, "transformB", "transformed.ybn", PrimitiveGraph(new Vector3(20, 2, 3), true));
        plan = new AutomaticMergePlan(); output = CollisionMerger.Merge(new[] { sourceA, sourceB }, plan).Single();
        leaves = Leaves(Load(output.Data).Bounds).ToArray();
        check(leaves.Length == 3, "identical primitive shapes at different transforms are retained");
        check(leaves.Any(b => Math.Abs(b.Transform.TranslationVector.X - 10) < 0.001) && leaves.Any(b => Math.Abs(b.Transform.TranslationVector.X - 20) < 0.001), "transformed collision components retain both placements");

        var primitiveA = Write(directory, "primitiveA", "primitives.ybn", PrimitiveGraph(new Vector3(10, 2, 3), false));
        var primitiveB = Write(directory, "primitiveB", "primitives.ybn", PrimitiveGraph(new Vector3(10, 2, 3), true));
        plan = new AutomaticMergePlan(); output = CollisionMerger.Merge(new[] { primitiveA, primitiveB }, plan).Single();
        check(Leaves(Load(output.Data).Bounds).Count() == 2 && plan.DuplicateBounds == 1, "exact duplicate standalone primitive bounds are consolidated without losing another bound");
        return passed;
    }

    private static YbnFile Graph(uint material, bool extra)
    {
        var geometry = new BoundBVH { Type = BoundsType.GeometryBVH, FileVFT = 1080228536, BoxMin = -Vector3.One, BoxMax = Vector3.One, SphereRadius = 2,
            Vertices = new[] { new Vector3(-1, -1, -1), new Vector3(1, 1, -1), new Vector3(1, -1, 1), new Vector3(-1, 1, 1), Vector3.Zero, new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0) } };
        geometry.Polygons = new BoundPolygon[] { new BoundPolygonBox { boxIndex1 = 0, boxIndex2 = 1, boxIndex3 = 2, boxIndex4 = 3 },
            new BoundPolygonSphere { sphereIndex = 4, sphereRadius = 0.25f }, new BoundPolygonCapsule { capsuleIndex1 = 5, capsuleIndex2 = 6, capsuleRadius = 0.1f } };
        foreach (var polygon in geometry.Polygons) { polygon.Owner = geometry; polygon.MaterialCustom = new BoundMaterial_s { Data1 = material }; }
        geometry.BuildMaterials(); geometry.UpdateEdgeIndices();
        var children = new List<Bounds> { geometry };
        if (extra) children.Add(Box(new Vector3(30, 0, 0)));
        return Root(children);
    }
    private static YbnFile PrimitiveGraph(Vector3 translation, bool extra)
    {
        var children = new List<Bounds> { Box(translation) };
        if (extra) children.Add(new BoundSphere { Type = BoundsType.Sphere, FileVFT = 1080221960, BoxMin = -Vector3.One, BoxMax = Vector3.One,
            SphereRadius = 1, Transform = Matrix.Translation(40, 0, 0), TransformInv = Matrix.Translation(-40, 0, 0) });
        return Root(children);
    }
    private static Bounds Box(Vector3 translation) => new BoundBox { Type = BoundsType.Box, FileVFT = 1080221016, BoxMin = -Vector3.One, BoxMax = Vector3.One, SphereRadius = 2,
        Transform = Matrix.RotationZ(0.2f) * Matrix.Translation(translation), TransformInv = Matrix.Invert(Matrix.RotationZ(0.2f) * Matrix.Translation(translation)) };
    private static YbnFile Root(List<Bounds> children)
    {
        var root = new BoundComposite { Type = BoundsType.Composite, FileVFT = 1080212136, BoxMin = new Vector3(-50), BoxMax = new Vector3(50), SphereRadius = 90,
            Children = new ResourcePointerArray64<Bounds> { data_items = children.ToArray() } };
        foreach (var child in children) child.Parent = root;
        root.UpdateChildrenFlags(); root.UpdateChildrenBounds(); root.UpdateChildrenTransformations(); root.BuildBVH();
        return new YbnFile { Bounds = root };
    }
    private static MapAsset Write(string directory, string pack, string name, YbnFile file)
    {
        string folder = Path.Combine(directory, pack); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name); var bytes = file.Save(); File.WriteAllBytes(path, bytes);
        return new MapAsset { Pack = new MapPack { Name = pack, Root = folder }, Name = name, Path = path, Extension = ".ybn", Ybn = Load(bytes) };
    }
    private static YbnFile Load(byte[] bytes) { var file = new YbnFile(); file.Load(bytes); return file; }
    private static IEnumerable<Bounds> Leaves(Bounds bound)
    { if (bound is BoundComposite composite) { foreach (var child in composite.Children.data_items) foreach (var leaf in Leaves(child)) yield return leaf; } else yield return bound; }
}
