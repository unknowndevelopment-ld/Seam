using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Seam
{
    public sealed partial class MergeWorkspace
    {
        private sealed class ResourceFolder
        {
            public string Root;
            public string Name;
            public List<MapAsset> Assets;
            public string[] Files;
        }

        private List<ResourceFolder> ResourceFolders()
        {
            var packs = Packs.Where(p => p.Enabled).Select(p => new { Pack = p, Root = FindResourceRoot(p.Root) });
            return packs.GroupBy(p => p.Root, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var assets = group.SelectMany(p => p.Pack.Assets).ToList();
                string root = group.Key;
                return new ResourceFolder { Root = root, Name = new DirectoryInfo(root).Name, Assets = assets,
                    Files = IsResourceFolder(root) ? AllResourceFiles(root).ToArray() : assets.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
            }).ToList();
        }

        private static string FindResourceRoot(string start)
        {
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
                if (IsResourceFolder(directory.FullName)) return directory.FullName;
            return System.IO.Path.GetFullPath(start);
        }

        private static IEnumerable<string> AllResourceFiles(string root)
        {
            // Enumerate hidden files too (.fxap), without following junctions outside the resource.
            foreach (var file in Directory.EnumerateFiles(root))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (var directory in Directory.EnumerateDirectories(root))
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 && !System.IO.Path.GetFileName(directory).Equals(".git", StringComparison.OrdinalIgnoreCase))
                    foreach (var file in AllResourceFiles(directory)) yield return file;
        }

        private void SnapshotResourceFolders(AutomaticMergePlan plan)
        {
            var folders = ResourceFolders();
            plan.ResourceCount = folders.Count;
            foreach (var folder in folders)
            {
                foreach (var file in folder.Files)
                {
                    string digest = HashFile(file);
                    if (plan.SourceFiles.TryGetValue(file, out var expected) && digest != expected) throw new IOException("Source changed while merging: " + file);
                    plan.SourceFiles[file] = digest;
                }
                plan.Operations.Add(new MergeOperation { Kind = "Resource preserved", Title = folder.Name + "_merged",
                    Detail = "Separate copy of the original folder. Hidden .fxap files, original manifests, encrypted assets, scripts and subfolders are retained byte-for-byte. Only readable YMAP/YBN output is replaced.", Count = folder.Files.Length });
            }
        }

        public ExportResult ExportSeparateResources(string parentDirectory, Action<string> progress = null)
        {
            if (!Directory.Exists(parentDirectory)) throw new DirectoryNotFoundException("Choose an existing output folder.");
            var parent = System.IO.Path.GetFullPath(parentDirectory).TrimEnd('\\', '/');
            var folders = ResourceFolders();
            if (folders.Count == 0) throw new InvalidOperationException("Add a resource before exporting.");
            foreach (var folder in folders)
                if (parent.Equals(folder.Root, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(folder.Root.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Choose an output folder outside the original resources.");
            var plan = LastAutomaticMerge;
            if (plan == null || !plan.PreserveResourceFolders || plan.Fingerprint != AutomaticFingerprint()) plan = AutoMergeResources(progress);
            VerifyFolderSources(plan, folders);

            var names = folders.Select(f => Regex.Replace(f.Name, @"[^A-Za-z0-9_-]", "_")).ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
                throw new InvalidOperationException("Two source folders have the same resource name. Give those input folders distinct names first.");
            int version = 1;
            string[] outputNames;
            do { outputNames = names.Select(n => n + (version == 1 ? "" : "_" + version) + "_merged").ToArray(); version++; }
            while (outputNames.Any(n => Directory.Exists(System.IO.Path.Combine(parent, n)) || File.Exists(System.IO.Path.Combine(parent, n))));

            var stage = System.IO.Path.Combine(parent, ".seam-" + Guid.NewGuid().ToString("N"));
            var committed = new List<Tuple<string, string>>();
            var result = new ExportResult { Path = parent, RemovedEntities = plan.DuplicateEntities, RemovedBounds = plan.DuplicateBounds };
            try
            {
                Directory.CreateDirectory(stage);
                for (int index = 0; index < folders.Count; index++)
                {
                    var folder = folders[index];
                    var destination = System.IO.Path.Combine(stage, outputNames[index]);
                    Directory.CreateDirectory(destination);
                    foreach (var file in folder.Files)
                    {
                        string relative = RelativeResourcePath(folder.Root, file);
                        string target = System.IO.Path.Combine(destination, relative);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                        progress?.Invoke("Copying " + outputNames[index] + " · " + relative);
                        File.Copy(file, target, false);
                        if (HashFile(target) != plan.SourceFiles[file]) throw new IOException("Source changed while copying " + file);
                        result.Files++;
                    }
                    var applied = new List<object>();
                    foreach (var output in plan.Outputs.Where(o => o.Data != null))
                    foreach (var source in output.Sources.Where(a => folder.Assets.Contains(a) && a.Error == null && (a.Extension == ".ymap" || a.Extension == ".ybn")))
                    {
                        if (!source.Name.Equals(output.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A merged map changed its streaming name: " + source.Name);
                        string relative = RelativeResourcePath(folder.Root, source.Path);
                        if (IsXml(source.Path)) relative = relative.Substring(0, relative.Length - 4);
                        string target = System.IO.Path.Combine(destination, relative);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                        var attributes = File.Exists(target) ? File.GetAttributes(target) : FileAttributes.Normal;
                        if (File.Exists(target)) File.SetAttributes(target, attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
                        File.WriteAllBytes(target, output.Data);
                        if (File.Exists(target)) File.SetAttributes(target, attributes);
                        applied.Add(new { file = relative, action = output.Action });
                    }
                    // Preserve an existing report too; add a uniquely named handoff report.
                    string report = System.IO.Path.Combine(destination, "seam-merge-report.json");
                    for (int suffix = 2; File.Exists(report); suffix++) report = System.IO.Path.Combine(destination, "seam-merge-report-" + suffix + ".json");
                    File.WriteAllText(report, Json().Serialize(new { application = "Seam " + typeof(MergeWorkspace).Assembly.GetName().Version.ToString(3), mode = "separate-resources-ymap-ybn-only", source = folder.Root,
                        resource = outputNames[index], applied, operations = plan.Operations.ToArray(),
                        note = "Use all exported resource copies together in place of the originals. Original manifests and escrow metadata are unchanged. Preserving files does not validate entitlement or compatibility with a renamed resource." }), new UTF8Encoding(false));
                }
                VerifyFolderSources(plan, folders);
                for (int i = 0; i < outputNames.Length; i++)
                {
                    string from = System.IO.Path.Combine(stage, outputNames[i]); string to = System.IO.Path.Combine(parent, outputNames[i]);
                    CommitExport(from, to); committed.Add(Tuple.Create(from, to)); result.ResourcePaths.Add(to);
                }
                progress?.Invoke("Export complete · " + result.ResourcePaths.Count + " separate resources");
                return result;
            }
            catch
            {
                // A batch has multiple directory commits: put only our own committed
                // folders back into private staging if a later commit fails.
                foreach (var own in committed.AsEnumerable().Reverse()) CommitExport(own.Item2, own.Item1);
                throw;
            }
            finally
            {
                if (Directory.Exists(stage) && System.IO.Path.GetDirectoryName(stage).Equals(parent, StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileName(stage).StartsWith(".seam-", StringComparison.Ordinal))
                {
                    foreach (var file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)) File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    Directory.Delete(stage, true);
                }
            }
        }

        private static string RelativeResourcePath(string root, string path)
        {
            string prefix = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            string full = System.IO.Path.GetFullPath(path);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("File is outside its resource: " + path);
            return full.Substring(prefix.Length);
        }
        private static void VerifyFolderSources(AutomaticMergePlan plan, List<ResourceFolder> folders)
        {
            foreach (var folder in folders)
            {
                if (IsResourceFolder(folder.Root) && !new HashSet<string>(AllResourceFiles(folder.Root), StringComparer.OrdinalIgnoreCase).SetEquals(folder.Files))
                    throw new IOException("Files were added or removed from " + folder.Root + ". Run Auto merge again.");
                foreach (var file in folder.Files)
                    if (!plan.SourceFiles.TryGetValue(file, out var digest) || !File.Exists(file) || HashFile(file) != digest)
                        throw new IOException("Source changed after merging: " + file + ". Run Auto merge again.");
            }
        }
    }
}
