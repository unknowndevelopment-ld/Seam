using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using CodeWalker.GameFiles;

namespace Seam
{
    // Opt-in cleanup that runs inside the automatic merge, after repairs and before
    // the snapshot. Every edit is recompiled and compared against the binary output;
    // a map that cannot be read or rewritten losslessly keeps its original bytes.
    public sealed class MapOptimizerOptions
    {
        public bool Enabled { get; set; }
        public bool RemoveDuplicatePlacements { get; set; } = true;
        public bool RemoveEmptyMaps { get; set; } = true;
        public bool LimitDrawDistance { get; set; }
        public float MaxDrawDistance { get; set; } = 500f;

        public bool AnyRule => RemoveDuplicatePlacements || RemoveEmptyMaps || LimitDrawDistance;

        public MapOptimizerOptions Copy() => new MapOptimizerOptions
        {
            Enabled = Enabled, RemoveDuplicatePlacements = RemoveDuplicatePlacements,
            RemoveEmptyMaps = RemoveEmptyMaps, LimitDrawDistance = LimitDrawDistance, MaxDrawDistance = MaxDrawDistance
        };
    }

    public sealed class MapOptimizerReport
    {
        public bool Applied { get; set; }
        public bool Skipped { get; set; }
        public int MapsScanned { get; set; }
        public int WritableMaps { get; set; }
        public int DuplicatePlacements { get; set; }
        public int EmptyMaps { get; set; }
        public int CappedDrawDistances { get; set; }
        public long SavedBytes { get; set; }

        public int Changes => DuplicatePlacements + EmptyMaps + CappedDrawDistances;

        public string Summary => Skipped
            ? "Map optimizer skipped: no rule selected or options are off."
            : Changes == 0 ? "Map optimizer found nothing to remove in " + MapsScanned.ToString("N0") + " maps."
            : $"{DuplicatePlacements:N0} duplicate placements removed · {EmptyMaps:N0} empty maps dropped · {CappedDrawDistances:N0} draw distances capped · {Size(SavedBytes)} saved";

        // Compact suffix for the merge summary and status bar.
        public string Short => Changes == 0 ? "" : $" · {Changes:N0} optimized · {Size(SavedBytes)} saved";

        private static string Size(long bytes) => bytes < 1024 ? bytes + " B"
            : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
            : (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    public static class MapOptimizer
    {
        private static readonly HashSet<string> MapLevel = new HashSet<string>(StringComparer.Ordinal)
        { "name", "parent", "block", "flags", "contentFlags", "entities", "streamingExtentsMin", "streamingExtentsMax", "entitiesExtentsMin", "entitiesExtentsMax" };

        private sealed class Target
        {
            public AutomaticMergeOutput Output;
            public YmapFile File;
            public XmlDocument Xml;
            public XmlElement[] Items;
            public YmapEntityDef[] Entities;
            public bool Standalone;
            public int Duplicates;
            public int Capped;
            public bool Empty;
            public byte[] Data;
        }

        public static MapOptimizerReport Apply(AutomaticMergePlan plan, List<MapAsset> assets, MapOptimizerOptions options, Action<string> progress)
        {
            var report = new MapOptimizerReport();
            if (options == null || !options.Enabled || !options.AnyRule) { report.Skipped = true; return report; }

            var referenced = new HashSet<uint>((assets ?? new List<MapAsset>())
                .Where(a => a.Ymap != null).Select(a => a.Ymap.CMapData.parent.Hash).Where(hash => hash != 0));
            var scripted = new Dictionary<MapPack, bool>();
            var packs = (assets ?? new List<MapAsset>()).Select(a => a.Pack).Where(p => p != null).Distinct().ToArray();
            bool anyScripts = packs.Any(p => MapRepairer.HasScripts(p, scripted));
            var targets = new List<Target>();
            foreach (var output in plan.Outputs.Where(o => !o.Removed && o.Data != null && o.Name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var file = new YmapFile(); file.Load(output.Data);
                    var document = new XmlDocument { XmlResolver = null };
                    document.LoadXml(MetaXml.GetXml(file, out _));
                    if (document.DocumentElement?.Name != "CMapData" || document.SelectSingleNode("//error") != null) continue;
                    var items = document.SelectNodes("/CMapData/entities/Item")?.Cast<XmlElement>().ToArray() ?? new XmlElement[0];
                    var entities = file.AllEntities ?? new YmapEntityDef[0];
                    if (items.Length != entities.Length || items.Any(i => i.GetAttribute("type") == "CMloInstanceDef") || (file.MloEntities?.Length ?? 0) != 0) continue;
                    var target = new Target { Output = output, File = file, Xml = document, Items = items, Entities = entities };
                    target.Standalone = file.CMapData.parent.Hash == 0 && file.CMapData.flags == 0 && !referenced.Contains(file.CMapData.name.Hash) &&
                        entities.All(e => !e.IsMlo && !e.LodInParentYmap && e._CEntityDef.parentIndex < 0 && e._CEntityDef.numChildren == 0 && e._CEntityDef.guid == 0) &&
                        !YmapMerger.HasData(document.DocumentElement["containerLods"]) &&
                        !YmapMerger.HasData(document.DocumentElement["instancedData"]?["PropInstanceList"]) &&
                        !output.Sources.Any(a => MapRepairer.HasScripts(a.Pack, scripted));
                    targets.Add(target);
                }
                catch (Exception error) when (error is InvalidDataException || error is ArgumentException || error is FormatException || error is IndexOutOfRangeException)
                {
                    plan.MapChecks++;
                    YmapLightMerger.Log(plan, "Check optimization", output.Name,
                        "The optimizer kept this map unchanged because its metadata could not be read losslessly: " + error.Message, 1);
                }
            }
            report.MapsScanned = targets.Count;
            report.WritableMaps = targets.Count(t => t.Standalone);

            foreach (var target in targets)
            {
                if (!target.Standalone) continue;
                progress?.Invoke("Optimizing map · " + target.Output.Name);
                if (options.LimitDrawDistance && options.MaxDrawDistance > 0)
                {
                    float cap = options.MaxDrawDistance;
                    for (int i = 0; i < target.Items.Length; i++)
                    {
                        var entity = target.Entities[i]; var def = entity._CEntityDef;
                        if (entity.IsMlo || entity.LodInParentYmap || def.parentIndex >= 0 || def.numChildren != 0) continue;
                        if (def.lodLevel != rage__eLodType.LODTYPES_DEPTH_HD && def.lodLevel != rage__eLodType.LODTYPES_DEPTH_ORPHANHD) continue;
                        var node = target.Items[i]["lodDist"];
                        if (node == null || !float.TryParse(node.GetAttribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || value <= cap) continue;
                        node.SetAttribute("value", cap.ToString("R", CultureInfo.InvariantCulture));
                        target.Capped++;
                    }
                }
                if (options.RemoveDuplicatePlacements)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in target.Items)
                        if (!seen.Add(YmapMerger.Canonical(item))) { item.ParentNode.RemoveChild(item); target.Duplicates++; }
                    target.Items = target.Xml.SelectNodes("/CMapData/entities/Item")?.Cast<XmlElement>().ToArray() ?? new XmlElement[0];
                }
                if (options.RemoveEmptyMaps && target.Items.Length == 0)
                {
                    uint content = ReadUInt(target.Xml.DocumentElement["contentFlags"]) & ~1u;
                    bool metadata = target.Xml.DocumentElement.ChildNodes.OfType<XmlElement>().Any(e => !MapLevel.Contains(e.Name) && YmapMerger.HasData(e));
                    target.Empty = !metadata && content == 0 && (target.File.LODLights?.hash?.Length ?? 0) == 0 && (target.File.DistantLODLights?.positions?.Length ?? 0) == 0;
                    // A client script may request this name even when the map has no placements.
                    if (target.Empty && anyScripts) target.Empty = false;
                }
            }

            try
            {
                foreach (var target in targets.Where(t => (t.Duplicates > 0 || t.Capped > 0) && !t.Empty))
                    target.Data = Compile(target.Xml);
            }
            catch (Exception error) when (error is InvalidDataException || error is ArgumentException || error is OverflowException)
            {
                plan.MapChecks++;
                YmapLightMerger.Log(plan, "Check optimization", "Map optimizer skipped",
                    "No optimization was applied because a rewritten map failed its binary round trip: " + error.Message + " Original merge bytes are retained.", 1);
                report.Skipped = true;
                return report;
            }

            foreach (var target in targets)
            {
                if (target.Empty)
                {
                    target.Output.Removed = true;
                    target.Output.Action = "Empty map removed";
                    plan.RemovedOutputNames.Add(target.Output.Name);
                    report.EmptyMaps++;
                    report.SavedBytes += target.Output.Data.Length;
                    YmapLightMerger.Log(plan, "Optimized", target.Output.Name,
                        "The merged map contains no placements or map metadata after cleanup, so it is not written to the export. Its source file is unchanged.", 1);
                    continue;
                }
                if (target.Data == null) continue;
                int before = target.Output.Data.Length;
                target.Output.Data = target.Data;
                target.Output.Action = "YMAP optimized";
                report.DuplicatePlacements += target.Duplicates;
                report.CappedDrawDistances += target.Capped;
                report.SavedBytes += Math.Max(0, before - target.Data.Length);
                if (target.Duplicates + target.Capped > 0)
                    YmapLightMerger.Log(plan, "Optimized", target.Output.Name,
                        $"Removed {target.Duplicates:N0} duplicate standalone placements and capped {target.Capped:N0} draw distances. Entity links, LOD branches, lights and map metadata passed a binary round trip; the source file is unchanged.",
                        target.Duplicates + target.Capped);
            }

            report.Applied = report.Changes > 0;
            if (report.Applied)
                YmapLightMerger.Log(plan, "Optimized", "Map optimizer", report.Summary + ". Only lossless edits were written; protected, linked and scripted maps are untouched.", report.Changes);
            return report;
        }

        private static byte[] Compile(XmlDocument document)
        {
            var bytes = XmlMeta.GetRSCData(document);
            if (bytes == null || YmapMerger.Canonical(Load(bytes).DocumentElement) != YmapMerger.Canonical(document.DocumentElement))
                throw new InvalidDataException("Optimizer changed metadata during the binary round trip.");
            return bytes;
        }

        private static XmlDocument Load(byte[] bytes)
        {
            var file = new YmapFile(); file.Load(bytes);
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(MetaXml.GetXml(file, out _));
            return document;
        }

        private static uint ReadUInt(XmlElement element)
        {
            return element != null && uint.TryParse(element.GetAttribute("value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
    }

    public sealed partial class MergeWorkspace
    {
        public MapOptimizerOptions Optimizer { get; set; } = new MapOptimizerOptions();
    }
}
