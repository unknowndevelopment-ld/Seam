using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using CodeWalker.GameFiles;
using Seam;

internal static class Program
{
    private static int passed;

    [STAThread]
    private static int Main()
    {
        string directory = Path.Combine(RepositoryRoot(), "artifacts", "checks",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(directory);
        Console.WriteLine("Artifacts: " + directory);
        try
        {
            Run(directory);
            Console.WriteLine("PASS: " + passed + " integration assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL after " + passed + " assertions: " + error);
            return 1;
        }
    }

    private static void Run(string directory)
    {
        GameInstallationChecks.Run(directory, Assert);
        string[] packs = MergeWorkspace.CreateDemo(Path.Combine(directory, "sources"));
        // Real resources in separate nested folders catch project restoration that accidentally
        // regroups one logical pack into several packs based on immediate parent directories.
        Relocate(packs[0], "seam_tower.ydr", "models");
        Relocate(packs[0], "seam_materials.ytd", "textures");
        var originalHashes = SourceHashes(packs);
        var workspace = new MergeWorkspace();
        workspace.Import(packs);
        Assert(workspace.Packs.Count == 2, "two procedural resource packs imported");
        Assert(workspace.Packs.SelectMany(p => p.Assets).All(a => a.Error == null), "all native resources readable");
        Assert(workspace.Packs.SelectMany(p => p.Assets).Count(a => a.Ytyp?.AllArchetypes?.Length > 0) == 2,
            "binary YTYP archetypes loaded through raw-resource path");
        Assert(workspace.Scene.Any(s => !s.IsCollision && !s.IsProxy), "actual YDR geometry rendered");
        Assert(workspace.Scene.Where(s => !s.IsCollision).SelectMany(s => Surfaces(s.Model))
            .Any(s => s.Geometry is MeshGeometry3D mesh && mesh.TriangleIndices.Count > 0 &&
                      s.Material is DiffuseMaterial material && material.Brush is ImageBrush image &&
                      image.ImageSource is BitmapSource bitmap && bitmap.PixelWidth > 0),
            "YTD pixels resolved into textured mesh materials");
        Assert(workspace.Issues.Any(i => i.Kind == "Duplicate placement" && i.CanAutoFix), "exact standalone duplicate detected");
        Assert(workspace.Issues.Any(i => i.Kind == "Collision bounds overlap"), "physical collision overlap detected");

        string exports = Path.Combine(directory, "exports");
        Directory.CreateDirectory(exports);
        Expect<InvalidOperationException>(() => workspace.Export(exports, "pending"), "unresolved conflicts block export");
        Assert(!Directory.EnumerateFileSystemEntries(exports).Any(), "blocked export leaves output directory empty");
        CheckLodSafety(workspace);
        CheckPlanarCollisions(workspace);

        var duplicate = workspace.Issues.First(i => i.Kind == "Duplicate placement" && i.CanAutoFix);
        string retainedEntity = duplicate.LeftId, removedEntity = duplicate.RightId;
        Assert(workspace.AutoResolveDuplicates() > 0, "verified duplicate cleanup applies resolutions");
        workspace.Refresh();
        Assert(Entities(workspace).Single(e => e.Id == removedEntity).Excluded &&
               !Entities(workspace).Single(e => e.Id == retainedEntity).Excluded,
            "duplicate cleanup removes only lower-priority placement");
        Assert(workspace.ActiveIssueCount > 0, "spatial conflicts still require explicit decisions");
        ResolveRemaining(workspace);
        Assert(workspace.ActiveIssueCount == 0, "explicit decisions clear remaining review");
        Assert(workspace.Packs.SelectMany(p => p.Assets).SelectMany(a => a.Collisions).Any(c => c.Excluded),
            "collision review excludes a selected component");

        // Persist an order different from the import order and a disabled pack, then restore it.
        workspace.Packs.Reverse();
        workspace.Packs[0].Name = "Foundry first";
        workspace.Packs[1].Enabled = false;
        workspace.Refresh();
        string project = Path.Combine(directory, "merged.seam");
        workspace.SaveProject(project);
        var restored = MergeWorkspace.LoadProject(project);
        Assert(State(workspace) == State(restored), "project preserves priority, nested assets, enabled state, and exclusions");
        Assert(restored.Packs.SelectMany(p => p.Assets).All(a => ReferenceEquals(a.Pack, restored.Packs.Single(p => p.Assets.Contains(a)))),
            "restored nested assets point to their logical pack");
        restored.Packs.ForEach(p => p.Enabled = true);
        restored.Refresh();
        Assert(restored.ActiveIssueCount == 0, "saved keep-both decisions survive reloading and re-enabling maps");
        restored.SaveProject(project); // Exercise atomic replacement of an existing project.
        Assert(MergeWorkspace.LoadProject(project).ActiveIssueCount == 0, "replacing existing project retains resolved state");

        var first = restored.Export(exports, "seam_checked");
        VerifyExport(restored, first);
        AssertSourceHashes(originalHashes, "all original sources unchanged after editing and export");
        var second = restored.Export(exports, "seam_checked");
        Assert(first.Path != second.Path && Directory.Exists(first.Path) && Directory.Exists(second.Path),
            "repeat export creates a distinct resource and preserves existing output");
        var unchangedNames = restored.GetEffectiveAssets().Where(a => !a.Entities.Any(e => e.Excluded) &&
            !a.Collisions.Any(c => c.Excluded)).Select(a => a.Name).ToArray();
        Assert(unchangedNames.All(name => Digest(Path.Combine(first.Path, "stream", name)) ==
            Digest(Path.Combine(second.Path, "stream", name))), "repeat export preserves unchanged resource bytes");
        AssertSourceHashes(originalHashes, "repeated export leaves source bytes unchanged");
        Expect<InvalidOperationException>(() => restored.Export(packs[0], "inside_source"),
            "export refuses a destination inside imported sources");

        // A source change must fail before a committed resource becomes visible. Restore the
        // fixture afterwards so the retained project remains usable for manual inspection.
        var changedAsset = restored.GetEffectiveAssets().First(a => a.Extension == ".ytd");
        byte[] original = File.ReadAllBytes(changedAsset.Path);
        var exportSnapshot = Directory.GetDirectories(exports).OrderBy(p => p).ToArray();
        try
        {
            File.WriteAllBytes(changedAsset.Path, original.Concat(new byte[] { 0x5A }).ToArray());
            Expect<IOException>(() => restored.Export(exports, "changed_source"), "changed source digest blocks export");
            Assert(exportSnapshot.SequenceEqual(Directory.GetDirectories(exports).OrderBy(p => p)),
                "failed export commits no output and removes private staging files");
            Expect<IOException>(() => MergeWorkspace.LoadProject(project), "project load detects changed source bytes");
        }
        finally { File.WriteAllBytes(changedAsset.Path, original); }
        AssertSourceHashes(originalHashes, "digest guard check restores procedural source fixture");

        CheckXmlResources(directory, packs[0], exports);
        CheckMalformedRotation(directory, packs[0]);
        CheckLocalLodExport(directory, packs[0], exports);
        CheckCollisionPreviewTransform();
        CheckReviewWindow();
        passed += AutomaticChecks.Run(directory);
        passed += YmapMergeChecks.Run(Path.Combine(directory, "ymap-auto"));
        passed += MapRepairChecks.Run(Path.Combine(directory, "map-repair"));
        TextureEditorChecks.Run(directory, Assert);
        passed += ClothingChecks.Run(Path.Combine(directory, "clothing-pack"));
        passed += DictionaryMergeChecks.Run(Path.Combine(directory, "dictionary-auto"));
        passed += CollisionMergeChecks.Run(Path.Combine(directory, "collision-auto"));
        passed += EncryptedSkipChecks.Run(Path.Combine(directory, "encrypted-skip"));
        passed += ResourceFolderChecks.Run(Path.Combine(directory, "resource-folders"));
        AssertSourceHashes(originalHashes, "additional XML and LOD checks preserve original procedural sources");
    }

    private static void CheckReviewWindow()
    {
        var workspace = new MergeWorkspace();
        for (int p = 0; p < 2; p++)
        {
            var pack = new MapPack { Name = "dense source " + p, Color = "#7ACBB5" };
            var asset = new MapAsset { Pack = pack, Name = "dense_" + p + ".ybn", Extension = ".ybn" };
            for (int n = 0; n < 65; n++)
            {
                var bound = new BoundBox { Type = BoundsType.Box, BoxMin = -SharpDX.Vector3.One, BoxMax = SharpDX.Vector3.One };
                asset.Collisions.Add(new CollisionPiece { Asset = asset, Index = n, Bound = bound, Bounds = GeometryReader.GetCollisionBounds(bound) });
            }
            pack.Assets.Add(asset); workspace.Packs.Add(pack);
        }
        workspace.Refresh();
        Assert(workspace.ActiveIssueCount == 4000, "dense collision review presents a bounded first window");
        foreach (var issue in workspace.Issues.ToArray()) workspace.Resolve(issue, "keep-both");
        workspace.Refresh();
        Assert(workspace.ActiveIssueCount == 225, "acknowledged pairs expose remaining collisions instead of hiding them behind the review limit");
    }

    private static void CheckCollisionPreviewTransform()
    {
        var parent = new BoundComposite { Type = BoundsType.Composite,
            Transform = SharpDX.Matrix.RotationY(0.3f) * SharpDX.Matrix.Translation(-5, 7, 9) };
        var bound = new BoundGeometry { Type = BoundsType.Geometry, Parent = parent,
            CenterGeom = new SharpDX.Vector3(2, 3, 4),
            Vertices = new[] { SharpDX.Vector3.Zero, SharpDX.Vector3.UnitX, SharpDX.Vector3.UnitY },
            Transform = SharpDX.Matrix.RotationZ(0.4f) * SharpDX.Matrix.Translation(10, 20, 30),
            BoxMin = new SharpDX.Vector3(2, 3, 4), BoxMax = new SharpDX.Vector3(3, 4, 4) };
        bound.TransformInv = SharpDX.Matrix.Invert(bound.Transform);
        bound.Polygons = new BoundPolygon[] { new BoundPolygonTriangle { Owner = bound,
            vertIndex1 = 0, vertIndex2 = 1, vertIndex3 = 2 } };
        parent.Children = new ResourcePointerArray64<Bounds> { data_items = new Bounds[] { bound } };
        var pack = new MapPack { Name = "transformed triangle", Color = "#7ACBB5" };
        var asset = new MapAsset { Pack = pack, Name = "triangle.ybn", Extension = ".ybn",
            Path = "procedural-memory-only.ybn", Ybn = new YbnFile { Bounds = parent } };
        asset.Collisions.Add(new CollisionPiece { Asset = asset, Bound = bound,
            Bounds = GeometryReader.GetCollisionBounds(bound) });
        pack.Assets.Add(asset);
        var workspace = new MergeWorkspace(); workspace.Packs.Add(pack); workspace.Refresh();
        var scene = workspace.Scene.Single();
        var points = WorldPoints(scene.Model).ToArray();
        Assert(!scene.IsProxy && scene.Model.IsFrozen && points.Length == 3,
            "native collision triangle preview produces a frozen mesh");
        var transform = bound.Transform * parent.Transform;
        for (int i = 0; i < points.Length; i++)
        {
            var expected = SharpDX.Vector3.TransformCoordinate(bound.Vertices[i] + bound.CenterGeom, transform);
            Assert(Math.Abs(points[i].X - expected.X) < 0.0001 && Math.Abs(points[i].Y - expected.Y) < 0.0001 &&
                   Math.Abs(points[i].Z - expected.Z) < 0.0001,
                "collision triangle applies geometry center and child/ancestor transforms once #" + i);
        }
    }

    private static void CheckXmlResources(string directory, string pack, string exports)
    {
        string source = Path.Combine(directory, "xml-sources");
        Directory.CreateDirectory(source);
        var nativeMap = new YmapFile();
        nativeMap.Load(File.ReadAllBytes(Directory.GetFiles(pack, "*.ymap", SearchOption.AllDirectories).Single()));
        var nativeType = new YtypFile();
        nativeType.Load(File.ReadAllBytes(Directory.GetFiles(pack, "*.ytyp", SearchOption.AllDirectories).Single()));
        File.WriteAllText(Path.Combine(source, "quay_district.ymap.xml"), MetaXml.GetXml(nativeMap, out _));
        File.WriteAllText(Path.Combine(source, "seam_demo.ytyp.xml"), MetaXml.GetXml(nativeType, out _));
        var workspace = new MergeWorkspace(); workspace.Import(new[] { source });
        var map = workspace.Packs.Single().Assets.Single(a => a.Ymap != null);
        var type = workspace.Packs.Single().Assets.Single(a => a.Ytyp != null);
        Assert(workspace.Packs.Single().Assets.All(a => a.Error == null) &&
               map.Entities.Count == nativeMap.AllEntities.Length && type.Ytyp.AllArchetypes.Length == nativeType.AllArchetypes.Length,
            "canonical CodeWalker YMAP/YTYP XML imports as native resource data");
        var exported = workspace.Export(exports, "xml_checked");
        var roundMap = new YmapFile(); roundMap.Load(File.ReadAllBytes(Path.Combine(exported.Path, "stream", map.Name)));
        var roundType = new YtypFile(); roundType.Load(File.ReadAllBytes(Path.Combine(exported.Path, "stream", type.Name)));
        Assert(roundMap.AllEntities.Length == nativeMap.AllEntities.Length &&
               roundType.AllArchetypes.Length == nativeType.AllArchetypes.Length,
            "XML inputs export to reloadable binary YMAP/YTYP files");
        Assert(!Directory.GetFiles(Path.Combine(exported.Path, "stream"), "*.xml").Any(),
            "export streams compiled resources rather than XML source");
    }

    private static void CheckMalformedRotation(string directory, string pack)
    {
        string path = Path.Combine(directory, "invalid-sources", "bad_rotation.ymap");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string valid = Directory.GetFiles(pack, "*.ymap", SearchOption.AllDirectories).Single();
        var map = new YmapFile(); map.Load(File.ReadAllBytes(valid));
        var definition = map.AllEntities.Last()._CEntityDef;
        definition.rotation = new SharpDX.Vector4(float.NaN, 0, 0, 1);
        map.AllEntities.Last()._CEntityDef = definition;
        map.SetName("bad_rotation");
        File.WriteAllBytes(path, map.Save());
        var workspace = new MergeWorkspace(); workspace.Import(new[] { path });
        var failed = workspace.Packs.Single().Assets.Single();
        Assert(failed.Error != null && failed.Ymap == null && failed.Entities.Count == 0 && workspace.Scene.Count == 0,
            "nonfinite rotation becomes a clean unreadable asset without partial geometry");
        Assert(workspace.Issues.Any(i => i.Kind == "Unreadable file"), "invalid rotation appears as a review issue");
        workspace.Exclude(failed.Id);
        workspace.Import(new[] { valid });
        Assert(workspace.Packs.SelectMany(p => p.Assets).Any(a => a.Ymap != null) && workspace.Scene.Count > 0,
            "workspace remains usable after excluding malformed input");
    }

    private static void CheckLocalLodExport(string directory, string pack, string exports)
    {
        string path = Path.Combine(directory, "lod-sources", "local_hierarchy.ymap");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var original = new YmapFile();
        original.Load(File.ReadAllBytes(Directory.GetFiles(pack, "*.ymap", SearchOption.AllDirectories).Single()));
        for (int i = 0; i < original.AllEntities.Length; i++)
        {
            var def = original.AllEntities[i]._CEntityDef;
            def.parentIndex = i == 2 || i == 3 ? 1 : -1;
            def.numChildren = i == 1 ? 2u : 0u;
            def.lodLevel = i == 1 ? rage__eLodType.LODTYPES_DEPTH_LOD : rage__eLodType.LODTYPES_DEPTH_HD;
            original.AllEntities[i]._CEntityDef = def;
        }
        original.SetName("local_hierarchy");
        original.CalcFlags();
        File.WriteAllBytes(path, original.Save());
        var workspace = new MergeWorkspace(); workspace.Import(new[] { path });
        var asset = workspace.Packs.Single().Assets.Single();
        Assert(asset.Error == null && asset.Ymap.AllEntities[2].Parent == asset.Ymap.AllEntities[1] &&
               asset.Ymap.AllEntities[3].Parent == asset.Ymap.AllEntities[1], "synthetic native YMAP contains a genuine local LOD hierarchy");
        var before = asset.Entities.Select(e => e.Entity._CEntityDef).ToArray();
        workspace.Exclude(asset.Entities[0].Id); // Shift the old parent index from 1 to 0.
        workspace.Exclude(asset.Entities[2].Id); // Remove one of that parent's two children.
        workspace.Refresh();
        Assert(workspace.ActiveIssueCount == 0, "valid local LOD leaf exclusions need no unrelated conflict decisions");
        var result = workspace.Export(exports, "lod_checked");
        var parsed = new YmapFile(); parsed.Load(File.ReadAllBytes(Path.Combine(result.Path, "stream", asset.Name)));
        Assert(parsed.AllEntities.Length == before.Length - 2 && parsed.AllEntities[1]._CEntityDef.parentIndex == 0 &&
               parsed.AllEntities[0]._CEntityDef.numChildren == 1 && parsed.AllEntities[1].Parent == parsed.AllEntities[0],
            "LOD export remaps parent indices and updates parent child count");
        var retained = new[] { 1, 3, 4 };
        for (int i = 0; i < retained.Length; i++)
        {
            var expected = before[retained[i]];
            expected.parentIndex = i == 1 ? 0 : -1;
            expected.numChildren = i == 0 ? 1u : 0u;
            var actual = parsed.AllEntities[i]._CEntityDef;
            expected.extensions = new Array_StructurePointer(); actual.extensions = new Array_StructurePointer();
            Assert(expected.Equals(actual), "local LOD round trip preserves other entity metadata #" + i);
        }
        Assert(before.SequenceEqual(asset.Entities.Select(e => e.Entity._CEntityDef)),
            "export remapping leaves open workspace entity indices and definitions unchanged");
    }

    private static void CheckLodSafety(MergeWorkspace workspace)
    {
        var issue = workspace.Issues.First(i => i.Kind == "Duplicate placement" && i.CanAutoFix);
        var left = Entities(workspace).Single(e => e.Id == issue.LeftId);
        var right = Entities(workspace).Single(e => e.Id == issue.RightId);
        var leftOriginal = left.Entity._CEntityDef;
        var rightOriginal = right.Entity._CEntityDef;
        try
        {
            // The same numeric parent index can name different parents in two maps.
            var a = leftOriginal; a.parentIndex = 0; left.Entity._CEntityDef = a;
            var b = rightOriginal; b.parentIndex = 0; right.Entity._CEntityDef = b;
            workspace.Refresh();
            var linked = workspace.Issues.Single(i => i.Kind == "Duplicate placement" &&
                SamePair(i, left.Id, right.Id));
            Assert(!linked.CanAutoFix, "matching local LOD leaves cannot be removed automatically");
        }
        finally
        {
            left.Entity._CEntityDef = leftOriginal;
            right.Entity._CEntityDef = rightOriginal;
            workspace.Refresh();
        }
    }

    private static void CheckPlanarCollisions(MergeWorkspace workspace)
    {
        var a = workspace.Packs[0].Assets.SelectMany(asset => asset.Collisions).First();
        var b = workspace.Packs[1].Assets.SelectMany(asset => asset.Collisions).First();
        Box3 originalA = a.Bounds, originalB = b.Bounds;
        try
        {
            a.Bounds = Box(0, 0, 0, 3, 3, 0);
            b.Bounds = Box(1, 1, 0, 4, 4, 0);
            workspace.Refresh();
            Assert(workspace.Issues.Any(i => i.Kind == "Collision bounds overlap" && SamePair(i, a.Id, b.Id)),
                "coplanar floor collision bounds remain review candidates");
            a.Bounds = Box(0, 0, 0, 0, 3, 3);
            b.Bounds = Box(0, 1, 1, 0, 4, 4);
            workspace.Refresh();
            Assert(workspace.Issues.Any(i => i.Kind == "Collision bounds overlap" && SamePair(i, a.Id, b.Id)),
                "zero-width wall bounds survive sweep filtering");
            b.Bounds = Box(100, 100, 100, 101, 101, 101);
            workspace.Refresh();
            Assert(!workspace.Issues.Any(i => i.Kind == "Collision bounds overlap" && SamePair(i, a.Id, b.Id)),
                "separated collision bounds create no candidate");
        }
        finally { a.Bounds = originalA; b.Bounds = originalB; workspace.Refresh(); }
    }

    private static void ResolveRemaining(MergeWorkspace workspace)
    {
        for (int guard = 0; workspace.ActiveIssueCount > 0 && guard < 500; guard++)
        {
            var issue = workspace.Issues.First(i => !i.Resolved);
            if (issue.Kind == "Unreadable file") throw new Exception(issue.Title + ": " + issue.Detail);
            string choice = issue.Kind == "Collision bounds overlap" || issue.Kind == "Shared filename" ||
                issue.CanAutoFix ? "keep-left" : "keep-both";
            workspace.Resolve(issue, choice);
            workspace.Refresh();
        }
        Assert(workspace.ActiveIssueCount == 0, "all procedural conflict choices terminate");
    }

    private static void VerifyExport(MergeWorkspace workspace, ExportResult result)
    {
        Assert(File.Exists(Path.Combine(result.Path, "fxmanifest.lua")) &&
               File.Exists(Path.Combine(result.Path, "seam-merge-report.json")), "resource includes manifest and merge report");
        int mapCount = 0, boundCount = 0, unchanged = 0;
        foreach (var asset in workspace.GetEffectiveAssets())
        {
            string exported = Path.Combine(result.Path, "stream", asset.Name);
            if (asset.Ybn != null && asset.Collisions.All(c => c.Excluded))
            {
                Assert(!File.Exists(exported), "fully excluded collision resource omitted");
                continue;
            }
            Assert(File.Exists(exported), "effective asset exported: " + asset.Name);
            if (asset.Ymap != null)
            {
                var parsed = new YmapFile(); parsed.Load(File.ReadAllBytes(exported));
                var expected = asset.Entities.Where(e => !e.Excluded).OrderBy(e => e.Index).ToArray();
                Assert(parsed.AllEntities.Length == expected.Length, "YMAP round trip retains expected placements: " + asset.Name);
                for (int i = 0; i < expected.Length; i++)
                {
                    var before = expected[i].Entity._CEntityDef;
                    var after = parsed.AllEntities[i]._CEntityDef;
                    before.extensions = new Array_StructurePointer(); after.extensions = new Array_StructurePointer();
                    Assert(before.Equals(after), "YMAP round trip preserves entity metadata: " + asset.Name + " #" + i);
                }
                Assert(parsed.CMapData.name.Hash == asset.Ymap.CMapData.name.Hash &&
                       parsed.CMapData.parent.Hash == asset.Ymap.CMapData.parent.Hash,
                    "YMAP names and external parent preserved: " + asset.Name);
                mapCount++;
            }
            if (asset.Ybn != null)
            {
                var parsed = new YbnFile(); parsed.Load(File.ReadAllBytes(exported));
                var expected = asset.Collisions.Where(c => !c.Excluded).OrderBy(c => c.Index).ToArray();
                var actual = Leaves(parsed.Bounds).ToArray();
                Assert(actual.Length == expected.Length, "YBN round trip retains expected components: " + asset.Name);
                for (int i = 0; i < actual.Length; i++)
                    Assert(actual[i].Type == expected[i].Bound.Type &&
                           Near(GeometryReader.GetCollisionBounds(actual[i]), expected[i].Bounds),
                        "YBN round trip preserves sibling transforms: " + asset.Name + " #" + i);
                boundCount++;
            }
            bool edited = asset.Entities.Any(e => e.Excluded) || asset.Collisions.Any(c => c.Excluded);
            if (!edited && !asset.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                Assert(Digest(exported) == Digest(asset.Path), "unchanged asset copied byte for byte: " + asset.Name);
                unchanged++;
            }
        }
        Assert(mapCount == 2 && boundCount > 0 && unchanged > 0, "native round trips cover both maps, collision, and unchanged assets");
        Assert(result.RemovedEntities > 0 && result.RemovedBounds > 0, "export reports removed placements and collision components");
    }

    private static IEnumerable<GeometryModel3D> Surfaces(Model3D model)
    {
        if (model is GeometryModel3D geometry) yield return geometry;
        if (model is Model3DGroup group)
            foreach (var child in group.Children)
                foreach (var surface in Surfaces(child)) yield return surface;
    }
    private static IEnumerable<Point3D> WorldPoints(Model3D model)
    {
        if (model is GeometryModel3D surface && surface.Geometry is MeshGeometry3D mesh)
            foreach (var point in mesh.Positions) yield return model.Transform.Transform(point);
        if (model is Model3DGroup group)
            foreach (var child in group.Children)
                foreach (var point in WorldPoints(child)) yield return group.Transform.Transform(point);
    }
    private static IEnumerable<Bounds> Leaves(Bounds bound)
    {
        if (bound is BoundComposite composite && composite.Children?.data_items != null)
        {
            foreach (var child in composite.Children.data_items)
                if (child != null) foreach (var leaf in Leaves(child)) yield return leaf;
        }
        else if (bound != null) yield return bound;
    }
    private static IEnumerable<MapEntity> Entities(MergeWorkspace workspace) => workspace.Packs.SelectMany(p => p.Assets).SelectMany(a => a.Entities);
    private static bool SamePair(MergeIssue issue, string a, string b) =>
        issue.LeftId == a && issue.RightId == b || issue.LeftId == b && issue.RightId == a;
    private static Box3 Box(double x, double y, double z, double maxX, double maxY, double maxZ) =>
        new Box3 { MinX = x, MinY = y, MinZ = z, MaxX = maxX, MaxY = maxY, MaxZ = maxZ };
    private static bool Near(Box3 a, Box3 b) => Math.Abs(a.MinX - b.MinX) < 0.002 &&
        Math.Abs(a.MinY - b.MinY) < 0.002 && Math.Abs(a.MinZ - b.MinZ) < 0.002 &&
        Math.Abs(a.MaxX - b.MaxX) < 0.002 && Math.Abs(a.MaxY - b.MaxY) < 0.002 && Math.Abs(a.MaxZ - b.MaxZ) < 0.002;
    private static void Relocate(string pack, string name, string subdirectory)
    {
        string nested = Path.Combine(pack, "stream", "nested", subdirectory);
        Directory.CreateDirectory(nested);
        File.Move(Path.Combine(pack, "stream", name), Path.Combine(nested, name));
    }
    private static Dictionary<string, string> SourceHashes(IEnumerable<string> roots) => roots
        .SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories)).ToDictionary(p => p, Digest);
    private static void AssertSourceHashes(Dictionary<string, string> expected, string label) =>
        Assert(expected.All(pair => File.Exists(pair.Key) && Digest(pair.Key) == pair.Value), label);
    private static string Digest(string path)
    {
        using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream));
    }
    private static string State(MergeWorkspace workspace) => string.Join("\n", workspace.Packs.Select(p =>
        p.Root + "|" + p.Name + "|" + p.Enabled + "|" + p.Color + "\n" + string.Join("\n", p.Assets.OrderBy(a => a.Path).Select(a =>
            a.Path + "|" + a.Excluded + "|" + string.Join(",", a.Entities.Where(e => e.Excluded).Select(e => e.Index)) +
            "|" + string.Join(",", a.Collisions.Where(c => c.Excluded).Select(c => c.Index))))));
    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "app", "Seam.csproj"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Run the checks from a build inside the Seam repository.");
    }
    private static void Expect<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { Assert(true, label); return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + label);
    }
    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        passed++;
        Console.WriteLine("  OK " + label);
    }
}
