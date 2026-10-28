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
    internal static class MapRepairer
    {
        private sealed class Map
        {
            public AutomaticMergeOutput Output;
            public YmapFile File;
            public XmlDocument Xml;
            public int Fixes;
        }

        public static void Apply(AutomaticMergePlan plan, List<MapAsset> assets, Action<string> progress)
        {
            var maps = plan.Outputs.Where(o => o.Data != null && o.Name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase) && o.Sources.All(a => a.Error == null && a.Ymap != null))
                .Select(o => { var file = Read(o.Data); return new Map { Output = o, File = file, Xml = Xml(file) }; }).ToList();
            if (maps.Count == 0) return;
            var archetypes = assets.Where(a => a.Error == null && a.Ytyp != null).SelectMany(a => a.Ytyp.AllArchetypes ?? new Archetype[0])
                .GroupBy(a => a.Hash.Hash).ToDictionary(g => g.Key, g => g.First());
            var referenced = new HashSet<uint>(maps.Select(m => m.File.CMapData.parent.Hash));
            var duplicateKeys = new HashSet<string>(StringComparer.Ordinal);
            var scripted = new Dictionary<MapPack, bool>();
            int duplicates = 0;
            try
            {
                foreach (var map in maps)
                {
                    progress?.Invoke("Checking map · " + map.Output.Name);
                    var items = map.Xml.SelectNodes("/CMapData/entities/Item").Cast<XmlElement>().ToArray();
                    var entities = map.File.AllEntities ?? new YmapEntityDef[0];
                    if (items.Length != entities.Length) throw new InvalidDataException("Unrecognized entity pointer order in " + map.Output.Name);
                    bool canRemove = map.File.CMapData.parent.Hash == 0 && map.File.CMapData.flags == 0 && !referenced.Contains(map.File.CMapData.name.Hash) &&
                        entities.All(e => e._CEntityDef.parentIndex < 0 && e._CEntityDef.numChildren == 0) &&
                        !map.Output.Sources.Any(a => HasScripts(a.Pack, scripted));
                    for (int i = 0; i < entities.Length; i++)
                    {
                        var item = items[i]; var entity = entities[i];
                        var rotation = item["rotation"];
                        var q = new Quaternion(F(rotation,"x"),F(rotation,"y"),F(rotation,"z"),F(rotation,"w"));
                        float length = q.Length();
                        if (length > 0.000001f && Math.Abs(length-1) > 0.0001f)
                        {
                            q.Normalize(); Set(rotation,"x",q.X); Set(rotation,"y",q.Y); Set(rotation,"z",q.Z); Set(rotation,"w",q.W); map.Fixes++;
                            var def = entity._CEntityDef; def.rotation = new Vector4(q.X,q.Y,q.Z,q.W);
                            entity = new YmapEntityDef(map.File,i,ref def);
                        }
                        if (canRemove && !entity.IsMlo && entity._CEntityDef.guid == 0 && !duplicateKeys.Add(YmapMerger.Canonical(item)))
                        {
                            item.ParentNode.RemoveChild(item); map.Fixes++; duplicates++; continue;
                        }
                        // Expand existing bounds only: unknown assets and other metadata
                        // may occupy more space than the imported archetypes describe.
                        var box = GeometryReader.GetEntityBounds(new MapEntity { Entity = entity }, archetypes.TryGetValue(entity._CEntityDef.archetypeName.Hash, out var arch) ? arch : null);
                        if (!box.Estimated && Valid(box))
                        {
                            if (Expand(map.Xml, "entitiesExtents", box)) map.Fixes++;
                            if (Expand(map.Xml, "streamingExtents", box)) map.Fixes++;
                        }
                    }
                    uint content = map.File.CMapData.contentFlags, flags = map.File.CMapData.flags;
                    if ((map.File.LODLights?.hash?.Length ?? 0) > 0) content |= 128;
                    if ((map.File.DistantLODLights?.positions?.Length ?? 0) > 0) { content |= 256; flags |= 2; }
                    if (content != map.File.CMapData.contentFlags) { Value(map.Xml.DocumentElement["contentFlags"], content); map.Fixes++; }
                    if (flags != map.File.CMapData.flags) { Value(map.Xml.DocumentElement["flags"], flags); map.Fixes++; }
                }
                if (maps.Any(m => (m.File.LODLights?.hash?.Length ?? 0) > 0))
                {
                    foreach (var map in maps.Where(m => m.Fixes > 0)) map.File = Read(Compile(map.Xml));
                    RepairLights(maps, assets, archetypes, plan);
                }
                // Validate every edit before publishing any; a parent/child light
                // repair must not commit just one half if serialization fails.
                var staged = maps.Where(m => m.Fixes > 0).Select(m => new { Map = m, Data = Compile(m.Xml) }).ToArray();
                foreach (var change in staged)
                {
                    change.Map.Output.Data = change.Data; change.Map.Output.Action = "YMAP repaired";
                    change.Map.File = Read(change.Data);
                    plan.MapRepairs += change.Map.Fixes;
                    YmapLightMerger.Log(plan, "Map repaired", change.Map.Output.Name,
                        "Applied " + change.Map.Fixes + " verified transform, duplicate-placement, map-bound or linked-light corrections. Full metadata passed a binary round trip; original source files are unchanged.", change.Map.Fixes);
                }
                plan.DuplicateEntities += duplicates;
            }
            catch (Exception error) when (error is InvalidDataException || error is ArgumentException || error is OverflowException)
            {
                plan.MapChecks++;
                YmapLightMerger.Log(plan, "Check map data", "Automatic repair skipped", "The repair pass retained its input bytes because validation failed: " + error.Message, 1);
                return;
            }
            AuditFloating(maps, plan, archetypes);
        }

        private static void RepairLights(List<Map> maps, List<MapAsset> assets, Dictionary<uint, Archetype> archetypes, AutomaticMergePlan plan)
        {
            // Compute owner hashes using CodeWalker's model/bounds algorithm. A
            // matching, unique hash is evidence; proximity alone is not ownership.
            var modelAssets = assets.Where(a => a.Error == null && (a.Extension == ".ydr" || a.Extension == ".ydd"))
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(a => a.Digest).Distinct().Count() == 1)
                .Select(g => g.First()).GroupBy(a => JenkHash.GenHash(Path.GetFileNameWithoutExtension(a.Name).ToLowerInvariant())).ToDictionary(g => g.Key, g => g.First());
            var loaded = new Dictionary<string, DrawableBase>();
            var owners = new Dictionary<uint, YmapEntityDef.LightInstance>();
            var ambiguous = new HashSet<uint>();
            foreach (var entity in maps.SelectMany(m => m.File.AllEntities ?? new YmapEntityDef[0]).Where(e => !e.IsMlo && e.Scale == Vector3.One))
            {
                if (!archetypes.TryGetValue(entity._CEntityDef.archetypeName.Hash, out var arch)) continue;
                uint hash = arch._BaseArchetypeDef.assetName.Hash;
                if (hash == 0) hash = arch.Hash.Hash;
                uint dictionary = arch.DrawableDict.Hash;
                if (!modelAssets.TryGetValue(dictionary == 0 ? hash : dictionary, out var asset)) continue;
                string key = asset.Path + ":" + hash;
                if (!loaded.TryGetValue(key, out var drawable))
                {
                    try
                    {
                        if (asset.Extension == ".ydr") { var file = new YdrFile(); file.Load(File.ReadAllBytes(asset.Path)); drawable = file.Drawable; }
                        else { var file = new YddFile(); file.Load(File.ReadAllBytes(asset.Path)); drawable = file.Dict != null && file.Dict.TryGetValue(hash, out var member) ? member : null; }
                    }
                    catch (Exception error) when (error is InvalidDataException || error is IOException || error is ArgumentException) { drawable = null; }
                    loaded[key] = drawable;
                }
                if (drawable == null || drawable.Skeleton != null) continue;
                entity.SetArchetype(arch); entity.EnsureLights(drawable);
                foreach (var light in entity.Lights ?? new YmapEntityDef.LightInstance[0])
                {
                    if (!YmapLightMerger.Finite(light.Position) || !YmapLightMerger.Finite(light.Direction)) continue;
                    if (owners.TryGetValue(light.Hash, out var old) && ((old.Position-light.Position).LengthSquared() > 0.0001f || (old.Direction-light.Direction).LengthSquared() > 0.0001f)) ambiguous.Add(light.Hash);
                    else owners[light.Hash] = light;
                }
            }
            foreach (var child in maps.Where(m => (m.File.LODLights?.hash?.Length ?? 0) > 0))
            {
                var candidates = maps.Where(m => m.File.CMapData.name.Hash == child.File.CMapData.parent.Hash).ToArray();
                if (candidates.Length != 1) continue;
                var parent = candidates[0]; List<YmapLightMerger.Light> rows;
                try { rows = YmapLightMerger.Read(child.File, parent.File); }
                catch (InvalidDataException) { continue; }
                int fixedPositions = 0, fixedDirections = 0, unmatched = 0;
                foreach (var row in rows)
                {
                    if (row.Hash == 0 || ambiguous.Contains(row.Hash) || !owners.TryGetValue(row.Hash, out var owner)) { unmatched++; continue; }
                    if ((row.Position-owner.Position).LengthSquared() > 0.0025f) { row.Position = owner.Position; fixedPositions++; }
                    // Point lights may legitimately have no direction.
                    if (((row.Flags >> 26) & 7) != 1 && owner.Direction.LengthSquared() > 0.000001f && (row.Direction-owner.Direction).LengthSquared() > 0.0001f)
                    { row.Direction = owner.Direction; fixedDirections++; }
                }
                if (fixedPositions + fixedDirections > 0)
                {
                    YmapLightMerger.Write(child.Xml, parent.Xml, rows);
                    parent.Fixes += fixedPositions; child.Fixes += fixedDirections;
                    foreach (var row in rows)
                    {
                        var box = new Box3 { MinX=row.Position.X-row.Falloff,MinY=row.Position.Y-row.Falloff,MinZ=row.Position.Z-row.Falloff,MaxX=row.Position.X+row.Falloff,MaxY=row.Position.Y+row.Falloff,MaxZ=row.Position.Z+row.Falloff };
                        if (Expand(parent.Xml,"entitiesExtents",box)) parent.Fixes++;
                        if (Expand(parent.Xml,"streamingExtents",box)) parent.Fixes++;
                        if (Expand(child.Xml,"entitiesExtents",box)) child.Fixes++;
                        if (Expand(child.Xml,"streamingExtents",box)) child.Fixes++;
                    }
                }
                if (unmatched > 0)
                {
                    plan.MapChecks += unmatched;
                    YmapLightMerger.Log(plan,"Check lighting",child.Output.Name,unmatched + " lights could not be linked uniquely to an imported readable model. They remain unchanged; missing game assets, encrypted models and intentional standalone lights cannot be inferred from height.",unmatched);
                }
            }
        }

        private sealed class Triangle { public Vector3 A,B,C; }
        private static void AuditFloating(List<Map> maps, AutomaticMergePlan plan, Dictionary<uint, Archetype> archetypes)
        {
            if (archetypes.Count == 0) { Coverage(plan,"No readable archetype bounds were supplied; placement support cannot be checked."); return; }
            var cells = new Dictionary<Tuple<int,int>,List<Triangle>>(); int triangles = 0, entries = 0; bool limited = false;
            foreach (var output in plan.Outputs.Where(o => o.Data != null && o.Name.EndsWith(".ybn",StringComparison.OrdinalIgnoreCase) && o.Sources.All(a => a.Error == null && a.Ybn != null)))
            {
                var file = new YbnFile(); file.Load(output.Data);
                foreach (var geometry in MergeWorkspace.FlattenBounds(file.Bounds).OfType<BoundGeometry>())
                {
                    var world = geometry.Transform;
                    for (var parent = geometry.Parent; parent != null; parent = parent.Parent) world *= parent.Transform;
                    foreach (var face in (geometry.Polygons ?? new BoundPolygon[0]).OfType<BoundPolygonTriangle>())
                    {
                        if (triangles >= 100000 || entries >= 500000) { limited = true; break; }
                        var t = new Triangle { A=Vector3.TransformCoordinate(geometry.GetVertex(face.vertIndex1)+geometry.CenterGeom,world), B=Vector3.TransformCoordinate(geometry.GetVertex(face.vertIndex2)+geometry.CenterGeom,world), C=Vector3.TransformCoordinate(geometry.GetVertex(face.vertIndex3)+geometry.CenterGeom,world) };
                        if (!YmapLightMerger.Finite(t.A) || !YmapLightMerger.Finite(t.B) || !YmapLightMerger.Finite(t.C)) continue;
                        var min = Vector3.Min(t.A,Vector3.Min(t.B,t.C)); var max=Vector3.Max(t.A,Vector3.Max(t.B,t.C));
                        if (Math.Abs(min.X)>1000000 || Math.Abs(min.Y)>1000000 || Math.Abs(max.X)>1000000 || Math.Abs(max.Y)>1000000) continue;
                        int x0=Cell(min.X),x1=Cell(max.X),y0=Cell(min.Y),y1=Cell(max.Y);
                        if ((long)(x1-x0+1)*(y1-y0+1)>256) { limited=true; continue; }
                        triangles++;
                        for(int x=x0;x<=x1;x++) for(int y=y0;y<=y1;y++) { var key=Tuple.Create(x,y); if(!cells.TryGetValue(key,out var list)) cells[key]=list=new List<Triangle>(); list.Add(t); entries++; }
                    }
                }
            }
            int reported=0, tests=0;
            foreach(var map in maps)
            {
                var samples=new List<string>(); int count=0;
                foreach(var entity in (map.File.AllEntities ?? new YmapEntityDef[0]).Where(e=>!e.IsMlo && (e._CEntityDef.lodLevel==rage__eLodType.LODTYPES_DEPTH_HD || e._CEntityDef.lodLevel==rage__eLodType.LODTYPES_DEPTH_ORPHANHD)))
                {
                    if(reported>=50 || tests>=1000000) { limited=true; break; }
                    if(!archetypes.TryGetValue(entity._CEntityDef.archetypeName.Hash,out var arch)) continue;
                    var b=GeometryReader.GetEntityBounds(new MapEntity{Entity=entity},arch);
                    if(!Valid(b) || Math.Abs(b.X)>1000000 || Math.Abs(b.Y)>1000000 || !cells.TryGetValue(Tuple.Create(Cell(b.X),Cell(b.Y)),out var faces)) continue;
                    double floor=double.NegativeInfinity;
                    foreach(var t in faces)
                    {
                        if(++tests>1000000) { limited=true; break; }
                        double d=(t.B.Y-t.C.Y)*(t.A.X-t.C.X)+(t.C.X-t.B.X)*(t.A.Y-t.C.Y);
                        if(Math.Abs(d)<0.00001) continue;
                        double u=((t.B.Y-t.C.Y)*(b.X-t.C.X)+(t.C.X-t.B.X)*(b.Y-t.C.Y))/d;
                        double v=((t.C.Y-t.A.Y)*(b.X-t.C.X)+(t.A.X-t.C.X)*(b.Y-t.C.Y))/d;
                        if(u < -0.00001 || v < -0.00001 || u+v>1.00001) continue;
                        double z=u*t.A.Z+v*t.B.Z+(1-u-v)*t.C.Z;
                        if(z<=b.MinZ+0.1 && z>b.MinZ-20) floor=Math.Max(floor,z);
                    }
                    if(double.IsNegativeInfinity(floor) || b.MinZ-floor<=0.75) continue;
                    count++; reported++;
                    if(samples.Count<5) samples.Add(entity._CEntityDef.archetypeName+" at "+entity.Position+"; bottom is "+(b.MinZ-floor).ToString("0.00",CultureInfo.InvariantCulture)+" m above imported collision");
                }
                if(count>0) { plan.MapChecks+=count; YmapLightMerger.Log(plan,"Check placement",map.Output.Name,count+" possible unsupported placements. "+string.Join("; ",samples)+". Hanging, wall-mounted and intentionally raised props can be valid; no height was guessed or snapped.",count); }
            }
            Coverage(plan,(triangles==0 ? "No usable imported collision triangles were available." : triangles.ToString("N0")+" imported collision triangles checked.")+
                (limited ? " The diagnostic scan reached its budget or omitted very large surfaces; coverage is partial." : "")+" Missing game-world, primitive and embedded model collision is not included. Uncertain placements and lights remain unchanged.");
        }

        private static int Cell(double value)=>(int)Math.Floor(value/16);
        private static void Coverage(AutomaticMergePlan p,string detail)=>YmapLightMerger.Log(p,"Check coverage","Placement support",detail,0);
        private static bool HasScripts(MapPack pack, Dictionary<MapPack,bool> cache)
        {
            if(pack==null || string.IsNullOrEmpty(pack.Root) || !Directory.Exists(pack.Root)) return false;
            if(!cache.TryGetValue(pack,out bool scripts)) cache[pack]=scripts=Directory.EnumerateFiles(pack.Root,"*.lua",SearchOption.AllDirectories)
                .Any(p=>!Path.GetFileName(p).Equals("fxmanifest.lua",StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(p).Equals("__resource.lua",StringComparison.OrdinalIgnoreCase));
            return scripts;
        }
        private static bool Valid(Box3 b)=>YmapLightMerger.Finite((float)b.MinX)&&YmapLightMerger.Finite((float)b.MinY)&&YmapLightMerger.Finite((float)b.MinZ)&&YmapLightMerger.Finite((float)b.MaxX)&&YmapLightMerger.Finite((float)b.MaxY)&&YmapLightMerger.Finite((float)b.MaxZ)&&b.MinX<=b.MaxX&&b.MinY<=b.MaxY&&b.MinZ<=b.MaxZ;
        private static bool Expand(XmlDocument doc,string field,Box3 b)
        {
            var min=doc.DocumentElement[field+"Min"]; var max=doc.DocumentElement[field+"Max"];
            if(min==null || max==null) return false;
            bool changed=false;
            foreach(var axis in new[]{"x","y","z"})
            {
                double lo=F(min,axis),hi=F(max,axis),a=axis=="x"?b.MinX:axis=="y"?b.MinY:b.MinZ,z=axis=="x"?b.MaxX:axis=="y"?b.MaxY:b.MaxZ;
                if(double.IsNaN(lo)||double.IsInfinity(lo)||double.IsNaN(hi)||double.IsInfinity(hi)||lo>hi) continue;
                if(a<lo-0.25) {Set(min,axis,(float)a);changed=true;} if(z>hi+0.25) {Set(max,axis,(float)z);changed=true;}
            }
            return changed;
        }
        private static float F(XmlElement e,string axis)=>float.Parse(e.GetAttribute(axis),CultureInfo.InvariantCulture);
        private static void Set(XmlElement e,string axis,float value)=>e.SetAttribute(axis,value.ToString("R",CultureInfo.InvariantCulture));
        private static void Value(XmlElement e,uint value)=>e.SetAttribute("value",value.ToString(CultureInfo.InvariantCulture));
        private static YmapFile Read(byte[] bytes) {var f=new YmapFile();f.Load(bytes);return f;}
        private static XmlDocument Xml(YmapFile file) {var d=new XmlDocument{XmlResolver=null};d.LoadXml(MetaXml.GetXml(file,out _));return d;}
        private static byte[] Compile(XmlDocument document)
        {
            var bytes=XmlMeta.GetRSCData(document);
            if(bytes==null || YmapMerger.Canonical(Xml(Read(bytes)).DocumentElement)!=YmapMerger.Canonical(document.DocumentElement)) throw new InvalidDataException("Repair changed metadata during the binary round trip.");
            return bytes;
        }
    }
}
