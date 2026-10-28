using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using CodeWalker.GameFiles;

namespace Seam
{
    public sealed partial class MergeWorkspace
    {
        public static string[] CreateDemo(string directory) => DemoFactory.Create(directory);

        public ExportResult Export(string parentDirectory, string resourceName, Action<string> progress = null)
        {
            if (!Regex.IsMatch(resourceName ?? "", "^[a-zA-Z][a-zA-Z0-9_-]{0,63}$")) throw new ArgumentException("Use a resource name starting with a letter and containing only letters, digits, _ and - (up to 64 characters).");
            if (!Directory.Exists(parentDirectory)) throw new DirectoryNotFoundException("Choose an existing output folder.");
            Refresh(progress);
            if (ActiveIssueCount > 0) throw new InvalidOperationException($"Review the remaining {ActiveIssueCount:N0} conflicts before export. Use Keep both for intentional spatial overlaps.");
            var assets = GetEffectiveAssets().ToList();
            if (assets.Count == 0 || !assets.Any(a => a.Ymap != null)) throw new InvalidOperationException("Enable at least one map containing a YMAP before export.");
            var parent = System.IO.Path.GetFullPath(parentDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            foreach (var pack in Packs)
            {
                var root = System.IO.Path.GetFullPath(pack.Root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                if (parent.Equals(root, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Choose an output folder outside the imported map folders, so source files stay untouched.");
            }
            var final = System.IO.Path.Combine(parent, resourceName);
            if (Directory.Exists(final) || File.Exists(final))
            {
                int version = 2;
                while (Directory.Exists(final + "_" + version) || File.Exists(final + "_" + version)) version++;
                final += "_" + version;
            }
            var stage = System.IO.Path.Combine(parent, ".seam-" + Guid.NewGuid().ToString("N"));
            var result = new ExportResult { Path = final };
            try
            {
                Directory.CreateDirectory(System.IO.Path.Combine(stage, "stream"));
                var exported = new List<string>();
                foreach (var asset in assets)
                {
                    progress?.Invoke("Writing " + asset.Name);
                    if (asset.Error != null) throw new InvalidDataException(asset.Name + ": " + asset.Error);
                    if (!File.Exists(asset.Path) || HashFile(asset.Path) != asset.Digest)
                        throw new IOException("Source changed after import: " + asset.Path + ". Import it again before exporting.");
                    byte[] edited = null;
                    if (asset.Ymap != null && asset.Entities.Any(e => e.Excluded))
                    {
                        edited = WriteEditedMap(asset);
                        result.RemovedEntities += asset.Entities.Count(e => e.Excluded);
                    }
                    else if (asset.Ybn != null && asset.Collisions.Any(c => c.Excluded))
                    {
                        result.RemovedBounds += asset.Collisions.Count(c => c.Excluded);
                        if (asset.Collisions.All(c => c.Excluded)) continue;
                        edited = WriteEditedCollision(asset);
                    }
                    else if (IsXml(asset.Path)) edited = ReadResourceBytes(asset.Path, asset.Extension);
                    var destination = System.IO.Path.Combine(stage, "stream", asset.Name);
                    if (File.Exists(destination)) throw new InvalidOperationException("Two source assets would overwrite " + asset.Name + ". Resolve their filename conflict first.");
                    if (edited != null) File.WriteAllBytes(destination, edited);
                    else File.Copy(asset.Path, destination, false);
                    // Check the exact copied bytes as well as the pre-copy snapshot, closing a source-write race.
                    if (edited == null && HashFile(destination) != asset.Digest) throw new IOException("Source changed while copying " + asset.Name + ". No export was committed.");
                    if (HashFile(asset.Path) != asset.Digest) throw new IOException("Source changed while processing " + asset.Name + ". No export was committed.");
                    exported.Add(asset.Name); result.Files++;
                }
                var manifest = new StringBuilder("fx_version 'cerulean'\ngame 'gta5'\n\nthis_is_a_map 'yes'\n\n");
                foreach (var name in exported.Where(n => n.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)))
                    manifest.AppendLine("data_file 'DLC_ITYP_REQUEST' 'stream/" + LuaEscape(name) + "'");
                File.WriteAllText(System.IO.Path.Combine(stage, "fxmanifest.lua"), manifest.ToString(), new UTF8Encoding(false));
                var report = new
                {
                    application = "Seam 1.0", exportedUtc = DateTime.UtcNow.ToString("o"),
                    sources = Packs.Where(p => p.Enabled).Select(p => new { p.Name, p.Root }).ToArray(),
                    files = exported.ToArray(),
                    removedEntities = Packs.SelectMany(p => p.Assets).SelectMany(a => a.Entities).Where(e => e.Excluded).Select(e => new { file = e.Asset.Name, source = e.Asset.Pack.Name, index = e.Index, model = e.Label }).ToArray(),
                    removedBounds = Packs.SelectMany(p => p.Assets).SelectMany(a => a.Collisions).Where(c => c.Excluded).Select(c => new { file = c.Asset.Name, source = c.Asset.Pack.Name, index = c.Index }).ToArray(),
                    excludedFiles = Packs.SelectMany(p => p.Assets).Where(a => a.Excluded).Select(a => new { file = a.Name, source = a.Pack.Name }).ToArray(),
                    decisions = decisions.ToArray(), notices = Notices.ToArray()
                };
                File.WriteAllText(System.IO.Path.Combine(stage, "seam-merge-report.json"), Json().Serialize(report), new UTF8Encoding(false));
                File.WriteAllText(System.IO.Path.Combine(stage, "README.txt"),
                    "Merged with Seam\r\n\r\nCopy this folder into your FiveM server resources and ensure it in server.cfg. Disable the original map resources to avoid loading both versions.\r\n\r\nThis is a map-only resource. Original scripts, manifests, metadata, dependencies and base-game removals are not combined. Review the original requirements. Unchanged binary assets were copied byte-for-byte. See seam-merge-report.json for decisions.\r\n\r\nOverlap review uses transformed bounds. It does not prove triangle intersections, perform mesh boolean subtraction, or guarantee collision-free gameplay. Check the result in GTA V before use.\r\n", new UTF8Encoding(false));
                CommitExport(stage, final);
                progress?.Invoke("Export complete · " + result.Files + " assets");
                return result;
            }
            finally
            {
                // Only remove the private staging directory allocated above, never an existing destination.
                if (Directory.Exists(stage) && System.IO.Path.GetDirectoryName(stage).Equals(parent, StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileName(stage).StartsWith(".seam-", StringComparison.Ordinal))
                    Directory.Delete(stage, true);
            }
        }

        private static void CommitExport(string stage, string final)
        {
            // Windows scanners can briefly hold a newly written directory. Retry only those
            // observed transient access/sharing errors; never replace an existing destination.
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Move(stage, final); return; }
                catch (IOException error) when (attempt < 5 && new[] { 5, 32, 33 }.Contains(error.HResult & 0xffff))
                { Thread.Sleep(50 << attempt); }
            }
        }

        private byte[] WriteEditedMap(MapAsset asset)
        {
            foreach (var excluded in asset.Entities.Where(e => e.Excluded))
                if (!CanEditEntity(excluded, out var reason)) throw new InvalidOperationException(asset.Name + ": " + reason);
            // Work on a fresh resource: exporting never mutates the open project or its original entity indices.
            var copy = new YmapFile(); copy.Load(ReadResourceBytes(asset.Path, ".ymap")); copy.Name = asset.Name;
            var old = copy.AllEntities ?? new YmapEntityDef[0];
            var removed = new HashSet<int>(asset.Entities.Where(e => e.Excluded).Select(e => e.Index));
            var indices = new Dictionary<int, int>();
            var keep = new List<YmapEntityDef>();
            for (int i = 0; i < old.Length; i++) if (!removed.Contains(i)) { indices[i] = keep.Count; keep.Add(old[i]); }
            for (int i = 0; i < keep.Count; i++)
            {
                var entity = keep[i]; var def = entity._CEntityDef;
                if (def.parentIndex >= 0)
                {
                    if (!indices.TryGetValue(def.parentIndex, out var replacement)) throw new InvalidOperationException("Removing this entity would leave an orphaned LOD child.");
                    def.parentIndex = replacement;
                }
                entity._CEntityDef = def; entity.Index = i;
            }
            foreach (var entity in keep)
            {
                var def = entity._CEntityDef;
                def.numChildren = (uint)keep.Count(child => child._CEntityDef.parentIndex == entity.Index);
                entity._CEntityDef = def;
            }
            // Keep conservative original extents: shrinking without every archetype would risk invisible streamed assets.
            copy.AllEntities = keep.ToArray();
            copy.RootEntities = keep.Where(e => e._CEntityDef.parentIndex < 0).ToArray();
            var data = copy.Save();
            if (copy.SaveWarnings != null && copy.SaveWarnings.Count > 0) throw new InvalidOperationException("CodeWalker could not preserve " + asset.Name + ": " + string.Join("; ", copy.SaveWarnings));
            var check = new YmapFile(); check.Load(data);
            if ((check.AllEntities?.Length ?? 0) != keep.Count || check.CMapData.name.Hash != copy.CMapData.name.Hash || check.CMapData.parent.Hash != copy.CMapData.parent.Hash)
                throw new InvalidDataException("YMAP round-trip validation failed for " + asset.Name);
            for (int i = 0; i < keep.Count; i++)
            {
                var original = keep[i]._CEntityDef; var saved = check.AllEntities[i]._CEntityDef;
                // Pointer offsets can change on save; compare all entity values after removing that storage-only field.
                original.extensions = new Array_StructurePointer(); saved.extensions = new Array_StructurePointer();
                if (!original.Equals(saved)) throw new InvalidDataException("Entity values changed unexpectedly while saving " + asset.Name);
            }
            if (NonEntityMapXml(asset.Ymap) != NonEntityMapXml(check))
                throw new InvalidDataException("CodeWalker changed non-entity map data while saving " + asset.Name + ". Preserve its whole source file instead.");
            return data;
        }

        private static string NonEntityMapXml(YmapFile map)
        {
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(MetaXml.GetXml(map, out _));
            var root = document.DocumentElement;
            // The entity list is intentionally edited; CodeWalker regenerates exporter metadata.
            foreach (var name in new[] { "entities", "block" })
            {
                var node = root.SelectSingleNode(name);
                if (node != null) root.RemoveChild(node);
            }
            return root.OuterXml;
        }

        private static byte[] WriteEditedCollision(MapAsset asset)
        {
            var copy = new YbnFile(); copy.Load(ReadResourceBytes(asset.Path, ".ybn")); copy.Name = asset.Name;
            var leaves = FlattenBounds(copy.Bounds).ToArray();
            var remove = new HashSet<Bounds>(asset.Collisions.Where(c => c.Excluded).Select(c => leaves[c.Index]));
            PruneCollision(copy.Bounds, remove);
            var data = copy.Save(); var check = new YbnFile(); check.Load(data);
            var retained = FlattenBounds(copy.Bounds).ToArray(); var saved = FlattenBounds(check.Bounds).ToArray();
            if (retained.Length != saved.Length) throw new InvalidDataException("Collision round-trip validation failed for " + asset.Name);
            for (int i = 0; i < saved.Length; i++)
                if (retained[i].Type != saved[i].Type || !SameBox(GeometryReader.GetCollisionBounds(retained[i]), GeometryReader.GetCollisionBounds(saved[i])))
                    throw new InvalidDataException("Collision transforms changed while saving " + asset.Name);
            return data;
        }
        private static bool PruneCollision(Bounds node, HashSet<Bounds> remove)
        {
            if (remove.Contains(node)) return false;
            if (node is BoundComposite composite && composite.Children?.data_items != null)
            {
                foreach (var child in composite.Children.data_items.ToArray())
                    if (child != null && !PruneCollision(child, remove)) composite.DeleteChild(child);
                return composite.Children.data_items.Any(c => c != null);
            }
            return true;
        }
        private static bool SameBox(Box3 a, Box3 b) => Math.Abs(a.MinX - b.MinX) < 0.002 && Math.Abs(a.MinY - b.MinY) < 0.002 && Math.Abs(a.MinZ - b.MinZ) < 0.002 && Math.Abs(a.MaxX - b.MaxX) < 0.002 && Math.Abs(a.MaxY - b.MaxY) < 0.002 && Math.Abs(a.MaxZ - b.MaxZ) < 0.002;
        private static string LuaEscape(string text) => text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n");

        public void SaveProject(string path)
        {
            var project = new ProjectFile
            {
                Version = 1,
                Packs = Packs.Select(p => new ProjectPack { Name = p.Name, Root = p.Root, Color = p.Color, Enabled = p.Enabled, Assets = p.Assets.Select(a => new ProjectAsset { Path = a.Path, Digest = a.Digest, Excluded = a.Excluded, Entities = a.Entities.Where(e => e.Excluded).Select(e => e.Index).ToArray(), Bounds = a.Collisions.Where(c => c.Excluded).Select(c => c.Index).ToArray() }).ToArray() }).ToArray(),
                Decisions = decisions.ToDictionary(k => k.Key, v => v.Value)
            };
            var full = System.IO.Path.GetFullPath(path);
            var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, Json().Serialize(project), new UTF8Encoding(false));
                if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static MergeWorkspace LoadProject(string path, Action<string> progress = null)
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Project exceeds the 16 MB limit.");
            var data = Json().Deserialize<ProjectFile>(File.ReadAllText(path));
            if (data?.Version != 1 || data.Packs == null) throw new InvalidDataException("Unsupported or invalid Seam project.");
            var result = new MergeWorkspace();
            foreach (var stored in data.Packs)
            {
                if (stored.Assets == null || stored.Assets.Length == 0) continue;
                // Rebuild each logical pack independently; stream subdirectories must not become extra packs.
                var snapshot = new MergeWorkspace(); snapshot.Import(stored.Assets.Select(a => a.Path).ToArray(), progress);
                var imported = new MapPack { Id = StableId("pack:" + stored.Root), Name = stored.Name, Root = stored.Root, Enabled = stored.Enabled, Color = Regex.IsMatch(stored.Color ?? "", "^#[0-9a-fA-F]{6}$") ? stored.Color : Colors[result.Packs.Count % Colors.Length] };
                imported.Assets.AddRange(snapshot.Packs.SelectMany(p => p.Assets));
                result.Packs.Add(imported);
                foreach (var asset in imported.Assets)
                {
                    asset.Pack = imported;
                    var saved = stored.Assets.First(a => a.Path.Equals(asset.Path, StringComparison.OrdinalIgnoreCase));
                    if (asset.Digest != saved.Digest) throw new IOException("Source changed since this project was saved: " + asset.Path + ". Import the maps into a new project to review the changes.");
                    asset.Excluded = saved.Excluded;
                    foreach (var entity in asset.Entities) entity.Excluded = saved.Entities?.Contains(entity.Index) ?? false;
                    foreach (var bound in asset.Collisions) bound.Excluded = saved.Bounds?.Contains(bound.Index) ?? false;
                }
            }
            if (data.Decisions != null) foreach (var decision in data.Decisions) result.decisions[decision.Key] = decision.Value;
            result.Refresh(progress); return result;
        }
        private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 64 };
    }

    public sealed class ProjectFile
    {
        public int Version { get; set; }
        public ProjectPack[] Packs { get; set; }
        public Dictionary<string, string> Decisions { get; set; }
    }
    public sealed class ProjectPack
    {
        public string Name { get; set; }
        public string Root { get; set; }
        public string Color { get; set; }
        public bool Enabled { get; set; }
        public ProjectAsset[] Assets { get; set; }
    }
    public sealed class ProjectAsset
    {
        public string Path { get; set; }
        public string Digest { get; set; }
        public bool Excluded { get; set; }
        public int[] Entities { get; set; }
        public int[] Bounds { get; set; }
    }
}
