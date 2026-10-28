using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Seam
{
    public static class ResourceManifestMerger
    {
        private static readonly Regex Declaration = new Regex(@"(?m)^[\t ]*(?<key>client_scripts?|server_scripts?|shared_scripts?|files?|dependencies|dependency|data_file|lua54|use_experimental_fxv2_oal|escrow_ignore)\b[\t ]*(?<body>\{[^}]*\}|[^\r\n]*)", RegexOptions.Compiled);
        private static readonly Regex Quoted = new Regex(@"'(?<value>(?:\\.|[^'\\])*)'|""(?<value>(?:\\.|[^""\\])*)""", RegexOptions.Compiled);

        public static void Include(IReadOnlyList<MapPack> packs, AutomaticMergePlan plan, Action<string> progress = null)
        {
            var copied = new Dictionary<string, AutomaticMergeOutput>(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in packs)
            {
                string manifest = Path.Combine(pack.Root, "fxmanifest.lua");
                if (!File.Exists(manifest)) manifest = Path.Combine(pack.Root, "__resource.lua");
                if (!File.Exists(manifest)) continue;
                progress?.Invoke("Combining resource manifest · " + pack.Name);
                plan.SourceFiles[manifest] = MergeWorkspace.HashFile(manifest);
                string code = File.ReadAllText(manifest);
                code = Regex.Replace(code, @"--\[\[.*?\]\]", "", RegexOptions.Singleline);
                code = Regex.Replace(code, @"(?m)^\s*--[^\r\n]*", "");
                int declarations = 0;
                foreach (Match match in Declaration.Matches(code))
                {
                    string key = match.Groups["key"].Value;
                    var args = Quoted.Matches(match.Groups["body"].Value).Cast<Match>().Select(m => Unescape(m.Groups["value"].Value)).ToArray();
                    if (args.Length == 0) continue;
                    declarations++;
                    if (key == "dependency" || key == "dependencies")
                    {
                        foreach (var name in args) plan.ManifestDirectives.Add("dependency " + Quote(name));
                    }
                    else if (key == "lua54" || key == "use_experimental_fxv2_oal") plan.ManifestDirectives.Add(key + " " + Quote(args[0]));
                    else if (key == "data_file")
                    {
                        if (args.Length != 2) throw new InvalidDataException(pack.Name + ": data_file requires a type and path.");
                        foreach (var path in Expand(pack, args[1], plan))
                        {
                            string output = IncludeFile(pack, path, plan, copied);
                            plan.ManifestDirectives.Add("data_file " + Quote(args[0]) + " " + Quote(output));
                            plan.ManifestDirectives.Add("file " + Quote(output));
                        }
                    }
                    else
                    {
                        string directive = key == "files" ? "file" : key.EndsWith("s", StringComparison.Ordinal) ? key.Substring(0, key.Length - 1) : key;
                        foreach (var arg in args)
                        foreach (var path in Expand(pack, arg, plan))
                        {
                            var output = IncludeFile(pack, path, plan, copied);
                            if (key == "escrow_ignore") plan.ManifestDirectives.Add("escrow_ignore { " + Quote(output) + " }");
                            else plan.ManifestDirectives.Add(directive + " " + Quote(output));
                        }
                    }
                }
                DictionaryMerger.Log(plan, "Resource manifest", pack.Name, $"Included {declarations} supported manifest declarations. Local scripts and data retain their resource paths; external @resource references remain external.", declarations);
                if (Regex.IsMatch(code, @"(?m)^\s*(?:ui_page|provide|loadscreen|export|server_export|before_level_meta|after_level_meta|replace_level_meta)\b"))
                    throw new InvalidDataException(pack.Name + ": this resource includes a UI/export/level declaration that cannot be combined as a map automatically. Separate that behavior from the map resource first.");
            }
            plan.AuxiliaryFiles.AddRange(copied.Values);
        }

        private static IEnumerable<string> Expand(MapPack pack, string path, AutomaticMergePlan plan)
        {
            if (path.StartsWith("@", StringComparison.Ordinal)) { yield return path; yield break; }
            if (path.IndexOfAny(new[] { '*', '?' }) < 0) { yield return path; yield break; }
            string normalized = path.Replace('\\', '/');
            string pattern = "^" + Regex.Escape(normalized).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]").Replace("\u0001/", "(?:.*/)?").Replace("\u0001", ".*") + "$";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            bool found = false;
            foreach (var file in Enumerate(pack.Root))
            {
                string relative = file.Substring(pack.Root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
                if (regex.IsMatch(relative)) { found = true; yield return relative; }
            }
            if (!found) DictionaryMerger.Log(plan, "Manifest notice", pack.Name, "The optional manifest glob matched no local files: " + path);
        }

        private static string IncludeFile(MapPack pack, string relative, AutomaticMergePlan plan, Dictionary<string, AutomaticMergeOutput> copied)
        {
            if (relative.StartsWith("@", StringComparison.Ordinal)) return relative;
            var root = Path.GetFullPath(pack.Root).TrimEnd('\\', '/');
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Manifest path escapes its resource: " + relative);
            if (!File.Exists(full)) throw new FileNotFoundException(pack.Name + ": a declared resource file is missing.", full);
            var streamed = plan.Outputs.FirstOrDefault(o => o.Sources.Any(s => s.Path.Equals(full, StringComparison.OrdinalIgnoreCase)));
            if (streamed != null) return "stream/" + streamed.Name;
            string outputPath = relative.Replace('\\', '/').TrimStart('.','/');
            var bytes = File.ReadAllBytes(full);
            string digest = MergeWorkspace.HashFile(full);
            plan.SourceFiles[full] = digest;
            if (copied.TryGetValue(outputPath, out var other))
            {
                if (other.Data.SequenceEqual(bytes)) return outputPath;
                outputPath = "source/" + MergeWorkspace.StableId(pack.Root).Substring(0, 8) + "/" + outputPath;
                DictionaryMerger.Log(plan, "Script path namespaced", relative, pack.Name + " has a different file at an existing resource path. Its declaration uses " + outputPath + ". Scripts that construct relative filenames may need adaptation.");
                if (copied.TryGetValue(outputPath, out other))
                {
                    if (!other.Data.SequenceEqual(bytes)) throw new InvalidDataException("Conflicting resource data at " + outputPath);
                    return outputPath;
                }
            }
            if (outputPath.Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase) || outputPath.Equals("seam-merge-report.json", StringComparison.OrdinalIgnoreCase) || outputPath.Equals("README.txt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A source declaration collides with generated resource metadata: " + outputPath);
            copied[outputPath] = new AutomaticMergeOutput { Name = Path.GetFileName(full), RelativePath = outputPath, Data = bytes, Action = "Resource script/data copied" };
            return outputPath;
        }
        private static IEnumerable<string> Enumerate(string root)
        {
            foreach (var file in Directory.EnumerateFiles(root)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (var directory in Directory.EnumerateDirectories(root))
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0 && Path.GetFileName(directory) != ".git")
                    foreach (var file in Enumerate(directory)) yield return file;
        }
        private static string Unescape(string value) => value.Replace("\\'", "'").Replace("\\\"", "\"").Replace("\\\\", "\\");
        private static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n") + "'";
    }
}
