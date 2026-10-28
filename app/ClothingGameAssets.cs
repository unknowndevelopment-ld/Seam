using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeWalker.GameFiles;

namespace Seam
{
    /// <summary>Read-only access to a user's installed GTA V Legacy freemode assets.</summary>
    public static class ClothingGameAssets
    {
        static readonly object gate = new object();
        static readonly Dictionary<string, ClothingPedAsset> cache = new Dictionary<string, ClothingPedAsset>(StringComparer.OrdinalIgnoreCase);
        static string keyFolder;

        public static string DetectGameFolder()
        {
            var candidates = new List<string>();
            string ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM", "FiveM.app", "CitizenFX.ini");
            if (File.Exists(ini))
                foreach (string line in File.ReadLines(ini))
                    if (line.StartsWith("IVPath=", StringComparison.OrdinalIgnoreCase)) candidates.Add(line.Substring(7).Trim());
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Grand Theft Auto V"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rockstar Games", "Grand Theft Auto V"));
            try
            {
                using (var registry = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V"))
                    if (registry?.GetValue("InstallFolder") is string path) candidates.Add(path);
            }
            catch (System.Security.SecurityException) { }
            return candidates.FirstOrDefault(p => File.Exists(Path.Combine(p, "GTA5.exe")) && File.Exists(Path.Combine(p, "x64v.rpf"))) ?? "";
        }

        internal static ClothingPedAsset LoadPed(string gender, string folder, Action<string> progress)
        {
            if (string.IsNullOrWhiteSpace(folder)) folder = DetectGameFolder();
            if (string.IsNullOrWhiteSpace(folder) || !File.Exists(Path.Combine(folder, "x64v.rpf")))
                throw new FileNotFoundException("Select your GTA V Legacy game folder to preview the real freemode ped. Clothing files can still be edited and exported.");
            folder = Path.GetFullPath(folder);
            string pedName = gender == "female" ? "mp_f_freemode_01" : "mp_m_freemode_01";
            string key = folder + "|" + pedName;
            lock (gate)
            {
                if (cache.TryGetValue(key, out var cached)) return cached;
                progress?.Invoke("Reading your GTA V installation…");
                if (!string.Equals(keyFolder, folder, StringComparison.OrdinalIgnoreCase))
                {
                    // CodeWalker's public loader locates its decryption material in the user's own executable.
                    // Nothing is written to the game, exported or bundled with Seam.
                    GTA5Keys.LoadFromPath(folder);
                    keyFolder = folder;
                }
                var archive = new RpfFile(Path.Combine(folder, "x64v.rpf"), "x64v.rpf");
                archive.ScanStructure(null, null);
                var stream = archive.Children?.FirstOrDefault(r => r.NameLower == "streamedpeds_mp.rpf");
                if (stream?.AllEntries == null) throw new InvalidDataException("The game archive does not contain streamed freemode peds. GTA V Legacy is required by this codec.");
                var result = new ClothingPedAsset { Name = pedName };
                var skeletonEntry = stream.AllEntries.OfType<RpfFileEntry>().FirstOrDefault(e => e.Name == pedName + ".yft");
                var fragment = skeletonEntry == null ? null : RpfFile.GetFile<YftFile>(skeletonEntry);
                result.Skeleton = fragment?.Fragment?.Drawable?.Skeleton;
                result.Skeleton?.ResetBoneTransforms();
                string prefix = stream.Path + "\\" + pedName + "\\";
                var entries = stream.AllEntries.OfType<RpfFileEntry>().Where(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                // A complete unclothed base is useful for checking both apparel fit and tattoo UVs.
                string[] parts = gender == "female"
                    ? new[] { "head_000", "hair_000", "uppr_015", "accs_014", "lowr_015", "feet_000" }
                    : new[] { "head_000", "hair_000", "uppr_015", "lowr_014", "feet_005" };
                foreach (string requested in parts)
                {
                    string component = requested.Substring(0, 4);
                    var entry = entries.FirstOrDefault(e => e.Name.StartsWith(requested + "_", StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase))
                        ?? entries.FirstOrDefault(e => e.Name.StartsWith(component + "_000_", StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".ydd", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) continue;
                    progress?.Invoke("Loading " + pedName + " · " + component);
                    var ydd = RpfFile.GetFile<YddFile>(entry);
                    string drawable = Path.GetFileNameWithoutExtension(entry.Name);
                    string number = drawable.Substring(5, 3);
                    string texturePrefix = component + "_diff_" + number + "_a_";
                    var texEntry = entries.FirstOrDefault(e => e.Name.StartsWith(texturePrefix, StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase));
                    var textures = texEntry == null ? null : RpfFile.GetFile<YtdFile>(texEntry)?.TextureDict;
                    foreach (var mesh in ydd?.Dict?.Values ?? Enumerable.Empty<Drawable>())
                        result.Parts.Add(new ClothingPedPart { Component = ComponentId(component), Drawable = mesh, Textures = textures });
                }
                if (result.Parts.Count < 3) throw new InvalidDataException("The installed game did not provide a complete freemode body.");
                cache[key] = result;
                return result;
            }
        }

        static int ComponentId(string name)
        {
            switch (name) { case "head": return 0; case "hair": return 2; case "uppr": return 3; case "lowr": return 4; case "feet": return 6; case "accs": return 8; default: return -1; }
        }
    }

    internal sealed class ClothingPedAsset
    {
        public string Name;
        public Skeleton Skeleton;
        public List<ClothingPedPart> Parts = new List<ClothingPedPart>();
    }
    internal sealed class ClothingPedPart
    {
        public int Component;
        public Drawable Drawable;
        public TextureDictionary Textures;
    }
}
