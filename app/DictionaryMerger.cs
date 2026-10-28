using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using CodeWalker.GameFiles;

namespace Seam
{
    public static class DictionaryMerger
    {
        public static List<AutomaticMergeOutput> Merge(IReadOnlyList<MapAsset> assets, AutomaticMergePlan plan, Action<string> progress = null)
        {
            var result = new List<AutomaticMergeOutput>();
            foreach (var group in assets.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var sources = group.ToList();
                var unique = sources.GroupBy(a => a.Digest, StringComparer.Ordinal).Select(g => g.First()).ToList();
                plan.DuplicateFiles += sources.Count - unique.Count;
                var first = sources[0];
                var output = new AutomaticMergeOutput { Name = first.Name, Sources = sources, Action = "Preserved" };
                progress?.Invoke("Merging assets · " + first.Name);
                if (unique.Count == 1)
                {
                    output.Data = MergeWorkspace.ReadResourceBytes(first.Path, first.Extension);
                    if (sources.Count > 1) { output.Action = "Deduplicated"; Log(plan, "Duplicate asset", first.Name, $"{sources.Count} identical copies become one streamed asset.", sources.Count - 1); }
                }
                else
                {
                    switch (first.Extension)
                    {
                        case ".ytd": output.Data = MergeTextures(unique, plan); output.Action = "Textures combined"; break;
                        case ".ydd": output.Data = MergeDrawables(unique, plan); output.Action = "Drawables combined"; break;
                        case ".ytyp": output.Data = MergeArchetypes(unique, plan); output.Action = "Archetypes combined"; break;
                        default:
                            output.Data = MergeWorkspace.ReadResourceBytes(first.Path, first.Extension); output.Action = "Priority replacement";
                            Log(plan, "Priority replacement", first.Name, "Same streamed name has incompatible complete resource variants. Used " + first.Pack.Name + " automatically; the other versions remain recorded as sources.", unique.Count - 1); break;
                    }
                }
                result.Add(output);
            }
            return result;
        }

        private static byte[] MergeTextures(List<MapAsset> sources, AutomaticMergePlan plan)
        {
            var textures = new Dictionary<uint, Texture>(); int overridden = 0;
            foreach (var source in sources)
            {
                var file = new YtdFile(); file.Load(MergeWorkspace.ReadResourceBytes(source.Path, source.Extension));
                if (file.TextureDict?.Textures?.data_items == null) throw new InvalidDataException("Cannot read texture dictionary " + source.Name);
                foreach (var texture in file.TextureDict.Textures.data_items)
                    if (texture != null && !textures.ContainsKey(texture.NameHash)) textures[texture.NameHash] = texture;
                    else if (texture != null) overridden++;
            }
            if (textures.Count > ushort.MaxValue) throw new InvalidDataException("Merged texture dictionary exceeds the format's entry limit.");
            var dictionary = new TextureDictionary(); dictionary.BuildFromTextureList(textures.Values.ToList());
            var data = new YtdFile { TextureDict = dictionary }.Save();
            var check = new YtdFile(); check.Load(data);
            if (check.TextureDict?.Textures?.Count != textures.Count) throw new InvalidDataException("Texture dictionary round-trip count mismatch.");
            foreach (var entry in textures)
            {
                var saved = check.TextureDict.Lookup(entry.Key); var expected = entry.Value;
                if (saved == null || saved.Width != expected.Width || saved.Height != expected.Height || saved.Format != expected.Format || saved.Levels != expected.Levels || !BytesEqual(saved.Data?.FullData, expected.Data?.FullData))
                    throw new InvalidDataException("Texture data changed while combining dictionaries.");
            }
            Log(plan, "Textures combined", sources[0].Name, $"Combined {sources.Count} dictionaries into {textures.Count} texture entries. {overridden} repeated names use map priority.", textures.Count);
            return data;
        }

        private static byte[] MergeDrawables(List<MapAsset> sources, AutomaticMergePlan plan)
        {
            var models = new SortedDictionary<uint, Drawable>(); int overridden = 0;
            foreach (var source in sources)
            {
                var file = new YddFile(); file.Load(MergeWorkspace.ReadResourceBytes(source.Path, source.Extension));
                if (file.Dict == null) throw new InvalidDataException("Cannot read drawable dictionary " + source.Name);
                foreach (var entry in file.Dict) if (!models.ContainsKey(entry.Key)) models.Add(entry.Key, entry.Value); else overridden++;
            }
            if (models.Count > ushort.MaxValue) throw new InvalidDataException("Merged drawable dictionary exceeds the format's entry limit.");
            var dictionary = new DrawableDictionary { Hashes = models.Keys.ToArray(), Drawables = new ResourcePointerArray64<Drawable> { data_items = models.Values.ToArray() } };
            var data = new YddFile { DrawableDict = dictionary }.Save();
            var check = new YddFile(); check.Load(data);
            if (check.Dict == null || !check.Dict.Keys.OrderBy(k => k).SequenceEqual(models.Keys)) throw new InvalidDataException("Drawable dictionary round-trip validation failed.");
            foreach (var entry in models)
            {
                // Rebuilding a member independently verifies its full resource graph, including embedded textures.
                var expected = new YdrFile { Drawable = entry.Value }.Save();
                var saved = new YdrFile { Drawable = check.Dict[entry.Key] }.Save();
                if (!BytesEqual(expected, saved)) throw new InvalidDataException("Drawable data changed while combining dictionaries.");
            }
            Log(plan, "Drawables combined", sources[0].Name, $"Combined {sources.Count} dictionaries into {models.Count} drawable entries. {overridden} repeated names use map priority.", models.Count);
            return data;
        }

        private static byte[] MergeArchetypes(List<MapAsset> sources, AutomaticMergePlan plan)
        {
            // Generic Meta XML preserves MLO rooms/portals, extensions and dependencies;
            // YtypFile.Save currently clears the dependency array.
            var docs = sources.Select(a => Parse(MetaXml.GetXml(a.Ytyp ?? LoadYtyp(a), out _))).ToArray();
            var merged = docs[0]; var root = merged.DocumentElement;
            var archetypes = Ensure(merged, root, "archetypes");
            var known = new HashSet<string>(archetypes.ChildNodes.OfType<XmlElement>().Select(ArchetypeKey), StringComparer.OrdinalIgnoreCase);
            int added = 0, overridden = 0;
            foreach (var doc in docs.Skip(1))
            {
                foreach (var item in doc.DocumentElement.SelectNodes("archetypes/Item").OfType<XmlElement>())
                    if (known.Add(ArchetypeKey(item))) { archetypes.AppendChild(merged.ImportNode(item, true)); added++; } else overridden++;
                foreach (var tag in new[] { "extensions", "compositeEntityTypes" })
                {
                    var target = Ensure(merged, root, tag);
                    var seen = new HashSet<string>(target.ChildNodes.OfType<XmlElement>().Select(Canonical), StringComparer.Ordinal);
                    foreach (var item in doc.DocumentElement.SelectNodes(tag + "/Item").OfType<XmlElement>())
                        if (seen.Add(Canonical(item))) target.AppendChild(merged.ImportNode(item, true));
                }
                var dependency = doc.DocumentElement.SelectSingleNode("dependencies");
                if (dependency != null)
                {
                    var target = Ensure(merged, root, "dependencies");
                    // Meta hash arrays use Item elements, not a whitespace-separated text array.
                    var values = new HashSet<uint>(target.SelectNodes("Item").OfType<XmlElement>().Select(item => XmlMeta.GetHash(item.InnerText).Hash));
                    foreach (var item in dependency.SelectNodes("Item").OfType<XmlElement>())
                        if (values.Add(XmlMeta.GetHash(item.InnerText).Hash)) target.AppendChild(merged.ImportNode(item, true));
                }
            }
            var data = XmlMeta.GetRSCData(merged); var check = new YtypFile(); check.Load(data);
            var expected = archetypes.SelectNodes("Item").Count;
            if (check.AllArchetypes?.Length != expected) throw new InvalidDataException("Archetype dictionary round-trip validation failed.");
            var checkDoc = Parse(MetaXml.GetXml(check, out _));
            if (Canonical(root) != Canonical(checkDoc.DocumentElement))
            {
                string field = root.ChildNodes.OfType<XmlElement>().Select(e => e.Name).FirstOrDefault(n =>
                    checkDoc.DocumentElement[n] == null || Canonical(root[n]) != Canonical(checkDoc.DocumentElement[n]));
                throw new InvalidDataException("Archetype metadata changed while combining dictionaries: " + (field ?? "root attributes") + ".");
            }
            Log(plan, "Archetypes combined", sources[0].Name, $"Combined {sources.Count} YTYP files into {expected} archetypes, including nested MLO data. {overridden} repeated names use map priority.", added);
            return data;
        }
        private static YtypFile LoadYtyp(MapAsset asset) { var file = new YtypFile(); file.Load(MergeWorkspace.ReadResourceBytes(asset.Path, asset.Extension)); return file; }
        private static string ArchetypeKey(XmlElement node) => XmlMeta.GetHash(node.SelectSingleNode("name")?.InnerText ?? "").Hash.ToString(CultureInfo.InvariantCulture);
        private static XmlElement Ensure(XmlDocument doc, XmlElement root, string name)
        {
            var result = root.SelectSingleNode(name) as XmlElement;
            if (result == null) { result = doc.CreateElement(name); root.AppendChild(result); }
            return result;
        }
        private static XmlDocument Parse(string xml) { var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(xml); return doc; }
        private static string Canonical(XmlElement node)
        {
            var result = new StringBuilder(); AppendCanonical(node, result); return result.ToString();
        }
        private static void AppendCanonical(XmlElement node, StringBuilder result)
        {
            result.Append('<').Append(node.Name);
            foreach (var attribute in node.Attributes.Cast<XmlAttribute>().OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                var value = attribute.Value;
                if ((attribute.Name == "x" || attribute.Name == "y" || attribute.Name == "z" || attribute.Name == "w") && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var coordinate))
                    value = coordinate.ToString("R", CultureInfo.InvariantCulture);
                else if (attribute.Name == "value" && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) value = number.ToString("G29", CultureInfo.InvariantCulture);
                result.Append('|').Append(attribute.Name).Append('=').Append(value);
            }
            result.Append('>');
            var children = node.ChildNodes.OfType<XmlElement>().ToArray();
            if (children.Length == 0) result.Append(string.Join(" ", node.InnerText.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)));
            else foreach (var child in children) AppendCanonical(child, result);
            result.Append("</").Append(node.Name).Append('>');
        }
        private static bool BytesEqual(byte[] left, byte[] right) => left != null && right != null && left.SequenceEqual(right);
        internal static void Log(AutomaticMergePlan plan, string kind, string title, string detail, int count = 0) => plan.Operations.Add(new MergeOperation { Kind = kind, Title = title, Detail = detail, Count = count });
    }
}
