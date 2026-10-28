using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;
using SharpDX;

namespace Seam
{
    /// <summary>Merges collision resources without discarding a file because its box overlaps another.</summary>
    public static class CollisionMerger
    {
        public static List<AutomaticMergeOutput> Merge(IReadOnlyList<MapAsset> assets, AutomaticMergePlan plan, Action<string> progress = null)
        {
            var outputs = new List<AutomaticMergeOutput>();
            foreach (var group in assets.Where(a => a.Extension == ".ybn").GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var sources = group.ToList();
                progress?.Invoke("Merging collision · " + group.Key);
                var documents = sources.Select(Read).ToList();
                var distinct = documents.GroupBy(d => Digest(d.Bytes)).Select(g => g.First()).ToList();
                int identical = documents.Count - distinct.Count;
                plan.DuplicateFiles += identical;
                if (sources.Count > 1) plan.MergedCollisions++;
                if (distinct.Count == 1)
                {
                    outputs.Add(Output(group.Key, distinct[0].Bytes, sources, sources.Count > 1 ? "Identical collision resources consolidated" : "Original collision retained"));
                    if (identical > 0) Log(plan, "Collision merged", group.Key, identical + " identical collision copies consolidated under the original streaming name.", identical);
                    continue;
                }

                var surfaces = distinct.Select(d => Surfaces(d.File.Bounds)).ToArray();
                var indexes = surfaces.Select(s => new SurfaceIndex(s)).ToArray();
                var common = surfaces[0].Where(s => indexes.Skip(1).All(index => index.Match(s) != null)).ToArray();
                bool geometryOnly = distinct.All(d => Leaves(d.File.Bounds).All(b => b is BoundGeometry g && g.Polygons != null));
                bool tile = IsTileName(group.Key);
                bool replacement = tile && geometryOnly && surfaces.All(s => s.Length > 0) &&
                    common.Length >= surfaces.Min(s => s.Length) * 0.999;
                byte[] bytes;
                string action;
                if (replacement)
                {
                    // These are alternative edited copies of one GTA collision tile. A union
                    // would restore walls removed by either map. Keep their common surfaces.
                    int selected = Enumerable.Range(0, surfaces.Length).OrderBy(i => surfaces[i].Length).First();
                    int removed = surfaces.Max(s => s.Length) - common.Length;
                    int triangleRemoved = surfaces[0].Count(s => s.Polygon is BoundPolygonTriangle) - common.Count(s => s.Polygon is BoundPolygonTriangle);
                    plan.RemovedTriangles += Math.Max(0, triangleRemoved);
                    plan.RemovedPolygons += removed;
                    bool subset = surfaces[selected].Length == common.Length && surfaces[selected].All(s => indexes.All(index => index.Match(s) is Surface match && match.Metadata == s.Metadata));
                    if (subset) bytes = distinct[selected].Bytes;
                    else if (common.Length == surfaces[0].Length) bytes = distinct[0].Bytes;
                    else
                    {
                        var keep = new HashSet<BoundPolygon>(common.Select(s => s.Polygon));
                        var file = distinct[0].File;
                        file.Bounds = Prepare(file.Bounds);
                        Filter(file.Bounds, (g, p) => keep.Contains(p));
                        Rebuild(file.Bounds);
                        bytes = SaveChecked(file, group.Key);
                    }
                    action = "Base-game collision removal edits combined";
                    Log(plan, "Collision patch merged", group.Key,
                        "Recognized GTA tile variants share at least 99.9% of their collision surfaces, matched within encoded vertex quantization. Combined removal edits retain " + common.Length.ToString("N0") +
                        " common polygons (triangles and collision primitives) and remove " + removed.ToString("N0") + " polygons present in another variant. Where a common surface has differing materials or collision flags, map priority wins. This patch heuristic has no pristine base-game baseline; additions under the same tile name can be removed.", removed);
                }
                else if (tile)
                {
                    bytes = distinct[0].Bytes;
                    action = "Base-game tile resolved by map priority";
                    Log(plan, "Collision priority fallback", group.Key,
                        "The replacement tile variants could not be matched at the 99.9% threshold. Preserved the highest-priority complete tile to avoid restoring removed walls or cutting unrelated surfaces. Other independently named collision files remain included.", 1);
                }
                else
                {
                    var target = distinct[0].File;
                    target.Bounds = Prepare(target.Bounds);
                    var root = target.Bounds as BoundComposite;
                    if (root == null)
                    {
                        root = new BoundComposite { Type = BoundsType.Composite, FileVFT = 1080212136, Children = new ResourcePointerArray64<Bounds> { data_items = new[] { target.Bounds } } };
                        target.Bounds.Parent = root;
                        target.Bounds = root;
                    }
                    var seen = PolygonSet(root);
                    var seenPrimitives = new HashSet<string>(Leaves(root).Where(b => !(b is BoundGeometry)).Select(PrimitiveKey), StringComparer.Ordinal);
                    int removed = 0, removedTriangles = 0, droppedBounds = 0;
                    foreach (var document in distinct.Skip(1))
                    {
                        var incoming = Prepare(document.File.Bounds);
                        var additions = incoming is BoundComposite compound && compound.Transform == Matrix.Identity
                            ? (compound.Children?.data_items ?? new Bounds[0]).Where(b => b != null).ToArray()
                            : new[] { incoming };
                        foreach (var addition in additions)
                        {
                            int beforeLeaves = Leaves(addition).Count();
                            int beforeTriangles = Leaves(addition).OfType<BoundGeometry>().Sum(g => (g.Polygons ?? new BoundPolygon[0]).Count(p => p is BoundPolygonTriangle));
                            int count = Filter(addition, (g, p) => seen.Add(PolygonKey(g, p)));
                            removed += count;
                            removedTriangles += beforeTriangles - Leaves(addition).OfType<BoundGeometry>().Sum(g => (g.Polygons ?? new BoundPolygon[0]).Count(p => p is BoundPolygonTriangle));
                            bool retain = RetainPrimitives(addition, seenPrimitives) && !IsEmpty(addition);
                            droppedBounds += beforeLeaves - (retain ? Leaves(addition).Count() : 0);
                            if (!retain) continue;
                            // Detach only the source root. Nested transforms and child filter flags remain intact.
                            addition.Parent = root;
                            var children = (root.Children?.data_items ?? new Bounds[0]).ToList();
                            children.Add(addition);
                            if (children.Count > ushort.MaxValue) throw new InvalidDataException(group.Key + " exceeds the YBN composite child limit.");
                            root.Children = new ResourcePointerArray64<Bounds> { data_items = children.ToArray() };
                        }
                    }
                    Rebuild(root);
                    bytes = SaveChecked(target, group.Key);
                    plan.RemovedTriangles += removedTriangles;
                    plan.RemovedPolygons += removed;
                    plan.DuplicateBounds += droppedBounds;
                    action = "Collision graphs combined";
                    Log(plan, "Collision merged", group.Key,
                        distinct.Count + " distinct collision graphs combined under the original name; " + removed.ToString("N0") + " exact duplicate polygons and " + droppedBounds.ToString("N0") + " duplicate components removed. Independent surfaces, materials and transformed components retained.", removed);
                }
                outputs.Add(Output(group.Key, bytes, sources, action));
            }
            return outputs;
        }

        private static AutomaticMergeOutput Output(string name, byte[] bytes, List<MapAsset> sources, string action) =>
            new AutomaticMergeOutput { Name = name, Data = bytes, Sources = sources, Action = action };
        private static void Log(AutomaticMergePlan plan, string kind, string title, string detail, int count) =>
            plan.Operations.Add(new MergeOperation { Kind = kind, Title = title, Detail = detail, Count = count });
        private sealed class Document { public byte[] Bytes; public YbnFile File; }
        private static Document Read(MapAsset source)
        {
            if (source.Error != null) throw new InvalidDataException(source.Name + ": " + source.Error);
            var bytes = MergeWorkspace.ReadResourceBytes(source.Path, ".ybn");
            var file = new YbnFile(); file.Load(bytes); file.Name = source.Name;
            if (file.Bounds == null) throw new InvalidDataException("Unreadable collision: " + source.Name);
            return new Document { Bytes = bytes, File = file };
        }
        private static string Digest(byte[] bytes)
        { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes)); }
        private static bool IsTileName(string name) => Regex.IsMatch(Path.GetFileNameWithoutExtension(name), @"^(?:(?:hi@|hi)|hei_)?[a-z]{2,5}\d{1,2}_\d+_\d+$", RegexOptions.IgnoreCase);
        private static IEnumerable<Bounds> Leaves(Bounds bound)
        {
            if (bound is BoundComposite composite)
            { foreach (var child in composite.Children?.data_items ?? new Bounds[0]) if (child != null) foreach (var leaf in Leaves(child)) yield return leaf; }
            else if (bound != null) yield return bound;
        }
        private static Matrix World(Bounds bound)
        { var result = bound.Transform; for (var parent = bound.Parent; parent != null; parent = parent.Parent) result *= parent.Transform; return result; }
        private static string Number(float value) => (value == 0 ? 0f : value).ToString("R", CultureInfo.InvariantCulture);
        private static string Vector(Vector3 value) => Number(value.X) + "," + Number(value.Y) + "," + Number(value.Z);
        private static string Context(Bounds bound)
        {
            var result = new StringBuilder();
            for (var current = bound; current != null; current = current.Parent)
            {
                var a = current.CompositeFlags1; var b = current.CompositeFlags2;
                if ((uint)a.Flags1 != 0 || (uint)a.Flags2 != 0 || (uint)b.Flags1 != 0 || (uint)b.Flags2 != 0)
                    result.Append((uint)a.Flags1).Append('/').Append((uint)a.Flags2).Append('/').Append((uint)b.Flags1).Append('/').Append((uint)b.Flags2).Append(';');
            }
            return result.Append('|').Append(bound.PolyFlags).Append('/').Append(bound.UnkFlags).Append('/').Append(bound.RoomId_and_PedDensity)
                .Append('/').Append(bound.ProceduralId).Append('/').Append(Number(bound.Margin)).ToString();
        }
        private static string VertexKey(BoundGeometry geometry, int index, bool flag, Matrix world)
        {
            var point = Vector3.TransformCoordinate(geometry.GetVertex(index) + geometry.CenterGeom, world);
            var colors = geometry.VertexColours;
            var color = colors != null && index < colors.Length ? colors[index].ToString() : "";
            return Vector(point) + "/" + (flag ? "1" : "0") + "/" + color;
        }
        private static string TriangleKey(BoundGeometry geometry, BoundPolygonTriangle triangle)
        {
            var world = World(geometry);
            var vertices = new[] { VertexKey(geometry, triangle.vertIndex1, triangle.vertFlag1, world), VertexKey(geometry, triangle.vertIndex2, triangle.vertFlag2, world), VertexKey(geometry, triangle.vertIndex3, triangle.vertFlag3, world) };
            // Cyclic rotations describe the same face; reversed winding does not.
            int start = string.CompareOrdinal(vertices[0], vertices[1]) <= 0 ? 0 : 1;
            if (string.CompareOrdinal(vertices[2], vertices[start]) < 0) start = 2;
            var material = triangle.Material;
            return Context(geometry) + "|" + material.Data1 + ":" + material.Data2 + "|" + vertices[start] + ";" + vertices[(start + 1) % 3] + ";" + vertices[(start + 2) % 3];
        }
        private static HashSet<string> PolygonSet(Bounds bounds) => new HashSet<string>(Leaves(bounds).OfType<BoundGeometry>()
            .SelectMany(g => (g.Polygons ?? new BoundPolygon[0]).Select(p => PolygonKey(g, p))), StringComparer.Ordinal);
        private static string PolygonKey(BoundGeometry geometry, BoundPolygon polygon)
        {
            if (polygon is BoundPolygonTriangle triangle) return TriangleKey(geometry, triangle);
            var world = World(geometry);
            var points = polygon.VertexIndices.Select(i => Vector3.TransformCoordinate(geometry.GetVertex(i) + geometry.CenterGeom, world)).ToArray();
            if (polygon is BoundPolygonBox && points.Length == 4) points = BoxCorners(points);
            var material = polygon.Material;
            var properties = polygon.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.PropertyType.IsPrimitive && p.Name.IndexOf("Index", StringComparison.OrdinalIgnoreCase) < 0)
                .OrderBy(p => p.Name).Select(p => p.Name + "=" + Convert.ToString(p.GetValue(polygon, null), CultureInfo.InvariantCulture));
            return Context(geometry) + "|" + polygon.Type + "|" + material.Data1 + ":" + material.Data2 + "|" + string.Join(";", properties) +
                "|" + string.Join(";", points.Select(Vector).OrderBy(p => p, StringComparer.Ordinal)) + "|" + Linear(world);
        }
        private static string Linear(Matrix m) => Number(m.M11) + "," + Number(m.M12) + "," + Number(m.M13) + "," + Number(m.M21) + "," + Number(m.M22) + "," + Number(m.M23) + "," + Number(m.M31) + "," + Number(m.M32) + "," + Number(m.M33);
        private static string PrimitiveKey(Bounds bound) => Digest(new YbnFile { Bounds = bound }.Save()) + "|" + Context(bound) + "|" + string.Join(",", World(bound).ToArray().Select(Number));
        private static bool RetainPrimitives(Bounds bound, HashSet<string> seen)
        {
            if (bound is BoundComposite composite)
            {
                var retained = (composite.Children?.data_items ?? new Bounds[0]).Where(b => b != null && RetainPrimitives(b, seen) && !IsEmpty(b)).ToArray();
                composite.Children = new ResourcePointerArray64<Bounds> { data_items = retained };
                return retained.Length > 0;
            }
            return bound is BoundGeometry || seen.Add(PrimitiveKey(bound));
        }
        private static Vector3[] BoxCorners(Vector3[] points)
        {
            var p = points[0]; var a = ((points[2] + points[3]) - (points[0] + points[1])) * 0.5f;
            var b = points[2] - (p + a); var c = points[3] - (p + a);
            return Enumerable.Range(0, 8).Select(i => p + ((i & 1) != 0 ? a : Vector3.Zero) + ((i & 2) != 0 ? b : Vector3.Zero) + ((i & 4) != 0 ? c : Vector3.Zero)).ToArray();
        }

        private sealed class Surface
        {
            public BoundPolygon Polygon;
            public Vector3[] Vertices;
            public Vector3 Center, Quantum;
            public float Radius;
            public string Metadata;
        }
        private static Surface[] Surfaces(Bounds root)
        {
            return Leaves(root).OfType<BoundGeometry>().SelectMany(geometry =>
            {
                var world = World(geometry);
                var scale = new Vector3(new Vector3(world.M11, world.M12, world.M13).Length(), new Vector3(world.M21, world.M22, world.M23).Length(), new Vector3(world.M31, world.M32, world.M33).Length());
                return (geometry.Polygons ?? new BoundPolygon[0]).Select(polygon =>
                {
                    var vertices = polygon.VertexIndices.Select(i => Vector3.TransformCoordinate(geometry.GetVertex(i) + geometry.CenterGeom, world)).ToArray();
                    if (polygon is BoundPolygonBox && vertices.Length == 4)
                    {
                        // A box stores four alternating corners. Editors may choose another
                        // equivalent quartet, so compare the eight physical corners instead.
                        vertices = BoxCorners(vertices);
                    }
                    float radius = polygon is BoundPolygonSphere sphere ? sphere.sphereRadius : polygon is BoundPolygonCapsule capsule ? capsule.capsuleRadius : polygon is BoundPolygonCylinder cylinder ? cylinder.cylinderRadius : 0;
                    var material = polygon.Material;
                    var tags = polygon is BoundPolygonTriangle triangle ? (triangle.vertFlag1 ? "1" : "0") + (triangle.vertFlag2 ? "1" : "0") + (triangle.vertFlag3 ? "1" : "0") : "";
                    return new Surface { Polygon = polygon, Vertices = vertices, Center = vertices.Aggregate(Vector3.Zero, (a, b) => a + b) / Math.Max(1, vertices.Length), Quantum = geometry.Quantum * scale,
                        Radius = radius, Metadata = Context(geometry) + "|" + material.Data1 + ":" + material.Data2 + "|" + tags + (radius > 0 ? "|" + Vector(scale) : "") };
                });
            }).ToArray();
        }
        private sealed class SurfaceIndex
        {
            private readonly Dictionary<string, List<Surface>> cells = new Dictionary<string, List<Surface>>();
            private const double CellSize = 0.25;
            public SurfaceIndex(IEnumerable<Surface> surfaces)
            {
                foreach (var surface in surfaces)
                {
                    var key = Cell(surface.Polygon.Type, surface.Center, 0, 0, 0);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<Surface>();
                    list.Add(surface);
                }
            }
            public Surface Match(Surface surface)
            {
                for (int x = -1; x <= 1; x++) for (int y = -1; y <= 1; y++) for (int z = -1; z <= 1; z++)
                    if (cells.TryGetValue(Cell(surface.Polygon.Type, surface.Center, x, y, z), out var list))
                        foreach (var candidate in list) if (SameSurface(surface, candidate)) return candidate;
                return null;
            }
            private static string Cell(BoundPolygonType type, Vector3 center, int x, int y, int z) => (int)type + ":" + ((long)Math.Floor(center.X / CellSize) + x) + ":" + ((long)Math.Floor(center.Y / CellSize) + y) + ":" + ((long)Math.Floor(center.Z / CellSize) + z);
            private static bool SameSurface(Surface a, Surface b)
            {
                if (a.Vertices.Length != b.Vertices.Length || Math.Abs(a.Radius - b.Radius) > 0.00001f) return false;
                var quantum = (Vector3.Abs(a.Quantum) + Vector3.Abs(b.Quantum)) * 1.1f;
                if (a.Polygon is BoundPolygonBox)
                {
                    // The eight inferred corners combine several encoded vertices and an
                    // editor can orthogonalize their basis. Propagate that quantization
                    // across axes; per-axis raw-vertex error would create false box holes.
                    float uncertainty = Math.Max(quantum.X, Math.Max(quantum.Y, quantum.Z)) * (2f / 1.1f);
                    quantum = new Vector3(uncertainty);
                }
                var tolerance = Vector3.Min(new Vector3(0.15f), Vector3.Max(new Vector3(0.0003f), quantum));
                bool Near(Vector3 x, Vector3 y) { var delta = Vector3.Abs(x - y); return delta.X <= tolerance.X && delta.Y <= tolerance.Y && delta.Z <= tolerance.Z; }
                if (a.Polygon is BoundPolygonTriangle)
                {
                    for (int start = 0; start < 3; start++) if (Near(a.Vertices[0], b.Vertices[start]) && Near(a.Vertices[1], b.Vertices[(start + 1) % 3]) && Near(a.Vertices[2], b.Vertices[(start + 2) % 3])) return true;
                    return false;
                }
                var matched = Enumerable.Repeat(-1, b.Vertices.Length).ToArray();
                bool Assign(int vertex, bool[] visited)
                {
                    for (int i = 0; i < b.Vertices.Length; i++)
                    {
                        if (visited[i] || !Near(a.Vertices[vertex], b.Vertices[i])) continue;
                        visited[i] = true;
                        if (matched[i] < 0 || Assign(matched[i], visited)) { matched[i] = vertex; return true; }
                    }
                    return false;
                }
                for (int i = 0; i < a.Vertices.Length; i++) if (!Assign(i, new bool[b.Vertices.Length])) return false;
                return true;
            }
        }

        private static int Filter(Bounds bound, Func<BoundGeometry, BoundPolygon, bool> keep)
        {
            int removed = 0;
            if (bound is BoundComposite composite)
            {
                var children = new List<Bounds>();
                foreach (var child in composite.Children?.data_items ?? new Bounds[0])
                { if (child == null) continue; removed += Filter(child, keep); if (!IsEmpty(child)) children.Add(child); }
                composite.Children = new ResourcePointerArray64<Bounds> { data_items = children.ToArray() };
                return removed;
            }
            if (!(bound is BoundGeometry geometry) || geometry.Polygons == null) return 0;
            var original = geometry.Polygons;
            var selected = original.Where(p => keep(geometry, p)).ToArray();
            if (selected.Length == original.Length) return 0;
            removed = original.Length - selected.Length;
            foreach (var polygon in selected) polygon.MaterialCustom = polygon.Material;
            geometry.Polygons = selected;
            if (selected.Length == 0) { geometry.PolygonMaterialIndices = new byte[0]; return removed; }
            // Compact and recenter retained vertices so the regenerated quantization covers
            // the edited geometry without clipping coordinates against the old center.
            var used = selected.SelectMany(p => p.VertexIndices).Distinct().OrderBy(i => i).ToArray();
            var minimum = used.Select(i => geometry.Vertices[i]).Aggregate(Vector3.Min);
            var maximum = used.Select(i => geometry.Vertices[i]).Aggregate(Vector3.Max);
            var shift = (minimum + maximum) * 0.5f;
            var mapping = used.Select((old, index) => new { old, index }).ToDictionary(v => v.old, v => v.index);
            var oldVertices = geometry.Vertices;
            geometry.Vertices = used.Select(i => oldVertices[i] - shift).ToArray();
            if (geometry.VerticesShrunk != null) { var old = geometry.VerticesShrunk; geometry.VerticesShrunk = used.Select(i => old[i] - shift).ToArray(); }
            if (geometry.VertexColours != null) { var old = geometry.VertexColours; geometry.VertexColours = used.Select(i => old[i]).ToArray(); }
            geometry.CenterGeom += shift;
            foreach (var polygon in selected) polygon.VertexIndices = polygon.VertexIndices.Select(i => mapping[i]).ToArray();
            geometry.BuildMaterials(); geometry.UpdateEdgeIndices();
            return removed;
        }
        private static bool IsEmpty(Bounds bound) => bound is BoundComposite c ? !(c.Children?.data_items?.Any(b => b != null && !IsEmpty(b)) ?? false) : bound is BoundGeometry g && (g.Polygons?.Length ?? 0) == 0;

        private static Bounds Prepare(Bounds bound)
        {
            if (bound is BoundComposite composite)
            {
                var children = composite.Children?.data_items;
                if (children != null) for (int i = 0; i < children.Length; i++) if (children[i] != null) { children[i] = Prepare(children[i]); children[i].Parent = composite; }
            }
            else if (bound is BoundBVH && !(bound is LocalBoundBVH))
            {
                var replacement = new LocalBoundBVH();
                // Copy stored state from the private loaded clone, including opaque fields.
                // Property copying would invoke convenience transform setters and lose bits.
                for (var type = typeof(BoundBVH); type != typeof(object); type = type.BaseType)
                    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) field.SetValue(replacement, field.GetValue(bound));
                if (replacement.Polygons != null) foreach (var polygon in replacement.Polygons) polygon.Owner = replacement;
                bound = replacement;
            }
            return bound;
        }
        private sealed class LocalBoundBVH : BoundBVH
        {
            public override IResourceBlock[] GetReferences()
            {
                var transform = Transform; var inverse = TransformInv;
                try { Transform = Matrix.Identity; TransformInv = Matrix.Identity; return base.GetReferences(); }
                finally { Transform = transform; TransformInv = inverse; }
            }
        }
        private static void Rebuild(Bounds bound)
        {
            if (bound is BoundComposite composite)
            {
                var children = (composite.Children?.data_items ?? new Bounds[0]).Where(b => b != null).ToArray();
                if (children.Length > ushort.MaxValue) throw new InvalidDataException("YBN composite exceeds 65,535 children.");
                foreach (var child in children) { child.Parent = composite; Rebuild(child); }
                composite.Children = new ResourcePointerArray64<Bounds> { data_items = children };
                if (children.Length > 0)
                {
                    var boxes = children.Select(c => TransformedBox(c.BoxMin, c.BoxMax, c.Transform)).ToArray();
                    composite.BoxMin = boxes.Select(b => b.Minimum).Aggregate(Vector3.Min); composite.BoxMax = boxes.Select(b => b.Maximum).Aggregate(Vector3.Max);
                    composite.BoxCenter = (composite.BoxMin + composite.BoxMax) * 0.5f;
                    composite.SphereCenter = composite.BoxCenter; composite.SphereRadius = (composite.BoxMax - composite.BoxCenter).Length();
                }
                composite.UpdateChildrenFlags(); composite.UpdateChildrenBounds(); composite.UpdateChildrenTransformations(); composite.BuildBVH();
            }
            else if (bound is BoundBVH geometry && (geometry.Polygons?.Length ?? 0) > 0)
            {
                var transform = geometry.Transform; var inverse = geometry.TransformInv;
                try { geometry.Transform = Matrix.Identity; geometry.TransformInv = Matrix.Identity; geometry.BuildBVH(false); }
                finally { geometry.Transform = transform; geometry.TransformInv = inverse; }
            }
        }
        private static byte[] SaveChecked(YbnFile file, string name)
        {
            var bytes = file.Save();
            var loaded = new YbnFile(); loaded.Load(bytes);
            var expected = Leaves(file.Bounds).ToArray(); var actual = Leaves(loaded.Bounds).ToArray();
            if (expected.Length != actual.Length) throw new InvalidDataException("Collision component round trip failed for " + name);
            for (int i = 0; i < expected.Length; i++)
            {
                var a = expected[i]; var b = actual[i];
                if (a.Type != b.Type || Context(a) != Context(b)) throw new InvalidDataException("Collision flags changed while merging " + name);
                var transformA = World(a).ToArray(); var transformB = World(b).ToArray();
                for (int m = 0; m < transformA.Length; m++) if (Math.Abs(transformA[m] - transformB[m]) > 0.00001f)
                    throw new InvalidDataException("Collision transform changed while merging " + name);
                if (!(a is BoundGeometry ga) || !(b is BoundGeometry gb)) continue;
                if ((ga.Polygons?.Length ?? 0) != (gb.Polygons?.Length ?? 0)) throw new InvalidDataException("Collision polygon count changed while merging " + name);
                float tolerance = Math.Max(0.0005f, Math.Max(ga.Quantum.X, Math.Max(ga.Quantum.Y, ga.Quantum.Z)) * 3);
                var wa = World(a); var wb = World(b);
                for (int p = 0; p < ga.Polygons.Length; p++)
                {
                    var pa = ga.Polygons[p]; var pb = gb.Polygons[p];
                    if (pa.Type != pb.Type || !pa.Material.Equals(pb.Material) || pa.VertexIndices.Length != pb.VertexIndices.Length) throw new InvalidDataException("Collision surface metadata changed while merging " + name);
                    for (int v = 0; v < pa.VertexIndices.Length; v++)
                    {
                        var av = Vector3.TransformCoordinate(ga.GetVertex(pa.VertexIndices[v]) + ga.CenterGeom, wa);
                        var bv = Vector3.TransformCoordinate(gb.GetVertex(pb.VertexIndices[v]) + gb.CenterGeom, wb);
                        if (Vector3.Distance(av, bv) > tolerance) throw new InvalidDataException("Collision vertices changed while merging " + name);
                    }
                }
            }
            return bytes;
        }
        private static BoundingBox TransformedBox(Vector3 min, Vector3 max, Matrix transform)
        {
            var low = new Vector3(float.MaxValue); var high = new Vector3(float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var point = Vector3.TransformCoordinate(new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z), transform);
                low = Vector3.Min(low, point); high = Vector3.Max(high, point);
            }
            return new BoundingBox(low, high);
        }
    }
}
