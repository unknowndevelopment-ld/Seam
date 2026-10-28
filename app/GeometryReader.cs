using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using Dx = SharpDX;

namespace Seam
{
    public static class GeometryReader
    {
        public static List<SceneItem> Build(MergeWorkspace workspace, Action<string> progress)
        {
            var assets = workspace.GetEffectiveAssets().Where(a => !a.Excluded && a.Pack.Enabled && a.Error == null).ToArray();
            var archetypes = new Dictionary<uint, Archetype>();
            foreach (var a in assets.Where(a => a.Ytyp != null))
                foreach (var arch in a.Ytyp.AllArchetypes ?? new Archetype[0])
                {
                    if (!archetypes.ContainsKey(arch.Hash.Hash)) archetypes[arch.Hash.Hash] = arch;
                    uint name = arch._BaseArchetypeDef.name.Hash;
                    if (!archetypes.ContainsKey(name)) archetypes[name] = arch;
                }

            var ydrs = Index(assets, ".ydr");
            var ydds = Index(assets, ".ydd");
            var ytds = Index(assets, ".ytd");
            var drawables = new Dictionary<string, Drawable>();
            var dictionaries = new Dictionary<string, YddFile>();
            var textureDictionaries = new Dictionary<string, TextureDictionary>();
            var models = new Dictionary<Tuple<Drawable, string, uint>, Model3DGroup>();
            var materials = new Dictionary<Texture, Material>();
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Action<MapAsset, Exception> failure = (asset, error) =>
            {
                if (failed.Add(asset.Path))
                    workspace.Notices.Add("Preview unavailable for " + asset.Name + ": " + error.Message);
            };
            Func<MapAsset, Drawable> loadYdr = asset =>
            {
                Drawable d;
                if (drawables.TryGetValue(asset.Path, out d)) return d;
                try
                {
                    progress?.Invoke("Reading model · " + asset.Name);
                    var file = new YdrFile();
                    file.Load(File.ReadAllBytes(asset.Path));
                    d = file.Drawable;
                    if (d == null) throw new InvalidDataException("CodeWalker returned no drawable.");
                }
                catch (Exception ex) { failure(asset, ex); d = null; }
                drawables[asset.Path] = d;
                return d;
            };
            Func<MapAsset, YddFile> loadYdd = asset =>
            {
                YddFile d;
                if (dictionaries.TryGetValue(asset.Path, out d)) return d;
                try
                {
                    progress?.Invoke("Reading drawable dictionary · " + asset.Name);
                    d = new YddFile();
                    d.Load(File.ReadAllBytes(asset.Path));
                    if (d.Dict == null) throw new InvalidDataException("CodeWalker returned no drawable dictionary.");
                }
                catch (Exception ex) { failure(asset, ex); d = null; }
                dictionaries[asset.Path] = d;
                return d;
            };
            Func<MapAsset, TextureDictionary> loadYtd = asset =>
            {
                TextureDictionary d;
                if (textureDictionaries.TryGetValue(asset.Path, out d)) return d;
                try
                {
                    progress?.Invoke("Reading textures · " + asset.Name);
                    var file = new YtdFile();
                    file.Load(File.ReadAllBytes(asset.Path));
                    d = file.TextureDict;
                    if (d == null) throw new InvalidDataException("CodeWalker returned no texture dictionary.");
                }
                catch (Exception ex) { failure(asset, ex); d = null; }
                textureDictionaries[asset.Path] = d;
                return d;
            };

            var result = new List<SceneItem>();
            const int sceneLimit = 10000;
            int remainingVertices = 1000000, omitted = 0;
            foreach (var asset in assets)
            {
                foreach (var entity in asset.Entities)
                {
                    var ent = entity.Entity;
                    uint hash = ent.CEntityDef.archetypeName.Hash;
                    Archetype arch;
                    archetypes.TryGetValue(hash, out arch);
                    if (arch != null) ent.SetArchetype(arch);
                    if (IsHashLabel(entity.Label))
                    {
                        var knownName = JenkIndex.TryGetString(hash);
                        if (string.IsNullOrWhiteSpace(knownName) && arch != null)
                            knownName = JenkIndex.TryGetString(arch._BaseArchetypeDef.name.Hash);
                        if (!string.IsNullOrWhiteSpace(knownName)) entity.Label = knownName;
                    }
                    if (result.Count >= sceneLimit)
                    {
                        entity.Bounds = GetEntityBounds(entity, arch);
                        omitted++;
                        continue;
                    }
                    uint drawableHash = arch?._BaseArchetypeDef.assetName.Hash ?? hash;
                    if (drawableHash == 0) drawableHash = hash;
                    Drawable drawable = null;
                    MapAsset modelAsset;
                    if (ydrs.TryGetValue(drawableHash, out modelAsset) || ydrs.TryGetValue(hash, out modelAsset))
                    {
                        drawable = loadYdr(modelAsset);
                        if (drawable != null && IsHashLabel(entity.Label))
                            entity.Label = Path.GetFileNameWithoutExtension(modelAsset.Name);
                    }
                    uint dictHash = arch?.DrawableDict.Hash ?? 0;
                    if (drawable == null && dictHash != 0 && ydds.TryGetValue(dictHash, out modelAsset))
                    {
                        var dict = loadYdd(modelAsset)?.Dict;
                        if (dict != null) dict.TryGetValue(drawableHash, out drawable);
                    }
                    if (drawable == null)
                    {
                        foreach (var candidate in ydds.Values)
                        {
                            var dict = loadYdd(candidate)?.Dict;
                            if (dict != null && (dict.TryGetValue(drawableHash, out drawable) || dict.TryGetValue(hash, out drawable))) break;
                        }
                    }
                    if (drawable != null && IsHashLabel(entity.Label) && !IsHashLabel(drawable.Name))
                        entity.Label = Path.GetFileNameWithoutExtension(drawable.Name.Replace('\\', '/'));

                    entity.Bounds = drawable != null
                        ? TransformBox(drawable.BoundingBoxMin, drawable.BoundingBoxMax, v => EntityPoint(v, ent))
                        : GetEntityBounds(entity, arch);
                    entity.Bounds.Estimated = drawable == null && arch == null;
                    Model3DGroup model = null;
                    if (drawable != null)
                    {
                        var key = Tuple.Create(drawable, asset.Pack.Color, arch?.TextureDict.Hash ?? 0);
                        if (!models.TryGetValue(key, out model))
                        {
                            var embedded = drawable.ShaderGroup?.TextureDictionary;
                            TextureDictionary external = null;
                            if (arch != null && ytds.TryGetValue(arch.TextureDict.Hash, out var tdAsset)) external = loadYtd(tdAsset);
                            Func<TextureBase, Texture> texture = t =>
                            {
                                var actual = t as Texture;
                                if (actual?.Data != null) return actual;
                                actual = embedded?.Lookup(t.NameHash) ?? external?.Lookup(t.NameHash);
                                if (actual != null) return actual;
                                foreach (var td in ytds.Values)
                                {
                                    actual = loadYtd(td)?.Lookup(t.NameHash);
                                    if (actual != null) return actual;
                                }
                                return null;
                            };
                            try { model = DrawableModel(drawable, asset.Pack.Color, texture, materials, workspace.Notices, ref remainingVertices); }
                            catch (Exception ex)
                            {
                                workspace.Notices.Add("Geometry preview unavailable for " + entity.Label + ": " + ex.Message);
                                model = null;
                            }
                            models[key] = model;
                        }
                    }
                    bool proxy = model == null || model.Children.Count == 0;
                    var placed = new Model3DGroup();
                    if (proxy) placed.Children.Add(BoxModel(entity.Bounds, asset.Pack.Color, 0.25));
                    else
                    {
                        placed.Children.Add(model);
                        placed.Transform = EntityTransform(ent);
                    }
                    placed.Freeze();
                    result.Add(new SceneItem { Id = entity.Id, Label = entity.Label, PackId = asset.Pack.Id,
                        Color = asset.Pack.Color, Bounds = entity.Bounds, IsProxy = proxy,
                        Excluded = entity.Excluded, Model = placed });
                }
                foreach (var collision in asset.Collisions)
                {
                    if (result.Count >= sceneLimit) { omitted++; continue; }
                    bool proxy = false;
                    Model3DGroup group;
                    long collisionVertices = CollisionVertices(collision.Bound);
                    if (collisionVertices > remainingVertices)
                    {
                        proxy = true;
                        group = new Model3DGroup();
                        group.Children.Add(BoxModel(collision.Bounds, asset.Pack.Color, 0.2));
                        AddBudgetNotice(workspace.Notices);
                    }
                    else
                    {
                        remainingVertices -= (int)collisionVertices;
                        group = CollisionModel(collision.Bound, asset.Pack.Color, ref proxy);
                        var parent = collision.Bound.Parent;
                        var transform = collision.Bound.Transform;
                        while (parent != null) { transform *= parent.Transform; parent = parent.Parent; }
                        group.Transform = new MatrixTransform3D(Matrix(transform));
                    }
                    group.Freeze();
                    result.Add(new SceneItem { Id = collision.Id, Label = collision.Label, PackId = asset.Pack.Id,
                        Color = asset.Pack.Color, IsCollision = true, IsProxy = proxy, Bounds = collision.Bounds,
                        Excluded = collision.Excluded, Model = group });
                }
            }
            if (omitted > 0) workspace.Notices.Add("3D preview limited to 10,000 items; " + omitted.ToString("N0") + " additional items remain in the conflict scan and export.");
            return result;
        }

        static Dictionary<uint, MapAsset> Index(IEnumerable<MapAsset> assets, string extension)
        {
            var result = new Dictionary<uint, MapAsset>();
            foreach (var a in assets.Where(a => a.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            {
                uint hash = JenkHash.GenHash(Path.GetFileNameWithoutExtension(a.Name).ToLowerInvariant());
                JenkIndex.Ensure(Path.GetFileNameWithoutExtension(a.Name).ToLowerInvariant());
                if (!result.ContainsKey(hash)) result[hash] = a;
            }
            return result;
        }

        static bool IsHashLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            return uint.TryParse(value, out _) || value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("hash_", StringComparison.OrdinalIgnoreCase);
        }

        static Model3DGroup DrawableModel(Drawable drawable, string color, Func<TextureBase, Texture> resolve,
            Dictionary<Texture, Material> materials, List<string> notices, ref int remainingVertices)
        {
            var group = new Model3DGroup();
            var levels = drawable.DrawableModels;
            var models = levels?.High ?? levels?.Med ?? levels?.Low ?? levels?.VLow;
            long vertices = (models ?? new CodeWalker.GameFiles.DrawableModel[0])
                .SelectMany(m => m.Geometries ?? new DrawableGeometry[0])
                .Sum(g => (long)(g.VertexData ?? g.VertexBuffer?.Data1)?.VertexCount);
            if (vertices > remainingVertices)
            {
                AddBudgetNotice(notices);
                group.Freeze();
                return group;
            }
            remainingVertices -= (int)vertices;
            foreach (var model in models ?? new CodeWalker.GameFiles.DrawableModel[0])
                foreach (var geometry in model.Geometries ?? new DrawableGeometry[0])
                {
                    var data = geometry.VertexData ?? geometry.VertexBuffer?.Data1;
                    var indices = geometry.IndexBuffer?.Indices;
                    if (data?.Info == null || indices == null || data.VertexCount == 0) continue;
                    var mesh = new MeshGeometry3D();
                    var uvType = data.Info.GetComponentType((int)VertexSemantics.TexCoord0);
                    for (int i = 0; i < data.VertexCount; i++)
                    {
                        var v = data.GetVector3(i, (int)VertexSemantics.Position);
                        mesh.Positions.Add(new Point3D(v.X, v.Y, v.Z));
                        if (uvType == VertexComponentType.Float2)
                        {
                            var uv = data.GetVector2(i, (int)VertexSemantics.TexCoord0);
                            mesh.TextureCoordinates.Add(new Point(uv.X, uv.Y));
                        }
                        else if (uvType == VertexComponentType.Half2)
                        {
                            var uv = data.GetHalf2(i, (int)VertexSemantics.TexCoord0);
                            mesh.TextureCoordinates.Add(new Point((float)uv.X, (float)uv.Y));
                        }
                    }
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                        if (indices[i] < data.VertexCount && indices[i + 1] < data.VertexCount && indices[i + 2] < data.VertexCount)
                        { mesh.TriangleIndices.Add(indices[i]); mesh.TriangleIndices.Add(indices[i + 1]); mesh.TriangleIndices.Add(indices[i + 2]); }
                    mesh.Freeze();
                    Material material = Solid(color, 1);
                    var parameters = geometry.Shader?.ParametersList;
                    TextureBase texture = null;
                    if (parameters?.Parameters != null)
                    {
                        uint diffuse = (uint)ShaderParamNames.DiffuseSampler;
                        for (int i = 0; i < parameters.Parameters.Length; i++)
                            if (parameters.Parameters[i].Data is TextureBase t &&
                                parameters.Hashes != null && i < parameters.Hashes.Length && (uint)parameters.Hashes[i] == diffuse)
                            { texture = t; break; }
                    }
                    if (texture != null)
                    {
                        var actual = resolve(texture);
                        if (actual != null)
                        {
                            if (!materials.TryGetValue(actual, out material))
                            {
                                try
                                {
                                    int mip = 0;
                                    while (mip + 1 < actual.Levels && Math.Max(actual.Width >> mip, actual.Height >> mip) > 1024) mip++;
                                    int w = Math.Max(1, actual.Width >> mip), h = Math.Max(1, actual.Height >> mip);
                                    var pixels = DDSIO.GetPixels(actual, mip);
                                    if (pixels == null || pixels.Length < w * h * 4) throw new InvalidDataException("Texture format is not supported by this CodeWalker decoder.");
                                    var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                                    bitmap.Freeze();
                                    var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
                                    brush.Freeze();
                                    material = new DiffuseMaterial(brush);
                                    material.Freeze();
                                }
                                catch (Exception ex)
                                {
                                    string warning = "Texture preview " + actual.Name + ": " + ex.Message;
                                    if (!notices.Contains(warning)) notices.Add(warning);
                                    material = Solid(color, 1);
                                }
                                materials[actual] = material;
                            }
                        }
                    }
                    var surface = new GeometryModel3D(mesh, material) { BackMaterial = material };
                    surface.Freeze();
                    group.Children.Add(surface);
                }
            group.Freeze();
            return group;
        }

        static void AddBudgetNotice(List<string> notices)
        {
            const string warning = "3D mesh budget reached (1,000,000 vertices). Additional models use bounds previews; export retains the original files.";
            if (!notices.Contains(warning)) notices.Add(warning);
        }

        static long CollisionVertices(Bounds bound)
        {
            if (bound is BoundComposite composite)
                return (composite.Children?.data_items ?? new Bounds[0]).Where(b => b != null).Sum(CollisionVertices);
            if (bound is BoundGeometry geometry)
                return (geometry.Polygons ?? new BoundPolygon[0]).LongCount(p => p is BoundPolygonTriangle) * 3;
            return 0;
        }

        static Model3DGroup CollisionModel(Bounds bound, string color, ref bool proxy)
        {
            var group = new Model3DGroup();
            if (bound is BoundComposite composite)
            {
                foreach (var child in composite.Children?.data_items ?? new Bounds[0])
                    if (child != null) group.Children.Add(CollisionModel(child, color, ref proxy));
            }
            else if (bound is BoundGeometry geometry && geometry.Polygons != null)
            {
                var mesh = new MeshGeometry3D();
                bool hasPrimitives = false;
                foreach (var polygon in geometry.Polygons)
                {
                    if (!(polygon is BoundPolygonTriangle tri)) { hasPrimitives = true; continue; }
                    int index = mesh.Positions.Count;
                    // Vertex1/2/3 already apply the bound transform. Keep these vertices local;
                    // the model group applies this bound and its ancestors exactly once.
                    foreach (int vertex in new[] { tri.vertIndex1, tri.vertIndex2, tri.vertIndex3 })
                    {
                        var p = geometry.GetVertex(vertex) + geometry.CenterGeom;
                        mesh.Positions.Add(new Point3D(p.X, p.Y, p.Z));
                    }
                    mesh.TriangleIndices.Add(index); mesh.TriangleIndices.Add(index + 1); mesh.TriangleIndices.Add(index + 2);
                }
                if (mesh.Positions.Count > 0)
                {
                    mesh.Freeze();
                    var material = Solid(color, 0.45);
                    group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
                }
                if (hasPrimitives || mesh.Positions.Count == 0) group.Children.Add(BoxModel(LocalBox(bound), color, 0.12));
                proxy |= hasPrimitives || mesh.Positions.Count == 0;
            }
            else { proxy = true; group.Children.Add(BoxModel(LocalBox(bound), color, 0.25)); }
            group.Transform = new MatrixTransform3D(Matrix(bound.Transform));
            // The caller may attach an ancestor transform before freezing the root.
            return group;
        }

        public static Box3 GetCollisionBounds(Bounds bound)
        {
            var transform = bound.Transform;
            var parent = bound.Parent;
            while (parent != null) { transform *= parent.Transform; parent = parent.Parent; }
            return TransformBox(bound.BoxMin, bound.BoxMax, v => Dx.Vector3.TransformCoordinate(v, transform));
        }

        public static Box3 GetEntityBounds(MapEntity entity, Archetype arch = null)
        {
            var ent = entity.Entity;
            arch = arch ?? ent.Archetype;
            var min = arch?.BBMin ?? new Dx.Vector3(-0.75f);
            var max = arch?.BBMax ?? new Dx.Vector3(0.75f);
            var box = TransformBox(min, max, v => EntityPoint(v, ent));
            box.Estimated = arch == null;
            return box;
        }

        static Dx.Vector3 EntityPoint(Dx.Vector3 v, YmapEntityDef ent)
            => Dx.Vector3.Transform(v * ent.Scale, ent.Orientation) + ent.Position;

        static Transform3D EntityTransform(YmapEntityDef ent)
        {
            var group = new Transform3DGroup();
            group.Children.Add(new ScaleTransform3D(ent.Scale.X, ent.Scale.Y, ent.Scale.Z));
            var q = ent.Orientation;
            group.Children.Add(new RotateTransform3D(new QuaternionRotation3D(new Quaternion(q.X, q.Y, q.Z, q.W))));
            group.Children.Add(new TranslateTransform3D(ent.Position.X, ent.Position.Y, ent.Position.Z));
            group.Freeze();
            return group;
        }

        static Box3 TransformBox(Dx.Vector3 min, Dx.Vector3 max, Func<Dx.Vector3, Dx.Vector3> transform)
        {
            var a = new Dx.Vector3(float.MaxValue);
            var b = new Dx.Vector3(float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var p = transform(new Dx.Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
                a = Dx.Vector3.Min(a, p); b = Dx.Vector3.Max(b, p);
            }
            return new Box3 { MinX = a.X, MinY = a.Y, MinZ = a.Z, MaxX = b.X, MaxY = b.Y, MaxZ = b.Z };
        }

        static Box3 LocalBox(Bounds bound) => new Box3 { MinX = bound.BoxMin.X, MinY = bound.BoxMin.Y, MinZ = bound.BoxMin.Z,
            MaxX = bound.BoxMax.X, MaxY = bound.BoxMax.Y, MaxZ = bound.BoxMax.Z };

        public static GeometryModel3D BoxModel(Box3 box, string color, double opacity)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i < 8; i++) mesh.Positions.Add(new Point3D((i & 1) == 0 ? box.MinX : box.MaxX,
                (i & 2) == 0 ? box.MinY : box.MaxY, (i & 4) == 0 ? box.MinZ : box.MaxZ));
            foreach (int i in new[] { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 }) mesh.TriangleIndices.Add(i);
            mesh.Freeze();
            var material = Solid(color, opacity);
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
            model.Freeze();
            return model;
        }

        static Material Solid(string color, double opacity)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color ?? "#7DADA5")) { Opacity = opacity };
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            return material;
        }

        static Matrix3D Matrix(Dx.Matrix m) => new Matrix3D(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    }
}
