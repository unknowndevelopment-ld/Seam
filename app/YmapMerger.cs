using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using CodeWalker.GameFiles;

namespace Seam
{
    // Merge the metadata tree, not just CEntityDef arrays: MLO fields, extensions,
    // dictionary references and map-specific sections must survive serialization.
    public static class YmapMerger
    {
        private static readonly HashSet<string> Appendable = new HashSet<string>(StringComparer.Ordinal)
        { "boxOccluders", "occludeModels", "timeCycleModifiers", "carGenerators", "physicsDictionaries" };
        private static readonly HashSet<string> Special = new HashSet<string>(StringComparer.Ordinal)
        { "name", "parent", "block", "flags", "contentFlags", "entities", "streamingExtentsMin", "streamingExtentsMax", "entitiesExtentsMin", "entitiesExtentsMax" };

        public static List<AutomaticMergeOutput> Merge(IReadOnlyList<MapAsset> assets, AutomaticMergePlan plan, Action<string> progress = null, bool consolidateIndependentMaps = true)
        {
            var result = new List<AutomaticMergeOutput>();
            var standalone = new List<MapAsset>();
            var referenced = new HashSet<uint>(assets.Where(a => a.Ymap != null && a.Ymap.CMapData.parent.Hash != 0).Select(a => a.Ymap.CMapData.parent.Hash));
            foreach (var group in assets.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var sources = group.ToList();
                progress?.Invoke("Merging map placements · " + sources[0].Name);
                if (sources.Any(a => a.Ymap == null)) throw new InvalidDataException("Cannot merge unreadable YMAP: " + sources.First(a => a.Ymap == null).Name);
                if (sources.Count == 1)
                {
                    if (consolidateIndependentMaps && EligibleStandalone(sources[0], referenced)) standalone.Add(sources[0]);
                    else result.Add(Original(sources[0]));
                    continue;
                }
                var distinct = sources.GroupBy(a => a.Digest ?? Convert.ToBase64String(Bytes(a)), StringComparer.Ordinal).Select(g => g.First()).ToList();
                plan.DuplicateFiles += sources.Count - distinct.Count;
                if (distinct.Count == 1)
                {
                    result.Add(Original(sources[0], sources));
                    Log(plan, "Deduplicated", sources[0].Name, "Identical map resources are written once.", sources.Count - 1);
                    continue;
                }
                try
                {
                    int duplicates;
                    var output = Combine(distinct, sources[0].Name, out duplicates);
                    output.Sources = sources;
                    result.Add(output);
                    plan.MergedMaps += distinct.Count - 1;
                    plan.DuplicateEntities += duplicates;
                    Log(plan, output.Action, output.Name, output.Action == "Replacement edits merged"
                        ? "Combined GTA replacement edits by entity GUID. Placements removed by either variant stay removed; vertical moves of at least five metres below an unchanged placement stay lowered. Other conflicting edits follow map priority. External LOD indices remain unchanged; shared occluders retain removal edits. No pristine game baseline is available: this rule treats missing original GUIDs as deletions."
                        : $"Combined {distinct.Count} versions, preserving complete entity and map metadata; removed {duplicates} identical standalone placements.", distinct.Count);
                }
                catch (InvalidDataException error)
                {
                    result.Add(Original(sources[0], sources));
                    Log(plan, "Priority fallback", sources[0].Name, "Retained the highest-priority original YMAP because its variants cannot be safely combined: " + error.Message, sources.Count - 1);
                }
            }
            if (standalone.Count > 1)
            {
                var used = new HashSet<string>(assets.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
                string name = "seam_combined.ymap";
                for (int suffix = 2; used.Contains(name); suffix++) name = "seam_combined_" + suffix + ".ymap";
                try
                {
                    int duplicates;
                    var output = Combine(standalone, name, out duplicates);
                    result.Add(output);
                    plan.MergedMaps += standalone.Count - 1;
                    plan.DuplicateEntities += duplicates;
                    Log(plan, "Merged YMAP", name, $"Combined {standalone.Count} independent custom placement maps and removed {duplicates} identical placements. Linked, replacement and MLO maps retain their original names.", standalone.Count);
                }
                catch (InvalidDataException error)
                {
                    result.AddRange(standalone.Select(a => Original(a)));
                    Log(plan, "Preserved maps", "Independent map metadata", "Original names and bytes retained because consolidation was not lossless: " + error.Message, standalone.Count);
                }
            }
            else result.AddRange(standalone.Select(a => Original(a)));
            return result;
        }

        private static bool EligibleStandalone(MapAsset asset, HashSet<uint> referenced)
        {
            var map = asset.Ymap;
            if (map.CMapData.parent.Hash != 0 || map.CMapData.flags != 0 || referenced.Contains(map.CMapData.name.Hash) ||
                (map.MloEntities?.Length ?? 0) != 0 || (map.AllEntities?.Length ?? 0) == 0) return false;
            if (map.AllEntities.Any(e => e.IsMlo || e._CEntityDef.parentIndex >= 0 || e._CEntityDef.numChildren != 0 || e._CEntityDef.guid != 0)) return false;
            string stem = Path.GetFileNameWithoutExtension(asset.Name).ToLowerInvariant();
            if (stem.StartsWith("hei_") || stem.StartsWith("hi@") || stem.Contains("_strm_") || stem.Contains("_occl_") || stem.EndsWith("_lod")) return false;
            // A custom client script may RequestIpl by source name. Its maps remain named.
            if (!string.IsNullOrEmpty(asset.Pack?.Root) && Directory.Exists(asset.Pack.Root))
                if (Directory.EnumerateFiles(asset.Pack.Root, "*.lua", SearchOption.AllDirectories).Any(p =>
                    !Path.GetFileName(p).Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(p).Equals("__resource.lua", StringComparison.OrdinalIgnoreCase))) return false;
            var document = Document(asset);
            foreach (XmlElement section in document.DocumentElement.ChildNodes.OfType<XmlElement>())
                if (!Special.Contains(section.Name) && !Appendable.Contains(section.Name) && HasData(section)) return false;
            return true;
        }

        private sealed class EntityRecord
        {
            public XmlElement Element;
            public int Source;
            public int OriginalIndex;
            public int NewIndex;
            public int OriginalParent;
            public bool ExternalParent;
        }

        internal static AutomaticMergeOutput Combine(List<MapAsset> sources, string name, out int duplicateCount, Dictionary<string, XmlElement> sectionsOverride = null)
        {
            var docs = sources.Select(Document).ToArray();
            var merged = (XmlDocument)docs[0].CloneNode(true);
            var root = merged.DocumentElement;
            duplicateCount = 0;
            uint parent = sources[0].Ymap.CMapData.parent.Hash;
            if (sources.Any(s => s.Ymap.CMapData.parent.Hash != parent)) throw new InvalidDataException("Map-level parent links differ.");
            var replacementWinners = ReplacementWinners(docs, sources, name);
            bool replacement = replacementWinners != null;
            if (!replacement && parent != 0 && sources.Any(s => s.Ymap.AllEntities?.Any(e => e.LodInParentYmap) == true))
                throw new InvalidDataException("External entity indices require their complete parent-map version.");
            // Container LOD / instanced-prop records can hold indexed entity relationships.
            if (docs.Any(d => HasData(d.DocumentElement["containerLods"]) || HasData(d.DocumentElement["instancedData"]?["PropInstanceList"])))
                throw new InvalidDataException("Indexed container LOD or instanced prop records are preserved in their original map.");

            var entities = new List<EntityRecord>();
            var seen = new Dictionary<string, EntityRecord>(StringComparer.Ordinal);
            var remap = new Dictionary<string, EntityRecord>(StringComparer.Ordinal);
            var sourceChildCounts = sources.Select(source => (source.Ymap.AllEntities ?? new YmapEntityDef[0])
                .Where(child => !child.LodInParentYmap && child._CEntityDef.parentIndex >= 0)
                .GroupBy(child => child._CEntityDef.parentIndex).ToDictionary(group => group.Key, group => group.Count())).ToArray();
            for (int s = 0; s < docs.Length; s++)
            {
                var items = docs[s].DocumentElement["entities"]?.ChildNodes.OfType<XmlElement>().ToArray() ?? new XmlElement[0];
                if (items.Length != (sources[s].Ymap.AllEntities?.Length ?? 0)) throw new InvalidDataException("Entity pointer order could not be established.");
                bool mloSeen = false;
                for (int i = 0; i < items.Length; i++)
                {
                    bool mlo = items[i].GetAttribute("type") == "CMloInstanceDef";
                    if (mlo) mloSeen = true;
                    else if (mloSeen) throw new InvalidDataException("Interleaved ordinary/MLO entity indices must retain their original ordering.");
                    var e = (XmlElement)merged.ImportNode(items[i], true);
                    if (replacement && (!replacementWinners.TryGetValue(ReadUInt(e["guid"]), out int winner) || winner != s)) continue;
                    int p = ReadInt(e["parentIndex"], -1);
                    uint children = ReadUInt(e["numChildren"]);
                    bool external = sources[s].Ymap.AllEntities[i].LodInParentYmap;
                    if ((!external && p >= items.Length) || p < -1) throw new InvalidDataException("Entity parent index lies outside the local map.");
                    bool localRoot = p < 0 && children == 0 && !sourceChildCounts[s].ContainsKey(i);
                    var key = Canonical(e);
                    if (localRoot && seen.TryGetValue(key, out var existing))
                    {
                        remap[s + ":" + i] = existing;
                        duplicateCount++;
                        continue;
                    }
                    var record = new EntityRecord { Element = e, Source = s, OriginalIndex = i, OriginalParent = p, ExternalParent = external };
                    entities.Add(record);
                    remap[s + ":" + i] = record;
                    if (localRoot) seen[key] = record;
                }
            }
            // CodeWalker exposes and reloads ordinary entities before MLO instances.
            entities = entities.OrderBy(e => e.Element.GetAttribute("type") == "CMloInstanceDef" ? 1 : 0).ToList();
            for (int i = 0; i < entities.Count; i++) entities[i].NewIndex = i;
            var entityNode = Ensure(root, "entities"); entityNode.RemoveAll();
            foreach (var e in entities)
            {
                if (e.OriginalParent >= 0 && !e.ExternalParent)
                {
                    if (!remap.TryGetValue(e.Source + ":" + e.OriginalParent, out var target)) throw new InvalidDataException("Missing local parent definition.");
                    Ensure(e.Element, "parentIndex").SetAttribute("value", target.NewIndex.ToString(CultureInfo.InvariantCulture));
                }
                entityNode.AppendChild(e.Element);
            }
            var mergedChildCounts = entities.Where(child => child.OriginalParent >= 0 && !child.ExternalParent)
                .GroupBy(child => remap[child.Source + ":" + child.OriginalParent]).ToDictionary(group => group.Key, group => group.Count());
            foreach (var e in entities)
            {
                mergedChildCounts.TryGetValue(e, out int actualChildren);
                // Validate original linkage rather than silently fixing incomplete external branches.
                sourceChildCounts[e.Source].TryGetValue(e.OriginalIndex, out int sourceChildren);
                if (ReadUInt(e.Element["numChildren"]) != sourceChildren) throw new InvalidDataException("An incomplete or external LOD branch would lose its indices.");
                Ensure(e.Element, "numChildren").SetAttribute("value", actualChildren.ToString(CultureInfo.InvariantCulture));
            }

            foreach (var field in new[] { "streamingExtentsMin", "streamingExtentsMax", "entitiesExtentsMin", "entitiesExtentsMax" })
                MergeExtents(root, docs, field, field.EndsWith("Min", StringComparison.Ordinal));
            foreach (var field in new[] { "flags", "contentFlags" })
                Ensure(root, field).SetAttribute("value", docs.Aggregate(0u, (value, doc) => value | ReadUInt(doc.DocumentElement[field])).ToString(CultureInfo.InvariantCulture));

            foreach (string field in docs.SelectMany(d => d.DocumentElement.ChildNodes.OfType<XmlElement>().Select(e => e.Name)).Distinct())
            {
                if (Special.Contains(field)) continue;
                var sections = docs.Select(d => d.DocumentElement[field]).Where(e => e != null).ToArray();
                var target = Ensure(root, field);
                if (sectionsOverride != null && sectionsOverride.TryGetValue(field, out var sectionOverride))
                {
                    root.ReplaceChild(merged.ImportNode(sectionOverride, true), target);
                    continue;
                }
                if (Appendable.Contains(field))
                {
                    if (replacement && field == "boxOccluders")
                    {
                        MergeReplacementBoxes(target, docs);
                        continue;
                    }
                    var unique = new HashSet<string>(StringComparer.Ordinal);
                    bool intersect = replacement && (field == "boxOccluders" || field == "occludeModels");
                    var otherKeys = intersect ? docs.Select(d => new HashSet<string>((d.DocumentElement[field]?.ChildNodes.OfType<XmlElement>() ?? Enumerable.Empty<XmlElement>()).Select(Canonical), StringComparer.Ordinal)).ToArray() : null;
                    foreach (XmlNode old in target.ChildNodes.Cast<XmlNode>().ToArray()) target.RemoveChild(old);
                    foreach (var section in sections)
                        foreach (var item in section.ChildNodes.OfType<XmlElement>())
                            if ((!intersect || otherKeys.All(keys => keys.Contains(Canonical(item)))) && unique.Add(Canonical(item))) target.AppendChild(merged.ImportNode(item, true));
                }
                else
                {
                    var populated = sections.Where(HasData).ToArray();
                    if (populated.Select(Canonical).Distinct(StringComparer.Ordinal).Count() > 1)
                        throw new InvalidDataException("Parallel or unknown metadata section differs: " + field + ".");
                    if (populated.Length > 0)
                    {
                        var sectionCopy = merged.ImportNode(populated[0], true);
                        root.ReplaceChild(sectionCopy, target);
                    }
                }
            }
            string stem = Path.GetFileNameWithoutExtension(name);
            Ensure(root, "name").InnerText = stem;
            var block = Ensure(root, "block"); Ensure(block, "name").InnerText = stem;
            Ensure(block, "exportedBy").InnerText = "Seam";
            var bytes = XmlMeta.GetRSCData(merged);
            if (bytes == null) throw new InvalidDataException("CodeWalker could not compile the combined metadata.");
            var check = new YmapFile(); check.Load(bytes);
            if ((check.AllEntities?.Length ?? 0) != entities.Count || check.CMapData.parent.Hash != parent || check.CMapData.name.Hash != JenkHash.GenHash(stem))
                throw new InvalidDataException("Combined map failed binary entity/name validation.");
            var loaded = new XmlDocument { XmlResolver = null }; loaded.LoadXml(MetaXml.GetXml(check, out _));
            if (Canonical(merged.DocumentElement) != Canonical(loaded.DocumentElement))
            {
                string changed = merged.DocumentElement.ChildNodes.OfType<XmlElement>().Select(e => e.Name)
                    .FirstOrDefault(field => Canonical(merged.DocumentElement[field]) != Canonical(loaded.DocumentElement[field]));
                throw new InvalidDataException("Metadata changed during the combined map binary round trip: " + (changed ?? "root attributes") + ".");
            }
            return new AutomaticMergeOutput { Name = name, Data = bytes, Sources = sources.ToList(), Action = replacement ? "Replacement edits merged" : "Merged YMAP" };
        }

        private static Dictionary<uint, int> ReplacementWinners(XmlDocument[] docs, List<MapAsset> sources, string name)
        {
            if (!Regex.IsMatch(Path.GetFileNameWithoutExtension(name), @"^(?:hei_)?[a-z]{2,5}\d{1,2}_(?:\d+[a-z]?|occl)(?:_[a-z0-9]+)+$", RegexOptions.IgnoreCase)) return null;
            var items = docs.Select(d => d.DocumentElement["entities"]?.ChildNodes.OfType<XmlElement>().ToArray() ?? new XmlElement[0]).ToArray();
            if (items.Any(list => list.Any(e => ReadUInt(e["guid"]) == 0 || e.GetAttribute("type") != "CEntityDef" || ReadUInt(e["numChildren"]) != 0)))
                throw new InvalidDataException("Replacement map mixes custom instances or local LOD branches; removal edits cannot be inferred.");
            if (sources.Any(a => a.Ymap.AllEntities?.Any(e => e._CEntityDef.parentIndex >= 0 && !e.LodInParentYmap) == true))
                throw new InvalidDataException("Replacement map contains local entity indices.");
            if (items.Any(list => list.Select(e => ReadUInt(e["guid"])).Distinct().Count() != list.Length))
                throw new InvalidDataException("Replacement GUIDs are not unique.");
            var byGuid = items.Select(list => list.ToDictionary(e => ReadUInt(e["guid"]))).ToArray();
            var common = new HashSet<uint>(byGuid[0].Keys);
            foreach (var dict in byGuid.Skip(1)) common.IntersectWith(dict.Keys);
            if (items.Any(list => list.Length > 0) && (common.Count == 0 || common.Count < items.Min(list => list.Length) * 0.8))
                throw new InvalidDataException("Replacement variants do not share enough original entity GUIDs to combine removal edits.");
            if (items.All(list => list.Length == 0) && !docs.Any(d => HasData(d.DocumentElement["boxOccluders"]) || HasData(d.DocumentElement["occludeModels"]))) return null;
            var winners = new Dictionary<uint, int>();
            foreach (uint guid in common)
            {
                var first = byGuid[0][guid];
                if (byGuid.Any(dict => Canonical(dict[guid]["archetypeName"]) != Canonical(first["archetypeName"]) ||
                    Canonical(dict[guid]["parentIndex"]) != Canonical(first["parentIndex"]) ||
                    ((ReadUInt(dict[guid]["flags"]) ^ ReadUInt(first["flags"])) & 8) != 0))
                    throw new InvalidDataException("Replacement GUIDs have incompatible archetypes or parent links.");
                int winner = 0;
                for (int s = 1; s < byGuid.Length; s++)
                {
                    var current = byGuid[winner][guid]; var candidate = byGuid[s][guid];
                    // Recognize the existing map authors' hide-below-ground edit, rather
                    // than restoring the original shell over a separately placed MLO.
                    if (Coordinate(current, "x") == Coordinate(candidate, "x") && Coordinate(current, "y") == Coordinate(candidate, "y") &&
                        Coordinate(current, "z") - Coordinate(candidate, "z") >= 5 &&
                        new[] { "rotation", "scaleXY", "scaleZ" }.All(field => Canonical(current[field]) == Canonical(candidate[field]))) winner = s;
                }
                winners[guid] = winner;
            }
            return winners;
        }

        private static double Coordinate(XmlElement entity, string axis) => double.Parse(entity["position"].GetAttribute(axis), CultureInfo.InvariantCulture);

        private static void MergeReplacementBoxes(XmlElement target, XmlDocument[] docs)
        {
            string Key(XmlElement box) => string.Concat(box.ChildNodes.OfType<XmlElement>().Where(e => e.Name != "iCenterZ").Select(Canonical));
            var variants = docs.Select(d => (d.DocumentElement["boxOccluders"]?.ChildNodes.OfType<XmlElement>() ?? Enumerable.Empty<XmlElement>())
                .GroupBy(Key).ToDictionary(g => g.Key, g => g.OrderBy(e => ReadInt(e["iCenterZ"])).First())).ToArray();
            foreach (XmlNode child in target.ChildNodes.Cast<XmlNode>().ToArray()) target.RemoveChild(child);
            foreach (var box in variants[0])
            {
                if (!variants.All(v => v.ContainsKey(box.Key))) continue;
                // Occluder centres use quarter-metre units. Preserve an author's
                // lowered occluder instead of putting it back across an MLO window.
                var selected = variants.Select(v => v[box.Key]).OrderBy(e => ReadInt(e["iCenterZ"])).First();
                target.AppendChild(target.OwnerDocument.ImportNode(selected, true));
            }
        }

        private static void MergeExtents(XmlElement root, XmlDocument[] docs, string field, bool min)
        {
            var target = Ensure(root, field);
            foreach (var axis in new[] { "x", "y", "z" })
            {
                var values = docs.Select(d => d.DocumentElement[field]?.GetAttribute(axis)).Where(v => !string.IsNullOrEmpty(v))
                    .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (values.Length > 0) target.SetAttribute(axis, (min ? values.Min() : values.Max()).ToString("R", CultureInfo.InvariantCulture));
            }
        }

        internal static XmlDocument Document(MapAsset asset)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(MetaXml.GetXml(asset.Ymap, out _));
            if (doc.DocumentElement?.Name != "CMapData") throw new InvalidDataException("Unsupported YMAP metadata root.");
            if (doc.SelectSingleNode("//error") != null) throw new InvalidDataException("CodeWalker cannot decode every metadata structure in this map.");
            return doc;
        }

        private static bool HasData(XmlElement element)
        {
            if (element == null) return false;
            if (element.ChildNodes.OfType<XmlElement>().Any(HasData)) return true;
            if (element.ChildNodes.OfType<XmlElement>().Any(e => e.Name == "Item")) return true;
            if (!string.IsNullOrWhiteSpace(string.Concat(element.ChildNodes.OfType<XmlText>().Select(t => t.Value)))) return true;
            return element.Attributes.Cast<XmlAttribute>().Any(a => a.Name != "itemType" && a.Name != "type" && a.Value != "0" && a.Value != "0.0" && a.Value.Length != 0);
        }

        internal static string Canonical(XmlElement element)
        {
            if (element == null) return "";
            var builder = new StringBuilder();
            AppendCanonical(element, builder);
            return builder.ToString();
        }
        private static void AppendCanonical(XmlElement element, StringBuilder builder)
        {
            // Old exporter schemas used a different name for an empty LOD-light
            // hash array. Empty/default structures carry no data to lose.
            if (!HasData(element)) { builder.Append('<').Append(element.Name).Append("/>"); return; }
            builder.Append('<').Append(element.Name);
            foreach (var attribute in element.Attributes.Cast<XmlAttribute>().OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                var value = attribute.Value;
                if ((attribute.Name == "x" || attribute.Name == "y" || attribute.Name == "z" || attribute.Name == "w") && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var coordinate))
                    value = coordinate.ToString("R", CultureInfo.InvariantCulture);
                else if (attribute.Name == "value" && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
                    value = numeric.ToString("G29", CultureInfo.InvariantCulture);
                builder.Append('|').Append(attribute.Name).Append('=').Append(value);
            }
            builder.Append('>');
            var elements = element.ChildNodes.OfType<XmlElement>().ToArray();
            if (elements.Length == 0) builder.Append(string.Join(" ", element.InnerText.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)));
            else foreach (var child in elements) AppendCanonical(child, builder);
            builder.Append("</").Append(element.Name).Append('>');
        }
        private static XmlElement Ensure(XmlElement parent, string name)
        {
            var found = parent[name];
            if (found != null) return found;
            var node = parent.OwnerDocument.CreateElement(name); parent.AppendChild(node); return node;
        }
        private static int ReadInt(XmlElement node, int fallback = 0) => int.TryParse(node?.GetAttribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        private static uint ReadUInt(XmlElement node) => uint.TryParse(node?.GetAttribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        private static byte[] Bytes(MapAsset asset) => MergeWorkspace.ReadResourceBytes(asset.Path, ".ymap");
        private static AutomaticMergeOutput Original(MapAsset asset, List<MapAsset> sources = null) => new AutomaticMergeOutput
        { Name = asset.Name, Data = Bytes(asset), Sources = sources ?? new List<MapAsset> { asset }, Action = "Preserved YMAP" };
        private static void Log(AutomaticMergePlan plan, string kind, string title, string detail, int count) =>
            plan.Operations.Add(new MergeOperation { Kind = kind, Title = title, Detail = detail, Count = count });
    }
}
