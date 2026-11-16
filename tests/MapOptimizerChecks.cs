using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using CodeWalker.GameFiles;
using Seam;

internal static class MapOptimizerChecks
{
    public static int Run(string directory)
    {
        int passed = 0;
        Action<bool, string> check = (ok, message) => { if (!ok) throw new InvalidOperationException("Map optimizer: " + message); passed++; Console.WriteLine("  OK " + message); };
        Directory.CreateDirectory(directory);
        var roots = MergeWorkspace.CreateDemo(Path.Combine(directory, "sources"));
        var template = Document(File.ReadAllBytes(Path.Combine(roots[0], "stream", "quay_district.ymap")));

        // ---- Direct plan checks: empty, referenced, scripted and duplicate maps ----
        var empty = (XmlDocument)template.CloneNode(true);
        Name(empty, "seam_empty");
        empty.DocumentElement["entities"].RemoveAll();
        byte[] emptyBytes = XmlMeta.GetRSCData(empty);
        check(emptyBytes != null && emptyBytes.Length > 0, "empty YMAP fixture compiles");

        var child = (XmlDocument)template.CloneNode(true);
        Name(child, "seam_child");
        child.DocumentElement["parent"].InnerText = "seam_empty";
        byte[] childBytes = XmlMeta.GetRSCData(child);

        var referencedPlan = new AutomaticMergePlan();
        var referencedEmpty = new AutomaticMergeOutput { Name = "seam_empty.ymap", Data = emptyBytes };
        referencedPlan.Outputs.Add(referencedEmpty);
        referencedPlan.Outputs.Add(new AutomaticMergeOutput { Name = "seam_child.ymap", Data = childBytes });
        var assets = new List<MapAsset> { new MapAsset { Name = "seam_child.ymap", Ymap = Read(childBytes) } };
        var referenced = MapOptimizer.Apply(referencedPlan, assets, Rules(), null);
        check(!referencedEmpty.Removed && referenced.EmptyMaps == 0, "a map referenced as a parent is never dropped or rewritten");

        var emptyPlan = new AutomaticMergePlan();
        var emptyOutput = new AutomaticMergeOutput { Name = "seam_empty.ymap", Data = emptyBytes };
        emptyPlan.Outputs.Add(emptyOutput);
        var removed = MapOptimizer.Apply(emptyPlan, new List<MapAsset>(), Rules(), null);
        check(emptyOutput.Removed && removed.EmptyMaps == 1 && removed.SavedBytes == emptyBytes.Length &&
              emptyPlan.RemovedOutputNames.Contains("seam_empty.ymap"), "an unused empty map is marked for removal and counted");

        string scriptPack = Path.Combine(directory, "scripted-resource");
        Directory.CreateDirectory(scriptPack);
        File.WriteAllText(Path.Combine(scriptPack, "client.lua"), "RequestIpl('seam_empty')\n");
        var scriptedPlan = new AutomaticMergePlan();
        var scriptedOutput = new AutomaticMergeOutput
        {
            Name = "seam_empty.ymap", Data = emptyBytes,
            Sources = new List<MapAsset> { new MapAsset { Name = "seam_empty.ymap", Pack = new MapPack { Root = scriptPack } } }
        };
        scriptedPlan.Outputs.Add(scriptedOutput);
        var scripted = MapOptimizer.Apply(scriptedPlan, scriptedOutput.Sources, Rules(), null);
        check(!scriptedOutput.Removed && scripted.EmptyMaps == 0, "map packs with client scripts keep their maps untouched");

        var duplicate = (XmlDocument)template.CloneNode(true);
        Name(duplicate, "seam_duplicate");
        var items = duplicate.DocumentElement["entities"];
        items.AppendChild(duplicate.ImportNode(items.ChildNodes[0], true));
        var lod = (XmlElement)items.ChildNodes[1];
        lod["lodLevel"].InnerText = "LODTYPES_DEPTH_LOD";
        lod["lodDist"].SetAttribute("value", "900");
        byte[] duplicateBytes = XmlMeta.GetRSCData(duplicate);
        var optimizePlan = new AutomaticMergePlan();
        var duplicateOutput = new AutomaticMergeOutput { Name = "seam_duplicate.ymap", Data = duplicateBytes };
        optimizePlan.Outputs.Add(duplicateOutput);
        var optimized = MapOptimizer.Apply(optimizePlan, new List<MapAsset>(),
            new MapOptimizerOptions { Enabled = true, RemoveDuplicatePlacements = true, RemoveEmptyMaps = true, LimitDrawDistance = true, MaxDrawDistance = 300 }, null);
        check(optimized.Applied && optimized.DuplicatePlacements == 1, "a byte-identical standalone placement is removed once");
        check(optimized.CappedDrawDistances == 5, "ordinary HD props above the limit are capped");
        var optimizedXml = Document(duplicateOutput.Data);
        var optimizedItems = optimizedXml.SelectNodes("/CMapData/entities/Item").Cast<XmlElement>().ToArray();
        check(optimizedItems.Length == 5 && Read(duplicateOutput.Data).AllEntities.Length == 5, "optimized map keeps entity indices consistent");
        check(optimizedItems.Count(i => i["lodDist"].GetAttribute("value") == "300") == 4, "capped draw distances persist through the binary round trip");
        check(optimizedItems.Single(i => i["lodLevel"].InnerText == "LODTYPES_DEPTH_LOD")["lodDist"].GetAttribute("value") == "900",
            "LOD entities keep their authored draw distance");
        check(duplicateOutput.Action == "YMAP optimized", "optimized output is labelled in merge activity");

        var offPlan = new AutomaticMergePlan();
        var offOutput = new AutomaticMergeOutput { Name = "seam_duplicate.ymap", Data = duplicateBytes };
        offPlan.Outputs.Add(offOutput);
        var off = MapOptimizer.Apply(offPlan, new List<MapAsset>(), new MapOptimizerOptions(), null);
        check(off.Skipped && !offOutput.Removed && offOutput.Data.SequenceEqual(duplicateBytes),
            "optimizer is off by default and leaves merge bytes unchanged");

        // ---- Workspace integration: opt-in cleanup reaches both export modes ----
        string emptySource = Path.Combine(roots[0], "stream", "seam_empty.ymap");
        File.WriteAllBytes(emptySource, emptyBytes);
        var before = Directory.GetFiles(Path.Combine(directory, "sources"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Digest(path));

        var workspace = new MergeWorkspace { Optimizer = Rules() };
        workspace.Import(roots);
        var plan = workspace.AutoMergeResources();
        var planEmpty = plan.Outputs.Single(o => o.Name.Equals("seam_empty.ymap", StringComparison.OrdinalIgnoreCase));
        check(planEmpty.Removed && plan.Optimization != null && plan.Optimization.EmptyMaps == 1,
            "opt-in optimizer removes the empty map during the automatic merge");
        check(plan.Summary.Contains("optimized"), "merge summary reports optimizer results");

        string exports = Path.Combine(directory, "exports");
        Directory.CreateDirectory(exports);
        var separate = workspace.ExportSeparateResources(exports);
        check(separate.ResourcePaths.All(path => !Directory.GetFiles(path, "seam_empty.ymap", SearchOption.AllDirectories).Any()),
            "separate resource export does not recreate an optimized-away map");

        string combinedFolder = Path.Combine(directory, "combined");
        Directory.CreateDirectory(combinedFolder);
        var combined = workspace.ExportAutomatic(combinedFolder, "seam_optimized");
        check(!Directory.GetFiles(combined.Path, "seam_empty.ymap", SearchOption.AllDirectories).Any(),
            "combined export omits an optimized-away map");
        check(File.ReadAllText(Path.Combine(combined.Path, "seam-merge-report.json")).Contains("Empty map removed") == false,
            "combined report lists only written outputs");

        var after = Directory.GetFiles(Path.Combine(directory, "sources"), "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => Digest(path));
        check(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var digest) && digest == pair.Value),
            "optimizer never modifies source files");

        workspace.Optimizer = new MapOptimizerOptions();
        var plain = workspace.AutoMergeResources();
        check(!plain.Outputs.Single(o => o.Name.Equals("seam_empty.ymap", StringComparison.OrdinalIgnoreCase)).Removed &&
              plan.Optimization.Applied, "turning the optimizer off restores the untouched merge output");

        string projectPath = Path.Combine(directory, "optimizer.seam");
        workspace.Optimizer = Rules();
        workspace.SaveProject(projectPath);
        var restored = MergeWorkspace.LoadProject(projectPath);
        check(restored.Optimizer.Enabled && restored.Optimizer.RemoveEmptyMaps && restored.Optimizer.RemoveDuplicatePlacements,
            "optimizer settings persist with a saved project");

        return passed;
    }

    private static MapOptimizerOptions Rules() => new MapOptimizerOptions
    { Enabled = true, RemoveDuplicatePlacements = true, RemoveEmptyMaps = true };

    private static void Name(XmlDocument document, string name)
    {
        document.DocumentElement["name"].InnerText = name;
        document.DocumentElement["block"]["name"].InnerText = name;
    }

    private static YmapFile Read(byte[] bytes) { var map = new YmapFile(); map.Load(bytes); return map; }

    private static XmlDocument Document(byte[] bytes)
    {
        var document = new XmlDocument { XmlResolver = null };
        document.LoadXml(MetaXml.GetXml(Read(bytes), out _));
        return document;
    }

    private static string Digest(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream));
    }
}
