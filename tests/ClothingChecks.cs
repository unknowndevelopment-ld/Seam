using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using CodeWalker.GameFiles;
using Seam;

internal static class ClothingChecks
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        int passed = 0;
        Action<bool,string> check = (ok,message) => { if (!ok) throw new InvalidOperationException("Clothing core: " + message); passed++; Console.WriteLine("  OK " + message); };
        var demo = DemoFactory.Create(Path.Combine(directory,"geometry"));
        string input = Path.Combine(directory,"input"); Directory.CreateDirectory(input);
        string model = Path.Combine(input,"jbib_006_u.ydr"), texture = Path.Combine(input,"jbib_diff_006_a_uni.ytd");
        File.Copy(Path.Combine(demo[0],"stream","seam_tower.ydr"),model);
        File.Copy(Path.Combine(demo[0],"stream","seam_materials.ytd"),texture);
        File.Copy(model,Path.Combine(input,"p_head_018.ydr"));
        File.Copy(texture,Path.Combine(input,"p_head_diff_018_a.ytd"));
        var project = new ClothingProject { Name="Native clothing regression" }; project.ImportFolder(input);
        check(project.Items.Count==2,"native component and prop models import without generic map models");
        var top=project.Items.Single(i=>!i.IsProp); var hat=project.Items.Single(i=>i.IsProp);
        check(top.Component==11 && top.OriginalDrawableId==6 && top.DrawableId==0 && top.TexturePaths.Count==1,"clothing slot, original ID, local ID and companion textures resolve");
        check(hat.Component==0 && hat.DrawableId==0 && hat.TexturePaths.Count==1,"head prop and prop diffuse texture resolve separately");
        project.ImportFiles(new[]{model,texture});check(project.Items.Count==2 && top.TexturePaths.Count==1,"repeated imports do not duplicate clothing or texture entries");
        string artwork=Path.Combine(input,"ink.png");
        var pixels=new byte[32*32*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=20;pixels[i+1]=40;pixels[i+2]=80;pixels[i+3]=128;}
        var bitmap=BitmapSource.Create(32,32,96,96,PixelFormats.Bgra32,null,pixels,32*4);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(artwork))encoder.Save(stream);
        project.Tattoos.Add(new TattooDesign{Name="Test ink",SourceImagePath=artwork,Ped="female",Zone="ZONE_LEFT_ARM",U=.4,V=.6,ScaleX=.2,ScaleY=.3,Rotation=.5});
        string projectPath=Path.Combine(directory,"wardrobe.seamcloth");project.Save(projectPath);project=ClothingProject.Load(projectPath);
        check(project.Items.Count==2 && project.Tattoos.Single().ScaleY==.3 && project.Items[0].SourcePath==top.SourcePath,"project persistence retains source paths and editable tattoo placement");
        var hashes=Directory.EnumerateFiles(input).ToDictionary(p=>p,Hash);
        string exports=Path.Combine(directory,"exports");Directory.CreateDirectory(exports);
        string result=project.ExportFiveM(exports,"native_clothes");
        check(File.Exists(Path.Combine(result,"fxmanifest.lua")) && File.Exists(Path.Combine(result,"seam-clothing-build.json")),"FiveM export includes a manifest and build map");
        string prefix="mp_m_freemode_01_mp_m_native_clothes";
        var ydd=new YddFile();ydd.Load(File.ReadAllBytes(Path.Combine(result,"stream",prefix+"^jbib_000_u.ydd")));
        check(ydd.DrawableDict?.Drawables?.data_items?.Length==1,"standalone drawable converts to a reloadable clothing YDD");
        check(Hash(Path.Combine(result,"stream",prefix+"^jbib_diff_000_a_uni.ytd"))==hashes[texture],"clothing texture dictionary pixels and bytes are copied unchanged");
        var ymt=new YmtFile();ymt.Load(File.ReadAllBytes(Path.Combine(result,"stream",prefix+".ymt")));
        var definition=new XmlDocument();definition.LoadXml(MetaXml.GetXml(ymt.Meta));
        check(definition.DocumentElement.Name=="CPedVariationInfo" && definition.SelectNodes("/CPedVariationInfo/aComponentData3/Item/aDrawblData3/Item").Count==1,"native ped variation YMT retains one component drawable");
        check(definition.SelectNodes("/CPedVariationInfo/propInfo/aPropMetaData/Item").Count==1 && definition.SelectSingleNode("/CPedVariationInfo/propInfo/aAnchors/Item/props").InnerText.Trim()=="1","native prop metadata and anchor texture counts match");
        var availability=definition.SelectSingleNode("/CPedVariationInfo/availComp").InnerText.Split((char[])null,StringSplitOptions.RemoveEmptyEntries);
        check(availability.Length==12 && availability[11]=="0" && availability[0]=="255","component availability maps the top slot without inventing other components");
        string shop=File.ReadAllText(Path.Combine(result,"data",prefix+"_shop.meta"));
        check(shop.Contains("localDrawableIndex") && shop.Contains("localPropIndex") && shop.Contains("mp_m_native_clothes"),"apparel metadata links collection-local components and props");
        var overlays=new XmlDocument();overlays.Load(Path.Combine(result,"data","native_clothes_overlays.xml"));
        check(overlays.SelectSingleNode("/PedDecorationCollection/presets/Item/zone").InnerText=="ZONE_LEFT_ARM" && ((XmlElement)overlays.SelectSingleNode("/PedDecorationCollection/presets/Item/uvPos")).GetAttribute("x")=="0.4","tattoo projection values are exported in native overlay metadata");
        string tattooPath=Directory.GetFiles(Path.Combine(result,"stream"),"native_clothes_seam_test_ink_*.ytd").Single();
        var tattoo=new YtdFile();tattoo.Load(File.ReadAllBytes(tattooPath));var ink=tattoo.TextureDict.Textures.data_items.Single();
        check(ink.Width==32 && ink.Height==32 && ink.Data.FullData[((ink.Height/2)*ink.Width+ink.Width/2)*4+3]==128,"tattoo artwork exports to native YTD with its central alpha intact after rotation baking");
        check(((XmlElement)overlays.SelectSingleNode("/PedDecorationCollection/presets/Item/rotation")).GetAttribute("value")=="0","tattoo degree rotation is baked into pixels without guessing native angle units");
        string again=project.ExportFiveM(exports,"native_clothes");
        check(Path.GetFileName(again)=="native_clothes_2" && Directory.GetFiles(Path.Combine(again,"stream"),"*mp_m_native_clothes_2.ymt").Length==1,"repeat builds preserve old output and create distinct collection names");
        string singleplayer=project.ExportSinglePlayer(exports,"native_sp");
        var rpf=new RpfFile(Path.Combine(singleplayer,"dlc.rpf"),"dlc.rpf");rpf.ScanStructure(null,null);
        check(rpf.LastException==null && rpf.Children.Count==2 && File.Exists(Path.Combine(singleplayer,"INSTALL.txt")),"Singleplayer export creates reloadable nested DLC archives and install instructions");
        var setup=rpf.AllEntries.OfType<RpfFileEntry>().Single(e=>e.Name=="setup2.xml");
        var setupXml=new XmlDocument();setupXml.LoadXml(System.Text.Encoding.UTF8.GetString(rpf.ExtractFile(setup)));
        check(setupXml.SelectSingleNode("/SSetupData/deviceName").InnerText=="dlc_native_sp","Singleplayer setup links its generated DLC device");
        check(hashes.All(p=>Hash(p.Key)==p.Value),"building either platform leaves all original clothing and artwork unchanged");
        bool blocked=false;top=project.Items.Single(i=>!i.IsProp);top.DrawableId=4;try{project.ExportFiveM(exports,"bad_ids");}catch(InvalidDataException){blocked=true;}
        check(blocked && !Directory.Exists(Path.Combine(exports,"bad_ids")),"invalid local index gaps fail before any output commits");
        project.Reindex();check(top.DrawableId==0,"reindex restores contiguous local IDs");
        project.Tattoos[0].Rotation=90;
        string rotated=project.ExportFiveM(exports,"rotated_ink");
        var rotatedXml=new XmlDocument();rotatedXml.Load(Path.Combine(rotated,"data","rotated_ink_overlays.xml"));
        var scale=(XmlElement)rotatedXml.SelectSingleNode("/PedDecorationCollection/presets/Item/scale");
        check(Math.Abs(double.Parse(scale.GetAttribute("x"),System.Globalization.CultureInfo.InvariantCulture)-.3)<.00001 && Math.Abs(double.Parse(scale.GetAttribute("y"),System.Globalization.CultureInfo.InvariantCulture)-.2)<.00001,"ninety-degree tattoo rotation preserves a non-square projection footprint");
        string protectedPath=Path.Combine(input,"feet_004_u.ydd");File.WriteAllText(protectedPath,"CFX protected fixture");project.ImportFiles(new[]{protectedPath});
        blocked=false;try{project.ExportFiveM(exports,"protected_clothes");}catch(InvalidDataException){blocked=true;}
        check(blocked,"protected clothing is reported instead of renamed into a broken addon pack");
        return passed;
    }
    private static string Hash(string path){using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return Convert.ToBase64String(sha.ComputeHash(stream));}
}
