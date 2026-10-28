using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using CodeWalker.GameFiles;

namespace Seam
{
    public sealed class ClothingItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string SourcePath { get; set; }
        public string Ped { get; set; } = "male";
        public int Component { get; set; } = 11;
        public bool IsProp { get; set; }
        public int DrawableId { get; set; }
        public int OriginalDrawableId { get; set; }
        public string SkinToneSuffix { get; set; } = "u";
        public bool Enabled { get; set; } = true;
        public List<string> TexturePaths { get; set; } = new List<string>();
        public List<string> AlternateModelPaths { get; set; } = new List<string>();
        public string PhysicsPath { get; set; }
        public string Error { get; set; }
        [ScriptIgnore] public string Summary => (Ped == "female" ? "Female" : "Male") + " · " + ClothingProject.ComponentLabel(Component, IsProp) + " · " + TexturePaths.Count + " textures";
    }

    public sealed class TattooDesign
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New tattoo";
        public string Ped { get; set; } = "male";
        public string Zone { get; set; } = "ZONE_TORSO";
        public string SourceImagePath { get; set; }
        public string ArtworkPath { get; set; }
        public string TexturePath { get; set; }
        public string TextureName { get; set; }
        public double U { get; set; } = 0.5;
        public double V { get; set; } = 0.5;
        public double ScaleX { get; set; } = 0.2;
        public double ScaleY { get; set; } = 0.2;
        public double Rotation { get; set; }
        public double Opacity { get; set; } = 1;
        public bool Enabled { get; set; } = true;
        [ScriptIgnore] public string Summary => (Ped == "female" ? "Female" : Ped == "both" ? "Both" : "Male") + " · " + Zone;
    }

    public sealed class ClothingProject
    {
        public const int FormatVersion = 1;
        public int Version { get; set; } = FormatVersion;
        public string Name { get; set; } = "My clothing pack";
        public List<ClothingItem> Items { get; set; } = new List<ClothingItem>();
        public List<TattooDesign> Tattoos { get; set; } = new List<TattooDesign>();
        [ScriptIgnore] public List<string> Notices { get; } = new List<string>();
        [ScriptIgnore] public string LastExportPath { get; private set; }
        public static readonly string[] ComponentNames = { "head", "berd", "hair", "uppr", "lowr", "hand", "feet", "teef", "accs", "task", "decl", "jbib" };
        public static readonly string[] PropNames = { "head", "eyes", "ears", "mouth", "lhand", "rhand", "lwrist", "rwrist", "hip", "lfoot", "rfoot", "unk1", "unk2" };
        private static readonly string[] Anchors = { "ANCHOR_HEAD", "ANCHOR_EYES", "ANCHOR_EARS", "ANCHOR_MOUTH", "ANCHOR_LEFT_HAND", "ANCHOR_RIGHT_HAND", "ANCHOR_LEFT_WRIST", "ANCHOR_RIGHT_WRIST", "ANCHOR_HIP", "ANCHOR_LEFT_FOOT", "ANCHOR_RIGHT_FOOT", "ANCHOR_PH_L_HAND", "ANCHOR_PH_R_HAND" };
        private static readonly Regex ModelName = new Regex(@"^(?<kind>p_[a-z0-9]+|head|berd|hair|uppr|lowr|hand|feet|teef|accs|task|decl|jbib)_(?<number>\d{3})(?:_(?<skin>[urm]))?(?<alt>_[1-3])?$", RegexOptions.IgnoreCase);
        private static readonly Regex TextureName = new Regex(@"^(?<kind>p_[a-z0-9]+|[a-z0-9]+)_diff_(?<number>\d{3})_(?<letter>[a-z])(?:_(?<race>[a-z]+))?$", RegexOptions.IgnoreCase);
        private static readonly HashSet<string> Zones = new HashSet<string>(new[] { "ZONE_TORSO", "ZONE_HEAD", "ZONE_LEFT_ARM", "ZONE_RIGHT_ARM", "ZONE_LEFT_LEG", "ZONE_RIGHT_LEG" }, StringComparer.Ordinal);

        public static string ComponentLabel(int component, bool prop) => component >= 0 && component < (prop ? PropNames.Length : ComponentNames.Length) ? (prop ? "Prop · " + PropNames[component] : ComponentNames[component]) : "Unknown component";
        public static string PedName(string ped) => ped == "female" ? "mp_f_freemode_01" : "mp_m_freemode_01";

        public void ImportFolder(string path) => ImportFiles(EnumerateFiles(Path.GetFullPath(path)));
        public void ImportFiles(IEnumerable<string> paths)
        {
            if (paths == null) return;
            var selected = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var files = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            foreach (var directory in selected.Where(File.Exists).Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var file in Directory.EnumerateFiles(directory)) files.Add(file);
            var known = new HashSet<string>(Items.Select(i => i.SourcePath), StringComparer.OrdinalIgnoreCase);
            foreach (var path in selected.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) throw new FileNotFoundException("Clothing source not found.", path);
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension != ".ydd" && extension != ".ydr") continue;
                var match = ModelName.Match(Stem(path));
                if (!match.Success || match.Groups["alt"].Success) { Notices.Add("Model name could not be resolved automatically: " + Path.GetFileName(path)); continue; }
                if (!known.Add(path)) continue;
                string kind = match.Groups["kind"].Value.ToLowerInvariant();
                bool prop = kind.StartsWith("p_", StringComparison.Ordinal);
                int component = Array.IndexOf(prop ? PropNames : ComponentNames, prop ? kind.Substring(2) : kind);
                if (component < 0) { Notices.Add("Unknown drawable type: " + kind); continue; }
                string ped = GuessPed(path);
                int original = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
                var item = new ClothingItem { Name = Path.GetFileNameWithoutExtension(path), SourcePath = path, Ped = ped, Component = component, IsProp = prop,
                    DrawableId = Items.Count(i => i.Ped == ped && i.Component == component && i.IsProp == prop), OriginalDrawableId = original,
                    SkinToneSuffix = match.Groups["skin"].Success ? match.Groups["skin"].Value.ToLowerInvariant() : "u" };
                foreach (var texture in files.Where(p => Path.GetDirectoryName(p).Equals(Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase) && Path.GetExtension(p).Equals(".ytd", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var t = TextureName.Match(Stem(texture));
                    if (t.Success && t.Groups["kind"].Value.Equals(kind, StringComparison.OrdinalIgnoreCase) && int.Parse(t.Groups["number"].Value, CultureInfo.InvariantCulture) == original) item.TexturePaths.Add(texture);
                }
                string sourceBase = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
                if (File.Exists(sourceBase + ".yld")) item.PhysicsPath = sourceBase + ".yld";
                for (int i = 1; i <= 3; i++) if (File.Exists(sourceBase + "_" + i + extension)) item.AlternateModelPaths.Add(sourceBase + "_" + i + extension);
                try { RequireResource(path); } catch (InvalidDataException error) { item.Error = error.Message; }
                Items.Add(item);
            }
            // A later explicit texture import can attach to an already present model.
            foreach (var path in selected.Where(p => Path.GetExtension(p).Equals(".ytd", StringComparison.OrdinalIgnoreCase)))
            {
                var match = TextureName.Match(Stem(path));
                if (!match.Success) continue;
                foreach (var item in Items.Where(i => Path.GetDirectoryName(i.SourcePath).Equals(Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase)))
                    if (Prefix(item).Equals(match.Groups["kind"].Value, StringComparison.OrdinalIgnoreCase) && item.OriginalDrawableId == int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture) && !item.TexturePaths.Contains(path, StringComparer.OrdinalIgnoreCase)) item.TexturePaths.Add(path);
            }
        }

        public void Reindex()
        {
            foreach (var group in Items.Where(i => i.Enabled).GroupBy(i => i.Ped + ":" + i.IsProp + ":" + i.Component))
            { int index = 0; foreach (var item in group) item.DrawableId = index++; }
        }

        public void Save(string path)
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, Json().Serialize(this), new UTF8Encoding(false));
                if (File.Exists(full)) File.Replace(temporary, full, null); else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static ClothingProject Load(string path)
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Project exceeds the 16 MB project limit.");
            var project = Json().Deserialize<ClothingProject>(File.ReadAllText(path));
            if (project == null || project.Version != FormatVersion || project.Items == null || project.Tattoos == null) throw new InvalidDataException("Unsupported Clothing Studio project.");
            foreach (var item in project.Items) { item.TexturePaths = item.TexturePaths ?? new List<string>(); item.AlternateModelPaths = item.AlternateModelPaths ?? new List<string>(); }
            return project;
        }

        public string ExportFiveM(string parentDirectory, string resourceName)
        {
            Validate(resourceName);
            string parent = Path.GetFullPath(parentDirectory);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose an existing build destination.");
            foreach (string source in SourcePaths())
                if (Inside(Path.GetDirectoryName(Path.GetFullPath(source)), parent)) throw new InvalidOperationException("Build outside your imported source folders.");
            string final = UniqueFolder(parent, resourceName);
            string buildName = Path.GetFileName(final);
            string stage = Path.Combine(parent, ".seam-clothing-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(stage, "stream"));
                Directory.CreateDirectory(Path.Combine(stage, "data"));
                var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Action<string, byte[]> add = (name, bytes) =>
                {
                    if (!written.Add(name)) throw new InvalidDataException("Output filename collision: " + name);
                    string target = Path.GetFullPath(Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!Inside(stage, target)) throw new InvalidDataException("Output path escaped its resource.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllBytes(target, bytes);
                };
                var manifests = new List<string>();
                var snapshot = SourcePaths().Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, FileHash, StringComparer.OrdinalIgnoreCase);
                foreach (var group in Items.Where(i => i.Enabled).GroupBy(i => i.Ped))
                {
                    string dlc = (group.Key == "female" ? "mp_f_" : "mp_m_") + buildName;
                    string collection = PedName(group.Key) + "_" + dlc;
                    var items = group.ToList();
                    foreach (var item in items)
                    {
                        string stem = Prefix(item) + "_" + item.DrawableId.ToString("000") + (item.IsProp ? "" : "_" + item.SkinToneSuffix);
                        var modelBytes = ClothingModel(item.SourcePath, stem);
                        add("stream/" + collection + "^" + stem + ".ydd", modelBytes);
                        for (int n = 0; n < item.TexturePaths.Count; n++)
                        {
                            string texture = item.TexturePaths[n]; var match = TextureName.Match(Stem(texture));
                            string race = match.Success && match.Groups["race"].Success ? match.Groups["race"].Value.ToLowerInvariant() : "uni";
                            string target = Prefix(item) + "_diff_" + item.DrawableId.ToString("000") + "_" + (char)('a' + n) + (item.IsProp ? "" : "_" + race);
                            var bytes = RequireResource(texture); var ytd = new YtdFile(); ytd.Load(bytes);
                            if (ytd.TextureDict == null) throw new InvalidDataException("Unreadable texture dictionary: " + texture);
                            add("stream/" + collection + "^" + target + ".ytd", bytes);
                        }
                        if (!string.IsNullOrEmpty(item.PhysicsPath)) add("stream/" + collection + "^" + stem + ".yld", RequireResource(item.PhysicsPath));
                        for (int n = 0; n < item.AlternateModelPaths.Count; n++) add("stream/" + collection + "^" + stem + "_" + (n + 1) + ".ydd", ClothingModel(item.AlternateModelPaths[n], stem + "_" + (n + 1)));
                    }
                    var definition = BuildVariation(dlc, items);
                    add("stream/" + collection + ".ymt", definition);
                    string shopPath = "data/" + collection + "_shop.meta";
                    add(shopPath, Encoding.UTF8.GetBytes(BuildShop(group.Key, dlc, collection, items)));
                    manifests.Add("data_file 'SHOP_PED_APPAREL_META_FILE' '" + shopPath + "'");
                }
                if (Tattoos.Any(t => t.Enabled))
                {
                    string collection = buildName + "_overlays";
                    var overlays = Document("PedDecorationCollection");
                    Number(overlays.DocumentElement, "bRequiredForSync", "true");
                    Text(overlays.DocumentElement, "nameHash", collection);
                    var list = Child(overlays.DocumentElement, "presets");
                    int n = 0;
                    foreach (var tattoo in Tattoos.Where(t => t.Enabled))
                    {
                        string textureName = "seam_" + SafeToken(tattoo.Name) + "_" + n++;
                        string txdName = buildName + "_" + textureName;
                        byte[] bytes; double scaleX = tattoo.ScaleX, scaleY = tattoo.ScaleY;
                        if (!string.IsNullOrEmpty(tattoo.SourceImagePath)) bytes = TattooTexture(tattoo, textureName, out scaleX, out scaleY);
                        else
                        {
                            if (Math.Abs(tattoo.Rotation % 360) > 0.000001 || tattoo.Opacity != 1) throw new InvalidDataException("Supply flattened PNG artwork to rotate or change the opacity of a tattoo.");
                            bytes = RequireResource(tattoo.TexturePath);
                            var ytd = new YtdFile(); ytd.Load(bytes);
                            if (ytd.TextureDict?.Textures?.data_items?.Length != 1) throw new InvalidDataException("A tattoo YTD must contain one texture, or supply a flattened PNG.");
                            textureName = ytd.TextureDict.Textures.data_items[0].Name;
                        }
                        add("stream/" + txdName + ".ytd", bytes);
                        var entry = Child(list, "Item");
                        var uv = Child(entry, "uvPos"); Attr(uv, "x", tattoo.U); Attr(uv, "y", tattoo.V);
                        var scale = Child(entry, "scale"); Attr(scale, "x", scaleX); Attr(scale, "y", scaleY);
                        // UI angles are degrees. Bake their complete affine transform
                        // into the texture and use the verified native zero rotation;
                        // this does not guess GTA's otherwise unverified angle unit.
                        Number(entry, "rotation", 0);
                        Text(entry, "nameHash", "SEAM_" + SafeToken(tattoo.Name).ToUpperInvariant() + "_" + (n - 1));
                        Text(entry, "txdHash", txdName); Text(entry, "txtHash", textureName); Text(entry, "zone", tattoo.Zone);
                        Text(entry, "type", "TYPE_TATTOO"); Text(entry, "faction", "FM"); Text(entry, "garment", "All");
                        Text(entry, "gender", tattoo.Ped == "both" ? "GENDER_DONTCARE" : tattoo.Ped == "female" ? "GENDER_FEMALE" : "GENDER_MALE");
                        Child(entry, "award"); Child(entry, "awardLevel");
                    }
                    Child(overlays.DocumentElement, "medalLocations").SetAttribute("content", "vector2_array");
                    string overlayPath = "data/" + collection + ".xml";
                    add(overlayPath, Encoding.UTF8.GetBytes(overlays.OuterXml));
                    manifests.Add("data_file 'PED_OVERLAY_FILE' '" + overlayPath + "'");
                }
                File.WriteAllText(Path.Combine(stage, "fxmanifest.lua"), "fx_version 'cerulean'\ngame 'gta5'\n\nfiles { 'data/*.meta', 'data/*.xml' }\n" + string.Join("\n", manifests) + "\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(stage, "seam-clothing-build.json"), Json().Serialize(new { application = "Seam Clothing Studio", target = "FiveM Legacy", resource = Path.GetFileName(final), project = Name,
                    clothing = Items.Where(i => i.Enabled).Select(i => new { i.Name, i.Ped, i.Component, i.IsProp, i.DrawableId, textures = i.TexturePaths.Count }).ToArray(),
                    tattoos = Tattoos.Where(t => t.Enabled).ToArray(), notices = Notices.ToArray() }), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(stage, "README.txt"), "Seam Clothing Studio — FiveM Legacy addon resource\r\n\r\nAdd ensure " + Path.GetFileName(final) + " to your server configuration.\r\nCollection names: " + string.Join(", ", Items.Where(i => i.Enabled).Select(i => (i.Ped == "female" ? "mp_f_" : "mp_m_") + buildName).Distinct()) + ".\r\nUse collection-based clothing natives with local IDs from seam-clothing-build.json.\r\nTattoo overlays require your tattoo script to call AddPedDecorationFromHashes using this resource's overlay collection and generated overlay name.\r\nTest in game. Base-game and other resources still consume clothing definition/streaming limits.\r\n", new UTF8Encoding(false));
                foreach (var source in snapshot) if (FileHash(source.Key) != source.Value) throw new IOException("Source changed during build: " + source.Key);
                Commit(stage, final); LastExportPath = final; return final;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }

        public string ExportSinglePlayer(string parentDirectory, string packName)
        {
            Validate(packName);
            string parent = Path.GetFullPath(parentDirectory);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose an existing build destination.");
            string final = UniqueFolder(parent, packName + "_singleplayer");
            string stage = Path.Combine(parent, ".seam-dlc-" + Guid.NewGuid().ToString("N"));
            string work = Path.Combine(Path.GetTempPath(), "seam-clothing-" + Guid.NewGuid().ToString("N"));
            string previousExport = LastExportPath;
            try
            {
                Directory.CreateDirectory(stage);
                Directory.CreateDirectory(work);
                // Reuse the validated build, then place its native assets in DLC archives.
                string resource = ExportFiveM(work, packName);
                var archive = RpfFile.CreateNew(stage, "dlc.rpf", RpfEncryption.OPEN);
                var x64 = RpfFile.CreateDirectory(archive.Root, "x64");
                var models = RpfFile.CreateDirectory(x64, "models");
                var cdimages = RpfFile.CreateDirectory(models, "cdimages");
                var common = RpfFile.CreateDirectory(archive.Root, "common");
                var data = RpfFile.CreateDirectory(common, "data");
                var archives = new Dictionary<string, RpfFile>(StringComparer.OrdinalIgnoreCase);
                var folders = new Dictionary<string, RpfDirectoryEntry>(StringComparer.OrdinalIgnoreCase);
                var virtualFiles = new List<Tuple<string, string, bool>>();
                string device = "dlc_" + packName;
                foreach (var file in Directory.EnumerateFiles(Path.Combine(resource, "stream")))
                {
                    string name = Path.GetFileName(file), shortName = name;
                    int caret = name.IndexOf('^');
                    string collection = caret >= 0 ? name.Substring(0, caret) : Path.GetFileNameWithoutExtension(name);
                    bool female = collection.StartsWith("mp_f_freemode_01", StringComparison.OrdinalIgnoreCase);
                    bool clothing = caret >= 0 || name.EndsWith(".ymt", StringComparison.OrdinalIgnoreCase);
                    string archiveName = packName + (clothing ? female ? "_female.rpf" : "_male.rpf" : "_tattoos.rpf");
                    if (!archives.TryGetValue(archiveName, out var targetArchive))
                    {
                        archives[archiveName] = targetArchive = RpfFile.CreateNew(cdimages, archiveName, RpfEncryption.OPEN);
                        virtualFiles.Add(Tuple.Create(device + ":/x64/models/cdimages/" + archiveName, "RPF_FILE", true));
                    }
                    RpfDirectoryEntry target = targetArchive.Root;
                    if (caret >= 0)
                    {
                        string key = archiveName + ":" + collection;
                        if (!folders.TryGetValue(key, out target)) folders[key] = target = RpfFile.CreateDirectory(targetArchive.Root, collection);
                        shortName = name.Substring(caret + 1);
                    }
                    RpfFile.CreateFile(target, shortName, File.ReadAllBytes(file), false);
                }
                foreach (var file in Directory.EnumerateFiles(Path.Combine(resource, "data")))
                {
                    string name = Path.GetFileName(file);
                    RpfFile.CreateFile(data, name, File.ReadAllBytes(file), false);
                    virtualFiles.Add(Tuple.Create(device + "CRC:/common/data/" + name, name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? "SHOP_PED_APPAREL_META_FILE" : "PED_OVERLAY_FILE", false));
                }
                string changeName = packName.ToUpperInvariant() + "_AUTOGEN";
                var contents = Document("CDataFileMgr__ContentsOfDataFileXml"); var root = contents.DocumentElement;
                Child(root, "disabledFiles"); Child(root, "includedXmlFiles"); Child(root, "includedDataFiles");
                var entries = Child(root, "dataFiles");
                foreach (var file in virtualFiles)
                {
                    var entry = Child(entries, "Item"); Text(entry, "filename", file.Item1); Text(entry, "fileType", file.Item2);
                    Number(entry, "overlay", "false"); Number(entry, "disabled", "true"); Number(entry, "persistent", file.Item3 ? "true" : "false");
                }
                var change = Child(Child(root, "contentChangeSets"), "Item"); Text(change, "changeSetName", changeName);
                var enable = Child(change, "filesToEnable"); foreach (var file in virtualFiles) Text(enable, "Item", file.Item1);
                Child(change, "filesToDisable"); Child(change, "txdToLoad"); Child(change, "txdToUnload"); Child(change, "residentResources"); Child(change, "unregisterResources"); Child(root, "patchFiles");
                RpfFile.CreateFile(archive.Root, "content.xml", Encoding.UTF8.GetBytes(contents.OuterXml), false);
                var setup = Document("SSetupData"); root = setup.DocumentElement;
                Text(root, "deviceName", device); Text(root, "datFile", "content.xml"); Text(root, "timeStamp", "01/01/2000 00:00:00"); Text(root, "nameHash", packName.ToUpperInvariant());
                var startup = Child(Child(root, "contentChangeSetGroups"), "Item"); Text(startup, "NameHash", "GROUP_STARTUP"); Text(Child(startup, "ContentChangeSets"), "Item", changeName);
                Text(root, "type", "EXTRACONTENT_COMPAT_PACK"); Number(root, "order", 1000); Number(root, "minorOrder", 0); Number(root, "isLevelPack", "false"); Number(root, "subPackCount", 0);
                RpfFile.CreateFile(archive.Root, "setup2.xml", Encoding.UTF8.GetBytes(setup.OuterXml), false);
                var validation = new RpfFile(Path.Combine(stage, "dlc.rpf"), "dlc.rpf"); validation.ScanStructure(null, null);
                if (validation.LastException != null || validation.Root == null || validation.Children.Count != archives.Count) throw new InvalidDataException("Singleplayer DLC failed archive reload.", validation.LastException);
                string finished = Path.Combine(stage, "finished"); Directory.CreateDirectory(finished);
                File.Move(Path.Combine(stage, "dlc.rpf"), Path.Combine(finished, "dlc.rpf"));
                var buildInfo = Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(resource, "seam-clothing-build.json")));
                buildInfo["target"] = "Singleplayer Legacy DLC";
                File.WriteAllText(Path.Combine(finished, "seam-clothing-build.json"), Json().Serialize(buildInfo), new UTF8Encoding(false));
                string folderName = Path.GetFileName(final);
                File.WriteAllText(Path.Combine(finished, "INSTALL.txt"), "Copy dlc.rpf to mods/update/x64/dlcpacks/" + folderName + "/dlc.rpf using your normal Singleplayer mod workflow.\r\nAdd <Item>dlcpacks:/" + folderName + "/</Item> to your mods copy of dlclist.xml.\r\nThis builder does not edit your game installation. Test the pack and its clothing/prop limits on your game build.\r\n", new UTF8Encoding(false));
                Commit(finished, final); LastExportPath = final; return final;
            }
            finally
            {
                if (LastExportPath != final) LastExportPath = previousExport;
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                if (Directory.Exists(work)) Directory.Delete(work, true);
            }
        }

        public List<string> Validate(string resourceName)
        {
            if (!Regex.IsMatch(resourceName ?? "", "^[a-z][a-z0-9_]{0,39}$")) throw new InvalidDataException("Use a resource name beginning with a lowercase letter, followed by up to 39 lowercase letters, digits or underscores.");
            var enabled = Items.Where(i => i.Enabled).ToList();
            if (enabled.Count == 0 && !Tattoos.Any(t => t.Enabled)) throw new InvalidDataException("Add clothing or a tattoo before building.");
            foreach (var item in enabled)
            {
                if (item.Ped != "male" && item.Ped != "female") throw new InvalidDataException("Choose male or female for " + item.Name);
                if (item.Component < 0 || item.Component >= (item.IsProp ? PropNames.Length : ComponentNames.Length)) throw new InvalidDataException("Invalid component for " + item.Name);
                if (!item.IsProp && item.SkinToneSuffix != "u" && item.SkinToneSuffix != "r" && item.SkinToneSuffix != "m") throw new InvalidDataException("Invalid skin-tone suffix for " + item.Name);
                if (item.TexturePaths.Count > 26) throw new InvalidDataException(item.Name + " exceeds 26 texture variations.");
                if (item.AlternateModelPaths.Count > 3 || item.AlternateModelPaths.Select((p, n) => ModelName.Match(Stem(p)).Groups["alt"].Value == "_" + (n + 1)).Any(ok => !ok))
                    throw new InvalidDataException("Alternation models must contain a consecutive _1, _2, _3 sequence: " + item.Name);
                if (!string.IsNullOrEmpty(item.Error)) throw new InvalidDataException(item.Name + ": " + item.Error);
                foreach (var path in new[] { item.SourcePath, item.PhysicsPath }.Concat(item.TexturePaths).Concat(item.AlternateModelPaths).Where(p => !string.IsNullOrEmpty(p)))
                    if (!File.Exists(path)) throw new FileNotFoundException("Missing clothing source.", path);
            }
            foreach (var group in enabled.GroupBy(i => i.Ped + ":" + i.IsProp + ":" + i.Component))
            {
                var ids = group.Select(i => i.DrawableId).OrderBy(i => i).ToArray();
                if (ids.Length > 255 || !ids.SequenceEqual(Enumerable.Range(0, ids.Length))) throw new InvalidDataException("Local IDs must be consecutive from zero, with at most 255 drawables per type. Reindex the clothing before building.");
            }
            foreach (var group in enabled.Where(i => i.IsProp).GroupBy(i => i.Ped)) if (group.Count() > 255) throw new InvalidDataException("A collection supports at most 255 props per gender.");
            foreach (var tattoo in Tattoos.Where(t => t.Enabled))
            {
                if (!Zones.Contains(tattoo.Zone ?? "") || !new[] { "male", "female", "both" }.Contains(tattoo.Ped)) throw new InvalidDataException("Choose a valid tattoo body zone and gender.");
                if (!Finite(tattoo.U) || !Finite(tattoo.V) || !Finite(tattoo.ScaleX) || !Finite(tattoo.ScaleY) || !Finite(tattoo.Rotation) || !Finite(tattoo.Opacity) || tattoo.Opacity < 0 || tattoo.Opacity > 1 || tattoo.ScaleX <= 0 || tattoo.ScaleY <= 0) throw new InvalidDataException("Tattoo coordinates and scales must be finite, scale must be positive, and opacity must be between zero and one.");
                if (!File.Exists(tattoo.SourceImagePath) && !File.Exists(tattoo.TexturePath)) throw new FileNotFoundException("Missing tattoo artwork: " + tattoo.Name);
            }
            return Notices;
        }

        public static byte[] BuildVariation(string collection, List<ClothingItem> items)
        {
            var document = Document("CPedVariationInfo"); var root = document.DocumentElement; root.SetAttribute("name", collection);
            Number(root, "bHasTexVariations", "true"); Number(root, "bHasDrawblVariations", "true"); Number(root, "bHasLowLODs", "false"); Number(root, "bIsSuperLOD", "false");
            var indices = Enumerable.Repeat(255, 12).ToArray();
            var components = Child(root, "aComponentData3"); components.SetAttribute("itemType", "CPVComponentData");
            var infos = Child(root, "compInfos"); infos.SetAttribute("itemType", "CComponentInfo");
            int componentIndex = 0;
            foreach (var group in items.Where(i => !i.IsProp).GroupBy(i => i.Component).OrderBy(g => g.Key))
            {
                indices[group.Key] = componentIndex++;
                var component = Child(components, "Item"); Number(component, "numAvailTex", group.Sum(i => Math.Max(1, i.TexturePaths.Count)) & 255);
                var draws = Child(component, "aDrawblData3"); draws.SetAttribute("itemType", "CPVDrawblData");
                foreach (var item in group.OrderBy(i => i.DrawableId))
                {
                    var draw = Child(draws, "Item"); Number(draw, "propMask", item.SkinToneSuffix == "r" ? 17 : item.SkinToneSuffix == "m" ? 33 : 1); Number(draw, "numAlternatives", item.AlternateModelPaths.Count);
                    var textures = Child(draw, "aTexData"); textures.SetAttribute("itemType", "CPVTextureData");
                    for (int t = 0; t < Math.Max(1, item.TexturePaths.Count); t++) { var texture = Child(textures, "Item"); Number(texture, "texId", t < item.TexturePaths.Count ? RaceId(item.TexturePaths[t]) : 0); Number(texture, "distribution", 255); }
                    Number(Child(draw, "clothData"), "ownsCloth", string.IsNullOrEmpty(item.PhysicsPath) ? "false" : "true");
                    var info = Child(infos, "Item");
                    Text(info, "hash_2FD08CEF", "none"); Text(info, "hash_FC507D28", "none"); Text(info, "hash_07AE529D", "0 0 0 0 0");
                    Number(info, "flags", 0); Text(info, "inclusions", "0"); Text(info, "exclusions", "0"); Text(info, "hash_6032815C", "PV_COMP_HEAD");
                    Number(info, "hash_7E103C8B", 0); Number(info, "hash_D12F579D", item.Component); Number(info, "hash_FA1F27BF", item.DrawableId);
                }
            }
            Text(root, "availComp", string.Join(" ", indices));
            var sets = Child(root, "aSelectionSets"); sets.SetAttribute("itemType", "CPedSelectionSet");
            var propInfo = Child(root, "propInfo"); var props = items.Where(i => i.IsProp).OrderBy(i => i.Component).ThenBy(i => i.DrawableId).ToArray();
            Number(propInfo, "numAvailProps", props.Length);
            var propData = Child(propInfo, "aPropMetaData"); propData.SetAttribute("itemType", "CPedPropMetaData");
            var anchors = Child(propInfo, "aAnchors"); anchors.SetAttribute("itemType", "CAnchorProps");
            for (int i = 0; i < props.Length; i++)
            {
                var item = props[i]; var data = Child(propData, "Item"); Text(data, "audioId", "none"); Text(data, "expressionMods", "0 0 0 0 0");
                var texData = Child(data, "texData"); texData.SetAttribute("itemType", "CPedPropTexData");
                for (int n = 0; n < Math.Max(1, item.TexturePaths.Count); n++) { var tex = Child(texData, "Item"); Text(tex, "inclusions", "0"); Text(tex, "exclusions", "0"); Number(tex, "texId", n); Number(tex, "inclusionId", 0); Number(tex, "exclusionId", 0); Number(tex, "distribution", 255); }
                Child(data, "renderFlags"); Number(data, "propFlags", 65569); Number(data, "flags", 1); Number(data, "anchorId", item.Component); Number(data, "propId", item.DrawableId); Number(data, "stickyness", 0);
            }
            foreach (var group in props.Select((item, index) => new { item, index }).GroupBy(p => p.item.Component))
            { var anchor = Child(anchors, "Item"); Text(anchor, "props", string.Join(" ", group.Select(p => Math.Max(1, p.item.TexturePaths.Count)))); Text(anchor, "anchor", Anchors[group.Key]); }
            Text(root, "dlcName", collection);
            byte[] bytes = XmlMeta.GetRSCData(document);
            if (bytes == null) throw new InvalidDataException("CodeWalker could not compile the clothing definition.");
            var loaded = new YmtFile(); loaded.Load(bytes);
            if (loaded.Meta == null || loaded.Meta.GetRootBlock().StructureNameHash != MetaName.CPedVariationInfo) throw new InvalidDataException("Clothing definition failed native reload.");
            var roundtrip = new XmlDocument { XmlResolver = null }; roundtrip.LoadXml(MetaXml.GetXml(loaded.Meta));
            if (roundtrip.SelectNodes("/CPedVariationInfo/aComponentData3/Item/aDrawblData3/Item").Count != items.Count(i => !i.IsProp) || roundtrip.SelectNodes("/CPedVariationInfo/propInfo/aPropMetaData/Item").Count != props.Length)
                throw new InvalidDataException("Clothing definition changed its drawable count during compilation.");
            return bytes;
        }

        private static string BuildShop(string ped, string dlc, string collection, List<ClothingItem> items)
        {
            var document = Document("ShopPedApparel"); var root = document.DocumentElement;
            Text(root, "pedName", PedName(ped)); Text(root, "dlcName", dlc); Text(root, "fullDlcName", collection); Text(root, "eCharacter", ped == "female" ? "SCR_CHAR_MULTIPLAYER_F" : "SCR_CHAR_MULTIPLAYER"); Child(root, "creatureMetaData"); Child(root, "pedOutfits");
            var components = Child(root, "pedComponents"); var props = Child(root, "pedProps");
            foreach (var item in items.OrderBy(i => i.IsProp).ThenBy(i => i.Component).ThenBy(i => i.DrawableId))
                for (int texture = 0; texture < Math.Max(1, item.TexturePaths.Count); texture++)
                {
                    var entry = Child(item.IsProp ? props : components, "Item");
                    Text(entry, "lockHash", "0"); Number(entry, "cost", 0); Child(entry, "textLabel"); Text(entry, "uniqueNameHash", "SEAM_" + dlc + "_" + Prefix(item) + "_" + item.DrawableId + "_" + texture);
                    Text(entry, "eShopEnum", "CLO_SHOP_NONE"); Number(entry, "locate", -99); Number(entry, "scriptSaveData", 0); Child(entry, "restrictionTags"); Child(entry, "forcedComponents"); Child(entry, "variantComponents");
                    if (item.IsProp) { Number(entry, "propIndex", 0); Number(entry, "localPropIndex", item.DrawableId); Text(entry, "eAnchor", Anchors[item.Component]); }
                    else { Number(entry, "drawableIndex", 0); Number(entry, "localDrawableIndex", item.DrawableId); Text(entry, "eCompType", "PV_COMP_" + ComponentNames[item.Component].ToUpperInvariant()); }
                    Number(entry, "textureIndex", texture); Number(entry, "isInOutfit", "false");
                }
            return document.OuterXml;
        }

        public static byte[] TattooTexture(string imagePath, string textureName)
        {
            return TattooTexture(ReadArtwork(imagePath), textureName, 1);
        }
        private static BitmapSource ReadArtwork(string path)
        {
            BitmapSource bitmap;
            using (var stream = File.OpenRead(path)) bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            if (width != height || width < 1 || width > 4096 || (width & (width - 1)) != 0) throw new InvalidDataException("Tattoo artwork must be square, power-of-two, and no larger than 4096 pixels.");
            return bitmap;
        }
        private static byte[] TattooTexture(TattooDesign tattoo, string textureName, out double scaleX, out double scaleY)
        {
            var bitmap = ReadArtwork(tattoo.SourceImagePath);
            double angle = tattoo.Rotation % 360, radians = angle * Math.PI / 180;
            double cosine = Math.Abs(Math.Cos(radians)), sine = Math.Abs(Math.Sin(radians));
            scaleX = tattoo.ScaleX * cosine + tattoo.ScaleY * sine;
            scaleY = tattoo.ScaleX * sine + tattoo.ScaleY * cosine;
            if (Math.Abs(angle) > 0.000001)
            {
                int size = bitmap.PixelWidth;
                var matrix = Matrix.Identity;
                matrix.Translate(-0.5, -0.5); matrix.Scale(tattoo.ScaleX, tattoo.ScaleY); matrix.Rotate(angle);
                matrix.Scale(size / scaleX, size / scaleY); matrix.Translate(size * 0.5, size * 0.5);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) { context.PushTransform(new MatrixTransform(matrix)); context.DrawImage(bitmap, new System.Windows.Rect(0, 0, 1, 1)); context.Pop(); }
                var rendered = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); rendered.Render(drawing); bitmap = rendered;
            }
            return TattooTexture(bitmap, textureName, tattoo.Opacity);
        }
        private static byte[] TattooTexture(BitmapSource bitmap, string textureName, double opacity)
        {
            if (bitmap.PixelWidth > 1024) bitmap = new TransformedBitmap(bitmap, new ScaleTransform(1024.0 / bitmap.PixelWidth, 1024.0 / bitmap.PixelHeight));
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); var pixels = new byte[width * height * 4]; converted.CopyPixels(pixels, width * 4, 0);
            if (opacity != 1) for (int p = 3; p < pixels.Length; p += 4) pixels[p] = (byte)Math.Round(pixels[p] * opacity);
            var texture = new Texture { Name = textureName, NameHash = JenkHash.GenHash(textureName), Width = (ushort)width, Height = (ushort)height, Depth = 1, Stride = (ushort)(width * 4), Levels = 1, Format = TextureFormat.D3DFMT_A8R8G8B8, Data = new TextureData { FullData = pixels } };
            var dictionary = new TextureDictionary(); dictionary.BuildFromTextureList(new List<Texture> { texture });
            return new YtdFile { TextureDict = dictionary }.Save();
        }

        private static byte[] ClothingModel(string path, string name)
        {
            var bytes = RequireResource(path);
            if (Path.GetExtension(path).Equals(".ydr", StringComparison.OrdinalIgnoreCase))
            {
                var source = new YdrFile(); source.Load(bytes);
                if (source.Drawable == null) throw new InvalidDataException("Unreadable drawable: " + path);
                return new YddFile { DrawableDict = new DrawableDictionary { Hashes = new[] { JenkHash.GenHash(name) }, Drawables = new ResourcePointerArray64<Drawable> { data_items = new[] { source.Drawable } } } }.Save();
            }
            var dictionary = new YddFile(); dictionary.Load(bytes);
            if (dictionary.DrawableDict == null) throw new InvalidDataException("Unreadable clothing dictionary: " + path);
            return bytes;
        }

        private IEnumerable<string> SourcePaths() => Items.Where(i => i.Enabled).SelectMany(i => new[] { i.SourcePath, i.PhysicsPath }.Concat(i.TexturePaths).Concat(i.AlternateModelPaths)).Concat(Tattoos.Where(t => t.Enabled).Select(t => !string.IsNullOrEmpty(t.SourceImagePath) ? t.SourceImagePath : t.TexturePath)).Where(p => !string.IsNullOrEmpty(p));
        private static string Prefix(ClothingItem item) => (item.IsProp ? "p_" : "") + (item.IsProp ? PropNames[item.Component] : ComponentNames[item.Component]);
        private static int RaceId(string path) { var match = TextureName.Match(Stem(path)); string race = match.Groups["race"].Value.ToLowerInvariant(); return race == "whi" ? 1 : race == "bla" ? 2 : race == "chi" ? 3 : race == "lat" ? 4 : race == "ara" ? 5 : race == "kor" ? 8 : race == "pak" ? 10 : 0; }
        private static string Stem(string path) { string name = Path.GetFileNameWithoutExtension(path); int caret = name.LastIndexOf('^'); return caret >= 0 ? name.Substring(caret + 1) : name; }
        private static string GuessPed(string path) => path.IndexOf("mp_f_freemode", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(path, @"[\\/]female[\\/]", RegexOptions.IgnoreCase) ? "female" : "male";
        private static string SafeToken(string value) { string token = Regex.Replace((value ?? "tattoo").ToLowerInvariant(), @"[^a-z0-9_]", "_"); return token.Length == 0 ? "tattoo" : token.Substring(0, Math.Min(32, token.Length)); }
        private static IEnumerable<string> EnumerateFiles(string root) { foreach (var file in Directory.EnumerateFiles(root)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file; foreach (var dir in Directory.EnumerateDirectories(root)) if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) foreach (var file in EnumerateFiles(dir)) yield return file; }
        private static byte[] RequireResource(string path) { if (new FileInfo(path).Length > 256L * 1024 * 1024) throw new InvalidDataException("Source exceeds the 256 MB resource limit: " + path); var bytes = File.ReadAllBytes(path); if (bytes.Length < 16 || BitConverter.ToUInt32(bytes, 0) != 0x37435352) throw new InvalidDataException("Protected or unsupported asset. A new clothing collection requires readable extracted Legacy resources; keep protected assets in their original authorized resource."); return bytes; }
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static bool Inside(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static string UniqueFolder(string parent, string name) { string result = Path.Combine(parent, name); for (int i = 2; Directory.Exists(result) || File.Exists(result); i++) result = Path.Combine(parent, name + "_" + i); return result; }
        private static void Commit(string stage, string final)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Move(stage, final); return; }
                catch (IOException error) when (attempt < 5 && new[] { 5, 32, 33 }.Contains(error.HResult & 0xffff)) { System.Threading.Thread.Sleep(50 << attempt); }
            }
        }
        private static string FileHash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(stream)); }
        private static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 80 };
        private static XmlDocument Document(string root) { var doc = new XmlDocument { XmlResolver = null }; doc.AppendChild(doc.CreateXmlDeclaration("1.0", "UTF-8", null)); doc.AppendChild(doc.CreateElement(root)); return doc; }
        private static XmlElement Child(XmlElement parent, string name) { var node = parent.OwnerDocument.CreateElement(name); parent.AppendChild(node); return node; }
        private static void Text(XmlElement parent, string name, string value) => Child(parent, name).InnerText = value;
        private static void Number(XmlElement parent, string name, object value) => Child(parent, name).SetAttribute("value", Convert.ToString(value, CultureInfo.InvariantCulture));
        private static void Attr(XmlElement node, string name, double value) => node.SetAttribute(name, value.ToString("R", CultureInfo.InvariantCulture));
    }
}
