using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using CodeWalker.GameFiles;
using SharpDX;

namespace Seam
{
    // A light's settings and its parent's position/colour share the same row index.
    // Never union those arrays independently or pair versions from different packs.
    internal static class YmapLightMerger
    {
        internal sealed class Light
        {
            public Vector3 Position, Direction;
            public uint Colour, Flags, Hash;
            public float Falloff, Exponent;
            public byte Inner, Outer, Corona;
            public bool Street => (Flags & (1u << 24)) != 0;
            public string Key => string.Join("|", new[] { Position.X, Position.Y, Position.Z, Direction.X, Direction.Y, Direction.Z, Falloff, Exponent }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) +
                "|" + string.Join("|", Colour, Flags, Hash, Inner, Outer, Corona);
        }

        internal static List<AutomaticMergeOutput> Merge(List<MapAsset> assets, AutomaticMergePlan plan, Action<string> progress)
        {
            var result = new List<AutomaticMergeOutput>();
            foreach (var group in assets.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Any(a => (a.Ymap.LODLights?.hash?.Length ?? 0) > 0)))
            {
                var children = group.ToList();
                var parents = new List<MapAsset>();
                try
                {
                    uint parentHash = children[0].Ymap.CMapData.parent.Hash;
                    if (parentHash == 0 || children.Any(a => a.Ymap.CMapData.parent.Hash != parentHash)) throw new InvalidDataException("Light parent names differ or are missing.");
                    if (assets.Where(a => a.Ymap.CMapData.parent.Hash == parentHash && (a.Ymap.LODLights?.hash?.Length ?? 0) > 0)
                        .Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                        throw new InvalidDataException("Several different child maps share one light-position array.");
                    foreach (var child in children)
                    {
                        var candidates = assets.Where(a => a.Pack == child.Pack && NameHash(a) == parentHash).ToArray();
                        if (candidates.Length != 1) throw new InvalidDataException("The matching position map is missing or ambiguous in " + child.Pack?.Name + ".");
                        parents.Add(candidates[0]);
                    }
                    if (assets.Count(a => NameHash(a) == parentHash) != parents.Count || parents.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                        throw new InvalidDataException("Not every parent version has its matching child version.");
                    progress?.Invoke("Merging linked lights · " + group.Key);
                    var lights = new List<Light>();
                    var hashes = new Dictionary<uint, Light>();
                    var exact = new HashSet<string>(StringComparer.Ordinal);
                    int removed = 0, conflicts = 0;
                    for (int s = 0; s < children.Count; s++)
                        foreach (var light in Read(children[s].Ymap, parents[s].Ymap))
                        {
                            if (!exact.Add(light.Key)) { removed++; continue; }
                            if (light.Hash != 0 && hashes.ContainsKey(light.Hash)) { conflicts++; continue; }
                            lights.Add(light);
                            if (light.Hash != 0) hashes.Add(light.Hash, light);
                        }
                    lights = lights.OrderByDescending(l => l.Street).ThenBy(l => l.Hash).ToList();
                    if (lights.Count(l => l.Street) > ushort.MaxValue) throw new InvalidDataException("Too many street lights for the parent format.");
                    var childDoc = YmapMerger.Document(children[0]); var parentDoc = YmapMerger.Document(parents[0]);
                    var oldChild = YmapMerger.Canonical(childDoc.DocumentElement["LODLightsSOA"]);
                    var oldParent = YmapMerger.Canonical(parentDoc.DocumentElement["DistantLODLightsSOA"]);
                    Write(childDoc, parentDoc, lights);
                    bool arraysChanged = oldChild != YmapMerger.Canonical(childDoc.DocumentElement["LODLightsSOA"]) || oldParent != YmapMerger.Canonical(parentDoc.DocumentElement["DistantLODLightsSOA"]);
                    var replacements = new Dictionary<string, XmlElement> { ["LODLightsSOA"] = childDoc.DocumentElement["LODLightsSOA"] };
                    int ignored;
                    var childOutput = children.Count == 1 && oldChild == YmapMerger.Canonical(replacements["LODLightsSOA"])
                        ? Original(children) : YmapMerger.Combine(children, children[0].Name, out ignored, replacements);
                    replacements = new Dictionary<string, XmlElement> { ["DistantLODLightsSOA"] = parentDoc.DocumentElement["DistantLODLightsSOA"] };
                    var parentOutput = parents.Count == 1 && oldParent == YmapMerger.Canonical(replacements["DistantLODLightsSOA"])
                        ? Original(parents) : YmapMerger.Combine(parents, parents[0].Name, out ignored, replacements);
                    childOutput.Sources = children; parentOutput.Sources = parents;
                    result.Add(childOutput); result.Add(parentOutput);
                    plan.MergedMaps += children.Count - 1 + parents.Count - 1;
                    plan.MapRepairs += removed + conflicts + (children.Count == 1 && arraysChanged ? 1 : 0);
                    if (children.Count > 1 || arraysChanged || removed + conflicts > 0)
                        Log(plan, "Lights merged", group.Key, $"Retained {lights.Count:N0} complete light records with aligned position, colour, hash and settings. Removed {removed} identical rows; {conflicts} differing hash definitions followed source priority. Street-light ordering and count were rebuilt together.", lights.Count);
                }
                catch (Exception error) when (error is InvalidDataException || error is ArgumentException)
                {
                    plan.MapChecks++;
                    Log(plan, "Check lighting", group.Key, "Linked light merging was skipped: " + error.Message + " Original/fallback versions are retained; no missing positions are invented.", 1);
                }
            }
            return result;
        }

        internal static List<Light> Read(YmapFile child, YmapFile parent)
        {
            var l = child.LODLights; var p = parent.DistantLODLights;
            int n = l?.hash?.Length ?? 0;
            if ((l?.direction?.Length ?? 0) != n || (l?.falloff?.Length ?? 0) != n || (l?.falloffExponent?.Length ?? 0) != n ||
                (l?.timeAndStateFlags?.Length ?? 0) != n || (l?.coneInnerAngle?.Length ?? 0) != n || (l?.coneOuterAngleOrCapExt?.Length ?? 0) != n ||
                (l?.coronaIntensity?.Length ?? 0) != n || (p?.positions?.Length ?? 0) != n || (p?.colours?.Length ?? 0) != n)
                throw new InvalidDataException("Linked light arrays have different lengths.");
            var rows = new List<Light>(n);
            for (int i = 0; i < n; i++)
            {
                var row = new Light { Position = p.positions[i].ToVector3(), Direction = l.direction[i].ToVector3(), Colour = p.colours[i], Hash = l.hash[i],
                    Flags = l.timeAndStateFlags[i], Falloff = l.falloff[i], Exponent = l.falloffExponent[i], Inner = l.coneInnerAngle[i], Outer = l.coneOuterAngleOrCapExt[i], Corona = l.coronaIntensity[i] };
                if (!Finite(row.Position) || !Finite(row.Direction) || !Finite(row.Falloff) || !Finite(row.Exponent) || row.Falloff < 0 || row.Exponent < 0)
                    throw new InvalidDataException("A light contains invalid coordinates or attenuation.");
                rows.Add(row);
            }
            return rows;
        }

        internal static void Write(XmlDocument child, XmlDocument parent, List<Light> rows)
        {
            var l = child.DocumentElement["LODLightsSOA"]; var p = parent.DocumentElement["DistantLODLightsSOA"];
            Vectors(l["direction"], rows.Select(r => r.Direction)); Vectors(p["position"], rows.Select(r => r.Position));
            Values(l["falloff"], rows.Select(r => r.Falloff)); Values(l["falloffExponent"], rows.Select(r => r.Exponent));
            Values(l["timeAndStateFlags"], rows.Select(r => r.Flags)); Values(l["hash"], rows.Select(r => r.Hash));
            Values(l["coneInnerAngle"], rows.Select(r => r.Inner)); Values(l["coneOuterAngleOrCapExt"], rows.Select(r => r.Outer));
            Values(l["coronaIntensity"], rows.Select(r => r.Corona)); Values(p["RGBI"], rows.Select(r => r.Colour));
            p["numStreetLights"].SetAttribute("value", rows.Count(r => r.Street).ToString(CultureInfo.InvariantCulture));
        }
        private static void Values<T>(XmlElement node, IEnumerable<T> values) => node.InnerText = string.Join(" ", values.Select(v => v is float f ? f.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture)));
        private static void Vectors(XmlElement node, IEnumerable<Vector3> values)
        {
            foreach (XmlNode old in node.ChildNodes.Cast<XmlNode>().ToArray()) node.RemoveChild(old);
            foreach (var vector in values)
            {
                var item = node.OwnerDocument.CreateElement("Item"); node.AppendChild(item);
                foreach (var axis in new[] { "x", "y", "z" })
                {
                    var value = node.OwnerDocument.CreateElement(axis);
                    value.SetAttribute("value", (axis == "x" ? vector.X : axis == "y" ? vector.Y : vector.Z).ToString("R", CultureInfo.InvariantCulture));
                    item.AppendChild(value);
                }
            }
        }
        private static uint NameHash(MapAsset a) => a.Ymap.CMapData.name.Hash != 0 ? a.Ymap.CMapData.name.Hash : JenkHash.GenHash(Path.GetFileNameWithoutExtension(a.Name).ToLowerInvariant());
        private static AutomaticMergeOutput Original(List<MapAsset> assets) => new AutomaticMergeOutput { Name = assets[0].Name, Data = MergeWorkspace.ReadResourceBytes(assets[0].Path, ".ymap"), Sources = assets, Action = "Preserved light map" };
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Finite(Vector3 v) => Finite(v.X) && Finite(v.Y) && Finite(v.Z);
        internal static void Log(AutomaticMergePlan plan, string kind, string title, string detail, int count) => plan.Operations.Add(new MergeOperation { Kind = kind, Title = title, Detail = detail, Count = count });
    }
}
