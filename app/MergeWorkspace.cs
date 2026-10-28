using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;
using CodeWalker.GameFiles;
using SharpDX;

namespace Seam
{
    public sealed partial class MergeWorkspace
    {
        private static readonly HashSet<string> StreamTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".ymap", ".ytyp", ".ybn", ".ydr", ".ydd", ".ytd", ".yft", ".ycd", ".ypt", ".ynv", ".yld", ".ymt", ".yvr", ".yvn", ".ymf", ".ynd", ".ywr", ".yfd", ".awc", ".rel" };
        private static readonly string[] Colors = { "#7ACBB5", "#E6AE75", "#9CAFE9", "#DA9CBD", "#B7C783", "#8CC4D8" };
        private readonly Dictionary<string, string> decisions = new Dictionary<string, string>();
        private readonly List<string> importNotices = new List<string>();
        internal static string StableId(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()))).Replace("-", "").Substring(0, 24);
        }
        internal static string HashFile(string path)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        internal static bool IsXml(string path) => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        internal static string StreamName(string path) => System.IO.Path.GetFileName(IsXml(path) ? path.Substring(0, path.Length - 4) : path);
        private static string KindOf(string path) => System.IO.Path.GetExtension(StreamName(path)).ToLowerInvariant();

        public void Import(string[] paths, Action<string> progress = null)
        {
            if (paths == null || paths.Length == 0) return;
            // Build imports privately: an interrupted/failed import cannot leave half a pack.
            var pending = new List<MapPack>();
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var input in paths)
            {
                var full = System.IO.Path.GetFullPath(input);
                if (Directory.Exists(full))
                {
                    var children = Directory.EnumerateDirectories(full).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0 && IsResourceFolder(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (!IsResourceFolder(full) && children.Length > 0)
                        foreach (var child in children) groups[child] = EnumerateSafe(child).ToList();
                    else groups[full] = EnumerateSafe(full).ToList();
                }
                else if (File.Exists(full))
                {
                    var parent = System.IO.Path.GetDirectoryName(full);
                    if (!groups.TryGetValue(parent, out var files)) groups[parent] = files = new List<string>();
                    files.Add(full);
                }
                else throw new FileNotFoundException("The selected path no longer exists.", full);
            }
            var loaded = new HashSet<string>(Packs.SelectMany(p => p.Assets).Select(a => a.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                var pack = new MapPack { Id = StableId("pack:" + group.Key), Root = group.Key, Name = new DirectoryInfo(group.Key).Name, Color = Colors[(Packs.Count + pending.Count) % Colors.Length] };
                var list = group.Value.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
                int i = 0;
                foreach (var file in list)
                {
                    if (!StreamTypes.Contains(KindOf(file)) || (IsXml(file) && KindOf(file) != ".ymap" && KindOf(file) != ".ytyp" && KindOf(file) != ".ybn")) continue;
                    if (!loaded.Add(file)) continue;
                    progress?.Invoke($"Reading {pack.Name} · {++i}: {System.IO.Path.GetFileName(file)}");
                    var asset = new MapAsset { Id = StableId("asset:" + file), Pack = pack, Path = file, Name = StreamName(file), Extension = KindOf(file) };
                    try
                    {
                        asset.Digest = HashFile(file);
                        if (new FileInfo(file).Length > 256L * 1024 * 1024) throw new InvalidDataException("File exceeds the 256 MB preview/merge limit; original bytes will be preserved.");
                        ReadAsset(asset);
                    }
                    catch (Exception ex) { asset.Error = ex.Message; asset.Entities.Clear(); asset.Collisions.Clear(); asset.Ymap = null; asset.Ybn = null; asset.Ytyp = null; }
                    pack.Assets.Add(asset);
                }
                if (pack.Assets.Count > 0) pending.Add(pack);
                if (list.Any(f => System.IO.Path.GetExtension(f).Equals(".rpf", StringComparison.OrdinalIgnoreCase)))
                    importNotices.Add(pack.Name + ": RPF archives must be extracted with CodeWalker first.");
                if (list.Any(f => System.IO.Path.GetExtension(f).Equals(".lua", StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetExtension(f).Equals(".meta", StringComparison.OrdinalIgnoreCase)))
                    importNotices.Add(pack.Name + ": local scripts and manifest declarations are included by automatic export.");
            }
            if (pending.Count == 0) throw new InvalidDataException("No new supported map assets found. Import extracted map folders or loose YMAP, YTYP, YBN, YDR, YDD and YTD files.");
            foreach (var pack in pending)
            {
                // Separate imports from one folder retain one deterministic pack identity.
                var existing = Packs.FirstOrDefault(p => p.Id == pack.Id);
                if (existing != null) existing.Assets.AddRange(pack.Assets.Select(a => { a.Pack = existing; return a; }));
                else Packs.Add(pack);
            }
            Refresh(progress);
        }

        private static IEnumerable<string> EnumerateSafe(string directory)
        {
            // Do not follow junctions into unrelated folders or recursively import our own exports.
            foreach (var file in Directory.EnumerateFiles(directory))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                var name = System.IO.Path.GetFileName(child);
                if (name == ".git" || name == "node_modules" || name.StartsWith(".seam-", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var file in EnumerateSafe(child)) yield return file;
            }
        }
        private static bool IsResourceFolder(string directory) => File.Exists(System.IO.Path.Combine(directory, "fxmanifest.lua")) || File.Exists(System.IO.Path.Combine(directory, "__resource.lua"));

        internal static byte[] ReadResourceBytes(string path, string extension)
        {
            if (!IsXml(path)) return File.ReadAllBytes(path);
            var doc = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128L * 1024 * 1024 })) doc.Load(reader);
            if (extension == ".ybn") return XmlYbn.GetYbn(doc).Save();
            var result = XmlMeta.GetRSCData(doc);
            if (result == null || result.Length == 0) throw new InvalidDataException("The XML contains no readable GTA resource data.");
            return result;
        }

        private static void ReadAsset(MapAsset asset)
        {
            if (!IsXml(asset.Path) && new[] { ".ymap", ".ytyp", ".ybn", ".ydr", ".ydd", ".ytd", ".yft" }.Contains(asset.Extension))
            {
                using (var file = File.OpenRead(asset.Path))
                using (var reader = new BinaryReader(file))
                {
                    if (file.Length < 16 || reader.ReadUInt32() != 0x37435352) throw new InvalidDataException("Encrypted or unsupported resource: skipped decoding; original bytes will be preserved.");
                    uint version = reader.ReadUInt32();
                    if ((asset.Extension == ".ytd" && version != 13) || (asset.Extension == ".ydr" && version != 165) || (asset.Extension == ".ydd" && version != 165))
                        throw new InvalidDataException("This model or texture uses an unsupported resource version. This build supports GTA V Legacy assets.");
                }
            }
            if (asset.Extension != ".ymap" && asset.Extension != ".ybn" && asset.Extension != ".ytyp") return;
            var bytes = ReadResourceBytes(asset.Path, asset.Extension);
            if (asset.Extension == ".ymap")
            {
                var ymap = new YmapFile(); ymap.Load(bytes); ymap.Name = asset.Name;
                if (ymap.Meta == null) throw new InvalidDataException("Only GTA V PC resource YMAPs are editable.");
                asset.Ymap = ymap;
                var entities = ymap.AllEntities ?? new YmapEntityDef[0];
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    if (!Finite(e.Position.X) || !Finite(e.Position.Y) || !Finite(e.Position.Z) || !Finite(e.Scale.X) || !Finite(e.Scale.Y) || !Finite(e.Scale.Z) ||
                        !Finite(e.Orientation.X) || !Finite(e.Orientation.Y) || !Finite(e.Orientation.Z) || !Finite(e.Orientation.W) || e.Orientation.LengthSquared() < 0.000001f)
                        throw new InvalidDataException("Entity contains invalid coordinates.");
                    var label = JenkIndex.GetString(e._CEntityDef.archetypeName.Hash);
                    asset.Entities.Add(new MapEntity { Id = StableId(asset.Id + ":entity:" + i), Asset = asset, Index = i, Entity = e, Label = string.IsNullOrWhiteSpace(label) ? "0x" + e._CEntityDef.archetypeName.Hash.ToString("X8") : label, Bounds = GeometryReader.GetEntityBounds(new MapEntity { Entity = e }) });
                }
            }
            else if (asset.Extension == ".ybn")
            {
                var ybn = new YbnFile(); ybn.Load(bytes); ybn.Name = asset.Name;
                if (ybn.Bounds == null) throw new InvalidDataException("No collision bounds found.");
                asset.Ybn = ybn;
                int index = 0;
                foreach (var bound in FlattenBounds(ybn.Bounds))
                {
                    var box = GeometryReader.GetCollisionBounds(bound);
                    if (!Finite(box.MinX) || !Finite(box.MaxX) || !Finite(box.MinY) || !Finite(box.MaxY) || !Finite(box.MinZ) || !Finite(box.MaxZ)) throw new InvalidDataException("Invalid collision coordinates.");
                    asset.Collisions.Add(new CollisionPiece { Id = StableId(asset.Id + ":bound:" + index), Asset = asset, Index = index++, Bound = bound, Bounds = box });
                }
            }
            else
            {
                var ytyp = new YtypFile(); ytyp.Load(bytes); ytyp.Name = asset.Name;
                asset.Ytyp = ytyp;
            }
        }

        internal static IEnumerable<Bounds> FlattenBounds(Bounds root)
        {
            if (root is BoundComposite composite && composite.Children?.data_items != null)
            {
                foreach (var child in composite.Children.data_items)
                    if (child != null) foreach (var leaf in FlattenBounds(child)) yield return leaf;
            }
            else if (root != null) yield return root;
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        public IEnumerable<MapAsset> GetEffectiveAssets() => Packs.Where(p => p.Enabled).SelectMany(p => p.Assets).Where(a => !a.Excluded).GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First());

        public void Refresh(Action<string> progress = null)
        {
            LastAutomaticMerge = null;
            progress?.Invoke("Building the 3D scene…");
            Notices.Clear(); Notices.AddRange(importNotices.Distinct());
            Scene.Clear(); Scene.AddRange(GeometryReader.Build(this, progress));
            progress?.Invoke("Checking shared assets and collision bounds…");
            Issues.Clear();
            var all = Packs.Where(p => p.Enabled).SelectMany(p => p.Assets).Where(a => !a.Excluded).ToList();
            foreach (var asset in all.Where(a => a.Error != null))
                AddIssue("Unreadable file", asset.Name, asset.Error + " Exclude this file or remove its source map before export.", asset.Id, null, asset.Pack.Name, null, null, false);
            foreach (var group in all.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var winner = group.First();
                foreach (var other in group.Skip(1))
                {
                    bool same = winner.Digest != null && winner.Digest == other.Digest;
                    AddIssue(same ? "Identical file" : "Shared filename", winner.Name, same ? "These files are byte-for-byte identical. Keep the highest-priority copy." : "GTA streams one asset per filename. Choose which complete asset to keep; renaming can break references.", winner.Id, other.Id, winner.Pack.Name, other.Pack.Name, null, same);
                }
            }
            var effective = GetEffectiveAssets().Where(a => a.Error == null).ToList();
            var entities = effective.SelectMany(a => a.Entities).Where(e => !e.Excluded && !e.Entity.IsMlo).ToList();
            var duplicatePairs = new HashSet<string>();
            foreach (var group in entities.GroupBy(e => PlacementKey(e)))
            {
                var ordered = group.ToList();
                for (int n = 1; n < ordered.Count; n++)
                {
                    var a = ordered[0]; var b = ordered[n];
                    if (a.Asset == b.Asset) continue;
                    bool exact = a.Entity._CEntityDef.Equals(b.Entity._CEntityDef) && (a.Entity.Extensions?.Length ?? 0) == 0 && (b.Entity.Extensions?.Length ?? 0) == 0;
                    bool safe = exact && CanEditEntity(b, out _) && a.Entity._CEntityDef.parentIndex < 0 && b.Entity._CEntityDef.parentIndex < 0 && a.Entity._CEntityDef.numChildren == 0 && b.Entity._CEntityDef.numChildren == 0;
                    AddIssue("Duplicate placement", a.Label, safe ? "Same placement and complete entity definition. The lower-priority instance can be removed automatically." : "Same model and transform. Entity metadata or hierarchy needs a manual decision.", a.Id, b.Id, a.Asset.Pack.Name + " / " + a.Asset.Name, b.Asset.Pack.Name + " / " + b.Asset.Name, a.Bounds, safe);
                    duplicatePairs.Add(Pair(a.Id, b.Id));
                }
            }
            int overlapCount = 0;
            foreach (var pair in SpatialPairs(entities, e => e.Bounds))
            {
                var a = pair.Item1; var b = pair.Item2;
                if (a.Asset.Pack == b.Asset.Pack || duplicatePairs.Contains(Pair(a.Id, b.Id))) continue;
                if (a.Bounds.Estimated || b.Bounds.Estimated) continue;
                if (!Acknowledged("Model bounds overlap", a.Id, b.Id) && overlapCount++ >= 4000) { Notices.Add("Entity overlap review is capped at 4,000 unresolved pairs. Resolve these and scan again."); break; }
                AddIssue("Model bounds overlap", a.Label + " / " + b.Label, "Transformed model bounds overlap across maps. This is a candidate, not proof of intersecting surfaces. Inspect in 3D, then exclude one entity or keep both.", a.Id, b.Id, a.Asset.Pack.Name, b.Asset.Pack.Name, Union(a.Bounds, b.Bounds), false);
            }
            overlapCount = 0;
            var collisions = effective.SelectMany(a => a.Collisions).Where(c => !c.Excluded).ToList();
            foreach (var pair in SpatialPairs(collisions, c => c.Bounds, true))
            {
                var a = pair.Item1; var b = pair.Item2;
                if (a.Asset.Pack == b.Asset.Pack) continue;
                if (!Acknowledged("Collision bounds overlap", a.Id, b.Id) && overlapCount++ >= 4000) { Notices.Add("Collision review is capped at 4,000 unresolved pairs. Resolve these and scan again."); break; }
                AddIssue("Collision bounds overlap", a.Asset.Name + " / " + b.Asset.Name, "YBN component bounds overlap. Roads, floors and adjacent surfaces may intentionally share space. Choose which entire collision component to retain, or keep both. This does not cut triangles.", a.Id, b.Id, a.Asset.Pack.Name + " · bound " + a.Index, b.Asset.Pack.Name + " · bound " + b.Index, Union(a.Bounds, b.Bounds), false);
            }
            foreach (var group in effective.Where(a => a.Ytyp?.AllArchetypes != null).SelectMany(a => a.Ytyp.AllArchetypes.Select(t => new { Asset = a, Type = t })).GroupBy(t => t.Type.Hash.Hash).Where(g => g.Select(x => x.Asset.Id).Distinct().Count() > 1))
            {
                var first = group.First();
                foreach (var other in group.Skip(1).Where(t => t.Asset != first.Asset))
                    AddIssue("Archetype override", "Shared archetype 0x" + group.Key.ToString("X8"), "Two YTYP files define the same archetype. Choosing a side excludes the other whole YTYP file. Keep both only if the original resource intentionally uses this override.", first.Asset.Id, other.Asset.Id, first.Asset.Pack.Name + " / " + first.Asset.Name, other.Asset.Pack.Name + " / " + other.Asset.Name, null, false);
            }
            progress?.Invoke($"Ready · {EntityCount:N0} entities · {ActiveIssueCount:N0} decisions");
        }

        private static string PlacementKey(MapEntity e)
        {
            var d = e.Entity._CEntityDef;
            return d.archetypeName.Hash + ":" + d.position.X.ToString("R", CultureInfo.InvariantCulture) + ":" + d.position.Y.ToString("R", CultureInfo.InvariantCulture) + ":" + d.position.Z.ToString("R", CultureInfo.InvariantCulture) + ":" + d.rotation.ToString() + ":" + d.scaleXY.ToString("R", CultureInfo.InvariantCulture) + ":" + d.scaleZ.ToString("R", CultureInfo.InvariantCulture) + ":" + d.lodLevel;
        }
        private static string Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + ":" + b : b + ":" + a;
        internal static Box3 Union(Box3 a, Box3 b) => new Box3 { MinX = Math.Min(a.MinX, b.MinX), MinY = Math.Min(a.MinY, b.MinY), MinZ = Math.Min(a.MinZ, b.MinZ), MaxX = Math.Max(a.MaxX, b.MaxX), MaxY = Math.Max(a.MaxY, b.MaxY), MaxZ = Math.Max(a.MaxZ, b.MaxZ) };
        private static IEnumerable<Tuple<T, T>> SpatialPairs<T>(IEnumerable<T> values, Func<T, Box3> box, bool includeTouching = false)
        {
            var active = new List<T>();
            foreach (var item in values.Where(v => box(v) != null).OrderBy(v => box(v).MinX))
            {
                var current = box(item);
                active.RemoveAll(a => includeTouching ? box(a).MaxX < current.MinX - 0.002 : box(a).MaxX <= current.MinX);
                foreach (var previous in active) if (includeTouching ? current.Touches(box(previous)) : current.Overlaps(box(previous))) yield return Tuple.Create(previous, item);
                active.Add(item);
            }
        }
        private void AddIssue(string kind, string title, string detail, string left, string right, string leftLabel, string rightLabel, Box3 bounds, bool auto)
        {
            var key = StableId(kind + ":" + Pair(left ?? "", right ?? ""));
            decisions.TryGetValue(key, out var decision);
            Issues.Add(new MergeIssue { Id = key, Kind = kind, Title = title, Detail = detail, LeftId = left, RightId = right, LeftLabel = leftLabel, RightLabel = rightLabel, Bounds = bounds, CanAutoFix = auto, Resolved = decision == "keep-both", Resolution = decision == "keep-both" ? "Keep both" : null });
        }
        private bool Acknowledged(string kind, string left, string right) => decisions.TryGetValue(StableId(kind + ":" + Pair(left, right)), out var choice) && choice == "keep-both";

        private bool CanEditEntity(MapEntity entity, out string reason)
        {
            var map = entity.Asset.Ymap;
            reason = null;
            if (map == null || map.Meta == null) reason = "This YMAP is not an editable PC resource.";
            else if (map.CMapData.containerLods.Count1 > 0 || map.CMapData.instancedData.PropInstanceList.Count1 > 0) reason = "CodeWalker cannot preserve container LODs or instanced props when rewriting this YMAP. Exclude the entire file instead.";
            else if (map.CMapData.parent.Hash != 0 || GetEffectiveAssets().Any(a => a.Ymap != null && a != entity.Asset && a.Ymap.CMapData.parent.Hash == map.CMapData.name.Hash)) reason = "This YMAP has external LOD links. Removing an indexed entity could break another map; exclude the entire file instead.";
            else if (entity.Asset.Entities.Any(parent => parent.Entity._CEntityDef.numChildren != entity.Asset.Entities.Count(child => child.Entity._CEntityDef.parentIndex == parent.Index))) reason = "This YMAP has incomplete or external LOD branches. Preserve its original indices, or exclude its whole file.";
            else if ((map.MloEntities?.Length ?? 0) > 0 || entity.Entity.IsMlo) reason = "This YMAP includes MLO instances. Preserve its original file; exclude the whole YMAP if needed.";
            else if (entity.Entity._CEntityDef.numChildren > 0 || entity.Asset.Entities.Any(e => !e.Excluded && e.Entity._CEntityDef.parentIndex == entity.Index)) reason = "This entity has LOD children. Preserve its branch, or exclude its whole YMAP.";
            return reason == null;
        }

        public void Exclude(string id)
        {
            var assets = Packs.SelectMany(p => p.Assets).ToList();
            var asset = assets.FirstOrDefault(a => a.Id == id);
            if (asset != null) { asset.Excluded = true; return; }
            var entity = assets.SelectMany(a => a.Entities).FirstOrDefault(e => e.Id == id);
            if (entity != null)
            {
                if (!CanEditEntity(entity, out var reason)) throw new InvalidOperationException(reason);
                entity.Excluded = true; return;
            }
            var bound = assets.SelectMany(a => a.Collisions).FirstOrDefault(c => c.Id == id);
            if (bound != null) { bound.Excluded = true; return; }
            throw new InvalidOperationException("The selected item is no longer in this workspace.");
        }

        public void Resolve(MergeIssue issue, string choice)
        {
            if (issue == null || !Issues.Contains(issue)) return;
            if (choice == "keep-both")
            {
                if (issue.Kind == "Shared filename" || issue.Kind == "Unreadable file") throw new InvalidOperationException("GTA cannot stream both versions under the same filename. Choose one file.");
            }
            else if (choice == "keep-left")
            {
                if (issue.RightId == null) throw new InvalidOperationException("Exclude the unreadable file to continue.");
                Exclude(issue.RightId);
            }
            else if (choice == "keep-right") Exclude(issue.LeftId);
            else throw new ArgumentException("Unknown resolution.");
            decisions[issue.Id] = choice; issue.Resolved = true; issue.Resolution = choice;
        }

        public int AutoResolveDuplicates()
        {
            int count = 0;
            foreach (var issue in Issues.Where(i => !i.Resolved && i.CanAutoFix).ToList()) { Resolve(issue, "keep-left"); count++; }
            return count;
        }
        public void RestoreAll()
        {
            foreach (var asset in Packs.SelectMany(p => p.Assets))
            {
                asset.Excluded = false;
                foreach (var e in asset.Entities) e.Excluded = false;
                foreach (var c in asset.Collisions) c.Excluded = false;
            }
            decisions.Clear();
        }
    }
}
