using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using CodeWalker.GameFiles;
using Seam;

internal static class YmapMergeChecks
{
    public static int Run(string directory)
    {
        int passed = 0;
        Action<bool, string> check = (condition, text) =>
        {
            if (!condition) throw new InvalidOperationException("YMAP merge regression: " + text);
            passed++; Console.WriteLine("  OK " + text);
        };
        Directory.CreateDirectory(directory);
        var workspace = new MergeWorkspace();
        workspace.Import(MergeWorkspace.CreateDemo(Path.Combine(directory, "sources")));
        var maps = workspace.Packs.SelectMany(p => p.Assets).Where(a => a.Ymap != null).ToList();
        var plan = new AutomaticMergePlan();
        var merged = YmapMerger.Merge(maps, plan);
        check(merged.Count == 1 && plan.MergedMaps == 1, "independent custom maps compile into one YMAP");
        check(plan.DuplicateEntities == 1 && Read(merged.Single().Data).AllEntities.Length == 8, "combined map retains unique placements and removes the exact duplicate");

        string original = maps[1].Name;
        maps[1].Name = maps[0].Name;
        plan = new AutomaticMergePlan(); merged = YmapMerger.Merge(maps, plan);
        check(plan.MergedMaps == 1 && Read(merged.Single().Data).AllEntities.Length == 8, "same-name map variants combine instead of selecting one file");
        maps[1].Name = original;

        plan = new AutomaticMergePlan(); merged = YmapMerger.Merge(new[] { maps[0], maps[0] }, plan);
        check(plan.DuplicateFiles == 1 && merged.Single().Data.SequenceEqual(File.ReadAllBytes(maps[0].Path)), "byte-identical YMAP deduplication preserves original bytes");

        var linked = new List<MapAsset>();
        for (int s = 0; s < 2; s++)
        {
            var map = Read(File.ReadAllBytes(maps[s].Path)); map.Name = "local_hierarchy.ymap";
            var parent = map.AllEntities[0]._CEntityDef; parent.numChildren = 1; parent.lodLevel = rage__eLodType.LODTYPES_DEPTH_LOD;
            map.AllEntities[0]._CEntityDef = parent;
            var child = map.AllEntities[1]._CEntityDef; child.parentIndex = 0; map.AllEntities[1]._CEntityDef = child;
            linked.Add(Asset(directory, "hierarchy-" + s, "local_hierarchy.ymap", map.Save(), maps[s].Pack));
        }
        plan = new AutomaticMergePlan(); merged = YmapMerger.Merge(linked, plan);
        var hierarchy = Read(merged.Single().Data);
        check(plan.MergedMaps == 1 && hierarchy.AllEntities.Length == 8, "local LOD branches survive automatic map merging");
        check(hierarchy.AllEntities[1]._CEntityDef.parentIndex == 0 && hierarchy.AllEntities[6]._CEntityDef.parentIndex == 5,
            "local parent indices shift with their source map");
        check(hierarchy.AllEntities[0]._CEntityDef.numChildren == 1 && hierarchy.AllEntities[5]._CEntityDef.numChildren == 1,
            "local parent child counts remain consistent");

        var mlos = new List<MapAsset>();
        for (int s = 0; s < 2; s++)
        {
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(MetaXml.GetXml(maps[s].Ymap, out _));
            var entity = (XmlElement)doc.DocumentElement["entities"].FirstChild.CloneNode(true);
            entity.SetAttribute("type", "CMloInstanceDef");
            entity["flags"].SetAttribute("value", "1572864");
            doc.DocumentElement["contentFlags"].SetAttribute("value", "73");
            foreach (var field in new[] { "groupId", "floorId", "numExitPortals", "MLOInstflags" })
            { var value = doc.CreateElement(field); value.SetAttribute("value", "0"); entity.AppendChild(value); }
            entity.AppendChild(doc.CreateElement("defaultEntitySets"));
            var list = doc.DocumentElement["entities"]; list.RemoveAll(); list.AppendChild(entity);
            var physics = doc.DocumentElement["physicsDictionaries"]; physics.RemoveAll();
            var item = doc.CreateElement("Item"); item.InnerText = "hash_" + (0x12345000u + (uint)s).ToString("X8"); physics.AppendChild(item);
            mlos.Add(Asset(directory, "mlo-" + s, "shared_mlo.ymap", XmlMeta.GetRSCData(doc), maps[s].Pack));
        }
        plan = new AutomaticMergePlan(); merged = YmapMerger.Merge(mlos, plan);
        var mlo = Read(merged.Single().Data);
        check(plan.MergedMaps == 1 && mlo.MloEntities.Length == 2, "same-name MLO maps retain both complete instance definitions");
        check(mlo.physicsDictionaries.Length == 2, "MLO physics dictionary references are united");

        // Mirror Park authors remove old shells by moving them underground and
        // use GUIDs to identify the same original object across map versions.
        var replacements = new List<MapAsset>();
        for (int s = 0; s < 2; s++)
        {
            var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(MetaXml.GetXml(maps[0].Ymap, out _));
            doc.DocumentElement["parent"].InnerText = "hash_12345678";
            var template = (XmlElement)doc.DocumentElement["entities"].FirstChild.CloneNode(true);
            var list = doc.DocumentElement["entities"]; list.RemoveAll();
            for (int i = 0; i < 5; i++)
            {
                var e = (XmlElement)template.CloneNode(true);
                e["guid"].SetAttribute("value", (i < 4 ? 100 + i : 200 + s).ToString());
                e["flags"].SetAttribute("value", "1572872");
                e["parentIndex"].SetAttribute("value", (90 + i).ToString());
                e["numChildren"].SetAttribute("value", "0");
                e["position"].SetAttribute("x", (1000 + i * 10).ToString());
                e["position"].SetAttribute("y", "-500");
                e["position"].SetAttribute("z", s == 1 && i == 1 ? "20" : "70");
                if (s == 1 && i == 2) e["position"].SetAttribute("x", "1021");
                list.AppendChild(e);
            }
            var boxes = doc.DocumentElement["boxOccluders"]; boxes.RemoveAll();
            for (int i = 0; i < 2; i++)
            {
                var box = doc.CreateElement("Item");
                foreach (var field in new[] { "iCenterX", "iCenterY", "iCenterZ", "iCosZ", "iLength", "iWidth", "iHeight", "iSinZ" })
                {
                    var node = doc.CreateElement(field);
                    int value = field == "iCenterZ" ? (s == 0 ? 280 : -2400) : field == "iCenterX" ? (i == 0 ? 4000 : 4100 + s) : 20;
                    node.SetAttribute("value", value.ToString()); box.AppendChild(node);
                }
                boxes.AppendChild(box);
            }
            replacements.Add(Asset(directory, "replacement-" + s, "hei_id2_21_g_strm_0.ymap", XmlMeta.GetRSCData(doc), maps[s].Pack));
        }
        plan = new AutomaticMergePlan(); merged = YmapMerger.Merge(replacements, plan, null, false);
        var replacement = Read(merged.Single().Data);
        check(merged.Single().Action == "Replacement edits merged" && replacement.AllEntities.Length == 4, "replacement map removal edits combine instead of restoring missing original shells");
        check(replacement.AllEntities.Single(e => e._CEntityDef.guid == 101).Position.Z == 20, "underground shell edit survives even when its map has lower priority");
        check(replacement.AllEntities.Single(e => e._CEntityDef.guid == 102).Position.X == 1020, "ambiguous horizontal edits follow source priority");
        check(replacement.AllEntities.All(e => e.LodInParentYmap && e._CEntityDef.parentIndex >= 90) && replacement.CMapData.parent.Hash == 0x12345678, "external parent indices remain unchanged after removing placements");
        var replacementXml = new XmlDocument(); replacementXml.LoadXml(MetaXml.GetXml(replacement, out _));
        var retainedBoxes = replacementXml.SelectNodes("/CMapData/boxOccluders/Item");
        check(retainedBoxes.Count == 1 && ((XmlElement)retainedBoxes[0])["iCenterZ"].GetAttribute("value") == "-2400", "occluder removals and lowered occluders survive together");
        plan = new AutomaticMergePlan(); merged = YmapMerger.Merge(replacements.AsEnumerable().Reverse().ToArray(), plan, null, false);
        check(Read(merged.Single().Data).AllEntities.Single(e => e._CEntityDef.guid == 101).Position.Z == 20, "lowered shells remain hidden when source priority is reversed");
        return passed;
    }

    private static MapAsset Asset(string directory, string suffix, string name, byte[] bytes, MapPack pack)
    {
        string path = Path.Combine(directory, suffix + ".ymap"); File.WriteAllBytes(path, bytes);
        return new MapAsset { Name = name, Extension = ".ymap", Path = path, Ymap = Read(bytes), Pack = pack };
    }
    private static YmapFile Read(byte[] bytes) { var map = new YmapFile(); map.Load(bytes); return map; }
}
