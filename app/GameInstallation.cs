using System;
using System.IO;
using System.Web.Script.Serialization;

namespace Seam
{
    public static class GameInstallation
    {
        public sealed class Settings { public bool HasChosen { get; set; } public string Folder { get; set; } }
        private static Settings settings;
        public static event EventHandler Changed;
        private static string SettingsPath => Environment.GetEnvironmentVariable("SEAM_SETTINGS_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seam", "settings.json");
        private static Settings Current
        {
            get
            {
                if (settings != null) return settings;
                try { settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(SettingsPath)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is InvalidOperationException) { }
                return settings ?? (settings = new Settings());
            }
        }
        public static bool IsValidFolder(string folder)
        {
            try { return !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "GTA5.exe")) && File.Exists(Path.Combine(folder, "x64v.rpf")); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }
        public static string SelectedFolder => IsValidFolder(Current.Folder) ? Current.Folder : null;
        public static bool Available => SelectedFolder != null;
        public static bool NeedsSetup => !Current.HasChosen || (!string.IsNullOrEmpty(Current.Folder) && !Available);
        public static void Select(string folder)
        {
            if (!string.IsNullOrWhiteSpace(folder) && !IsValidFolder(folder)) throw new ArgumentException("Choose a GTA V Legacy installation containing GTA5.exe and x64v.rpf.");
            var next = new Settings { HasChosen = true, Folder = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder) };
            string file = Path.GetFullPath(SettingsPath), temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            try
            {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(next));
                if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            settings = next;
            Changed?.Invoke(null, EventArgs.Empty);
        }
        public static void Reload() { settings = null; }
    }
}
