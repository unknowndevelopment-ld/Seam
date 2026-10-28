using System;
using System.IO;
using System.Linq;
using System.Xml;
using CodeWalker.GameFiles;
using SharpDX;
using Seam;

internal static class MapRepairChecks
{
    public static int Run(string directory)
    {
        int passed=0;
        Action<bool,string> check=(ok,message)=>{if(!ok)throw new InvalidOperationException("Map repair: "+message);passed++;Console.WriteLine("  OK "+message);};
        var roots=MergeWorkspace.CreateDemo(Path.Combine(directory,"sources"));
        string quay=Path.Combine(roots[0],"stream","quay_district.ymap");
        var template=Document(File.ReadAllBytes(quay));
        var altered=(XmlDocument)template.CloneNode(true);
        var prop=(XmlElement)altered.SelectNodes("/CMapData/entities/Item")[2];
        prop["position"].SetAttribute("z","5"); prop["rotation"].SetAttribute("w","2");
        foreach(var field in new[]{"entitiesExtentsMin","entitiesExtentsMax","streamingExtentsMin","streamingExtentsMax"})
            foreach(var axis in new[]{"x","y","z"}) altered.DocumentElement[field].SetAttribute(axis,"0");
        File.WriteAllBytes(quay,XmlMeta.GetRSCData(altered));
        var duplicate=(XmlDocument)altered.CloneNode(true);
        duplicate.DocumentElement["name"].InnerText="repair_duplicate";
        duplicate.DocumentElement["entities"].RemoveAll(); duplicate.DocumentElement["entities"].AppendChild(duplicate.ImportNode(prop,true));
        File.WriteAllBytes(Path.Combine(roots[1],"stream","repair_duplicate.ymap"),XmlMeta.GetRSCData(duplicate));

        string modelPath=Path.Combine(roots[0],"stream","seam_tower.ydr");
        var model=new YdrFile();model.Load(File.ReadAllBytes(modelPath));
        model.Drawable.LightAttributes=new ResourceSimpleList64<LightAttributes>{data_items=new[]{new LightAttributes{Position=new Vector3(0,0,8),Direction=-Vector3.UnitZ,Type=LightType.Point,Falloff=10,Intensity=1}}};
        var modelBytes=model.Save();File.WriteAllBytes(modelPath,modelBytes);
        File.WriteAllBytes(Path.Combine(roots[1],"stream","seam_tower.ydr"),modelBytes);
        model=new YdrFile();model.Load(modelBytes);
        var type=new YtypFile();type.Load(File.ReadAllBytes(Path.Combine(roots[0],"stream","seam_demo.ytyp")));
        var placements=Read(File.ReadAllBytes(quay));var tower=placements.AllEntities[0];
        tower.SetArchetype(type.AllArchetypes.Single(a=>a.Hash.Hash==tower._CEntityDef.archetypeName.Hash));tower.EnsureLights(model.Drawable);
        uint ownerHash=tower.Lights[0].Hash;var expected=tower.Lights[0].Position;
        Pair(roots[0],template,new[]{ownerHash,101u,101u},new[]{new Vector3(0,0,300),new Vector3(2,2,2),new Vector3(2,2,2)});
        Pair(roots[1],template,new[]{ownerHash,202u},new[]{new Vector3(0,0,300),new Vector3(3,3,3)});
        var ground=new BoundBVH{Type=BoundsType.GeometryBVH,FileVFT=1080228536,BoxMin=new Vector3(-30,-30,-0.01f),BoxMax=new Vector3(30,30,0.01f),
            SphereRadius=45,Quantum=new Vector3(0.001f),Vertices=new[]{new Vector3(-30,-30,0),new Vector3(30,-30,0),new Vector3(0,30,0)}};
        ground.Polygons=new BoundPolygon[]{new BoundPolygonTriangle{Owner=ground,vertIndex1=0,vertIndex2=1,vertIndex3=2,MaterialCustom=new BoundMaterial_s{Data1=1}}};
        ground.BuildMaterials();ground.UpdateEdgeIndices();
        File.WriteAllBytes(Path.Combine(roots[0],"stream","repair_ground.ybn"),new YbnFile{Bounds=ground}.Save());
        var before=Directory.GetFiles(Path.Combine(directory,"sources"),"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
        var workspace=new MergeWorkspace();workspace.Import(roots);
        check(workspace.Packs.SelectMany(p=>p.Assets).All(a=>a.Error==null),"repair fixtures import as native resources");
        var plan=workspace.AutoMergeResources();
        var child=Read(plan.Outputs.Single(o=>o.Name=="repair_lights.ymap").Data);
        var parent=Read(plan.Outputs.Single(o=>o.Name=="repair_positions.ymap").Data);
        check(child.LODLights.hash.Length==3 && parent.DistantLODLights.positions.Length==3,"linked light versions merge and duplicate rows are removed together");
        int owner=Array.IndexOf(child.LODLights.hash,ownerHash);
        check(owner>=0 && (parent.DistantLODLights.positions[owner].ToVector3()-expected).Length()<0.01f,"floating light returns to its uniquely verified model owner");
        check(child.LODLights.falloff.Length==3 && parent.DistantLODLights.colours.Length==3,"light settings, positions and colours retain aligned indices");
        check(parent.DistantLODLights.CDistantLODLight.numStreetLights==0,"incorrect street-light counts are rebuilt from aligned light flags");
        check((parent.DistantLODLights.positions[Array.IndexOf(child.LODLights.hash,202u)].ToVector3()-new Vector3(3,3,3)).Length()<0.01f,"unmatched standalone light keeps its authored position");
        var fixedMap=Read(plan.Outputs.Single(o=>o.Name=="quay_district.ymap").Data);
        check(Math.Abs(fixedMap.AllEntities[2]._CEntityDef.rotation.W-1)<0.0001f,"invalid non-unit prop rotation is normalized");
        check(fixedMap.AllEntities[2].Position.Z==5,"suspected floating props are not blindly snapped to ground");
        check(fixedMap.CMapData.entitiesExtentsMin.X<0 && fixedMap.CMapData.entitiesExtentsMax.Z>=11,"stale map bounds expand to contain readable archetypes");
        check((Read(plan.Outputs.Single(o=>o.Name=="repair_duplicate.ymap").Data).AllEntities?.Length??0)==0,"identical unlinked prop is removed across static placement maps");
        check(plan.Operations.Any(o=>o.Kind=="Check placement"),"triangle support scan reports potentially floating props");
        check(plan.MapRepairs>0 && plan.MapChecks>0,"automatic fixes and uncertain checks are reported separately");
        check(before.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"repair pass leaves every original source file unchanged");
        return passed;
    }
    private static void Pair(string root,XmlDocument template,uint[] hashes,Vector3[] positions)
    {
        var child=(XmlDocument)template.CloneNode(true);var parent=(XmlDocument)template.CloneNode(true);
        foreach(var doc in new[]{child,parent}) {doc.DocumentElement["entities"].RemoveAll();doc.DocumentElement["parent"].InnerText="";}
        child.DocumentElement["name"].InnerText="repair_lights";child.DocumentElement["parent"].InnerText="repair_positions";
        parent.DocumentElement["name"].InnerText="repair_positions";
        var l=child.DocumentElement["LODLightsSOA"];var p=parent.DocumentElement["DistantLODLightsSOA"];
        foreach(var field in new[]{"falloff","falloffExponent","timeAndStateFlags","hash","coneInnerAngle","coneOuterAngleOrCapExt","coronaIntensity"})
            l[field].InnerText=string.Join(" ",hashes.Select(h=>field=="hash"?h.ToString():field=="falloff"?"10":field=="timeAndStateFlags"?"67108864":field=="falloffExponent"?"1":"0"));
        p["RGBI"].InnerText=string.Join(" ",hashes.Select(h=>"4294967295"));p["numStreetLights"].SetAttribute("value","1");
        for(int i=0;i<hashes.Length;i++) {Vector(l["direction"],-Vector3.UnitZ);Vector(p["position"],positions[i]);}
        File.WriteAllBytes(Path.Combine(root,"stream","repair_lights.ymap"),XmlMeta.GetRSCData(child));
        File.WriteAllBytes(Path.Combine(root,"stream","repair_positions.ymap"),XmlMeta.GetRSCData(parent));
    }
    private static void Vector(XmlElement node,Vector3 value)
    {
        var item=node.OwnerDocument.CreateElement("Item");node.AppendChild(item);
        foreach(var axis in new[]{"x","y","z"}){var e=node.OwnerDocument.CreateElement(axis);e.SetAttribute("value",(axis=="x"?value.X:axis=="y"?value.Y:value.Z).ToString(System.Globalization.CultureInfo.InvariantCulture));item.AppendChild(e);}
    }
    private static YmapFile Read(byte[] bytes){var m=new YmapFile();m.Load(bytes);return m;}
    private static XmlDocument Document(byte[] bytes){var d=new XmlDocument();d.LoadXml(MetaXml.GetXml(Read(bytes),out _));return d;}
}
