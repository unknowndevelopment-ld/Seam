using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using CodeWalker.GameFiles;
using Seam;

internal static class DictionaryMergeChecks
{
    public static int Run(string directory)
    {
        int passed = 0;
        Action<bool, string> check = (value, label) => { if (!value) throw new InvalidOperationException("Dictionary merge regression: " + label); passed++; Console.WriteLine("  OK " + label); };
        Directory.CreateDirectory(directory);
        var demo = MergeWorkspace.CreateDemo(Path.Combine(directory, "demo"));
        var baseType = new YtypFile(); baseType.Load(File.ReadAllBytes(Directory.GetFiles(demo[0], "*.ytyp", SearchOption.AllDirectories).Single()));
        var originals = new XmlDocument { XmlResolver = null }; originals.LoadXml(MetaXml.GetXml(baseType, out _));
        var items = originals.DocumentElement["archetypes"].ChildNodes.OfType<XmlElement>().ToArray();
        var sources = new List<MapAsset>();
        for (int p = 0; p < 2; p++)
        {
            var doc = (XmlDocument)originals.CloneNode(true); var list = doc.DocumentElement["archetypes"]; list.RemoveAll();
            foreach (int index in p == 0 ? new[] { 0, 1 } : new[] { 1, 2 })
            {
                var item = (XmlElement)doc.ImportNode(items[index], true);
                if (index == 1) item["lodDist"].SetAttribute("value", p == 0 ? "425" : "825");
                list.AppendChild(item);
            }
            doc.DocumentElement["name"].InnerText = "shared_types";
            var dependencies = doc.DocumentElement["dependencies"]; dependencies.RemoveAll();
            foreach (uint dependency in p == 0 ? new uint[] { 42, 99 } : new uint[] { 99, 123 })
            { var dependencyNode = doc.CreateElement("Item"); dependencyNode.InnerText = "hash_" + dependency.ToString("X8"); dependencies.AppendChild(dependencyNode); }
            var bytes = XmlMeta.GetRSCData(doc); var type = new YtypFile(); type.Load(bytes);
            sources.Add(Write(directory, p, "shared_types.ytyp", bytes, type));
        }
        var plan = new AutomaticMergePlan(); var outputs = DictionaryMerger.Merge(sources, plan);
        check(outputs.Count == 1, "same-name YTYP dictionaries produce one output");
        var mergedType = new YtypFile(); mergedType.Load(outputs.Single().Data);
        check(mergedType.AllArchetypes.Length == 3, "YTYP union retains all three unique archetypes");
        uint sharedHash = XmlMeta.GetHash(items[1]["name"].InnerText).Hash;
        check(mergedType.AllArchetypes.Single(a => a.Hash.Hash == sharedHash)._BaseArchetypeDef.lodDist == 425,
            "repeated archetype definitions use the top source's complete entry");
        var mergedXml = new XmlDocument(); mergedXml.LoadXml(MetaXml.GetXml(mergedType, out _));
        check(mergedXml.DocumentElement["dependencies"].SelectNodes("Item").OfType<XmlElement>().Select(item => XmlMeta.GetHash(item.InnerText).Hash).SequenceEqual(new uint[] { 42, 99, 123 }),
            "YTYP dependency arrays survive union and native binary reload");
        foreach (var selected in new[] { new { Source = 0, Hash = XmlMeta.GetHash(items[0]["name"].InnerText).Hash },
            new { Source = 0, Hash = sharedHash }, new { Source = 1, Hash = XmlMeta.GetHash(items[2]["name"].InnerText).Hash } })
        {
            var before = sources[selected.Source].Ytyp.AllArchetypes.Single(a => a.Hash.Hash == selected.Hash)._BaseArchetypeDef;
            var after = mergedType.AllArchetypes.Single(a => a.Hash.Hash == selected.Hash)._BaseArchetypeDef;
            before.extensions = new Array_StructurePointer(); after.extensions = new Array_StructurePointer();
            check(before.Equals(after), "YTYP merge preserves complete selected archetype values 0x" + selected.Hash.ToString("X8"));
        }

        var drawablePaths = Directory.GetFiles(demo[0], "*.ydr", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).Take(3).ToArray();
        var drawables = drawablePaths.Select(p => { var file = new YdrFile(); file.Load(File.ReadAllBytes(p)); return file.Drawable; }).ToArray();
        uint[] hashes = drawablePaths.Select(p => JenkHash.GenHash(Path.GetFileNameWithoutExtension(p))).ToArray();
        var dictionaries = new List<MapAsset>();
        for (int p = 0; p < 2; p++)
        {
            int[] selected = p == 0 ? new[] { 0, 1 } : new[] { 1, 2 };
            var members = selected.Select(i => Clone(drawables[i])).ToArray();
            if (p == 1) members[0].LodDistHigh = 999; // distinguish the lower-priority repeated member.
            var dictionary = new DrawableDictionary { Hashes = selected.Select(i => hashes[i]).ToArray(), Drawables = new ResourcePointerArray64<Drawable> { data_items = members } };
            dictionaries.Add(Write(directory, p, "shared_drawables.ydd", new YddFile { DrawableDict = dictionary }.Save(), null));
        }
        plan = new AutomaticMergePlan(); outputs = DictionaryMerger.Merge(dictionaries, plan);
        var mergedDictionary = new YddFile(); mergedDictionary.Load(outputs.Single().Data);
        check(mergedDictionary.Dict.Count == 3 && hashes.All(h => mergedDictionary.Dict.ContainsKey(h)), "YDD union retains all three unique drawable hashes");
        var priority = new YddFile(); priority.Load(File.ReadAllBytes(dictionaries[0].Path));
        check(mergedDictionary.Dict[hashes[1]].LodDistHigh == priority.Dict[hashes[1]].LodDistHigh, "repeated YDD members use the higher-priority model");
        for (int i = 0; i < hashes.Length; i++)
        {
            var before = drawables[i].DrawableModels.High[0].Geometries[0];
            var after = mergedDictionary.Dict[hashes[i]].DrawableModels.High[0].Geometries[0];
            check(before.VertexData.VertexBytes.SequenceEqual(after.VertexData.VertexBytes) && before.IndexBuffer.Indices.SequenceEqual(after.IndexBuffer.Indices),
                "YDD merge preserves actual vertex and index buffers 0x" + hashes[i].ToString("X8"));
            check(before.Shader.ParametersList.Hashes.SequenceEqual(after.Shader.ParametersList.Hashes), "YDD merge preserves shader bindings 0x" + hashes[i].ToString("X8"));
        }
        return passed;
    }
    private static MapAsset Write(string directory, int index, string name, byte[] bytes, YtypFile type)
    {
        string folder = Path.Combine(directory, "source-" + index); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name); File.WriteAllBytes(path, bytes);
        string digest; using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(bytes));
        return new MapAsset { Name = name, Extension = Path.GetExtension(name), Path = path, Digest = digest, Ytyp = type, Pack = new MapPack { Name = "Source " + index, Root = folder } };
    }
    private static Drawable Clone(Drawable drawable) { var file = new YdrFile(); file.Load(new YdrFile { Drawable = drawable }.Save()); return file.Drawable; }
}
