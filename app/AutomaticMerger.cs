using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CodeWalker.GameFiles;

namespace Seam
{
    public sealed partial class MergeWorkspace
    {
        public AutomaticMergePlan AutoMerge(Action<string> progress = null)
            => BuildAutomaticPlan(false, progress);

        public AutomaticMergePlan AutoMergeResources(Action<string> progress = null)
            => BuildAutomaticPlan(true, progress);

        private AutomaticMergePlan BuildAutomaticPlan(bool preserveResources, Action<string> progress)
        {
            var assets = Packs.Where(p => p.Enabled).SelectMany(p => p.Assets).ToList();
            if (assets.Count == 0) throw new InvalidOperationException("Add at least one supported map asset before merging.");
            if (assets.Any(a => a.Excluded || a.Entities.Any(e => e.Excluded) || a.Collisions.Any(c => c.Excluded)))
            {
                RestoreAll();
                Scene.Clear(); if (BuildPreview) Scene.AddRange(GeometryReader.Build(this, progress));
            }
            var plan = new AutomaticMergePlan { PreserveResourceFolders = preserveResources, InputFiles = assets.Count, InputMaps = assets.Count(a => a.Extension == ".ymap"), InputCollisions = assets.Count(a => a.Extension == ".ybn") };
            foreach (var asset in assets)
            {
                progress?.Invoke("Checking source · " + asset.Name);
                if (!File.Exists(asset.Path) || HashFile(asset.Path) != asset.Digest)
                    throw new IOException("Source changed since import: " + asset.Path + ". Import the current files into a new project before merging.");
                plan.SourceFiles[asset.Path] = asset.Digest;
            }
            // A protected/unsupported version of a streamed name must never be fed
            // to a decoder or replaced by a merge of only its readable siblings.
            var mergeable = assets.Where(a => a.Extension == ".ymap" || a.Extension == ".ybn")
                .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.All(a => a.Error == null && (a.Extension == ".ymap" ? a.Ymap != null : a.Ybn != null)))
                .SelectMany(g => g).ToList();
            progress?.Invoke("Combining readable YMAP placements…");
            var maps = mergeable.Where(a => a.Extension == ".ymap").ToList();
            var lightOutputs = YmapLightMerger.Merge(maps, plan, progress);
            var lightSources = new HashSet<MapAsset>(lightOutputs.SelectMany(o => o.Sources));
            plan.Outputs.AddRange(lightOutputs);
            plan.Outputs.AddRange(YmapMerger.Merge(maps.Where(a => !lightSources.Contains(a)).ToList(), plan, progress, !preserveResources));
            progress?.Invoke("Merging collision patches and bounds…");
            plan.Outputs.AddRange(CollisionMerger.Merge(mergeable.Where(a => a.Extension == ".ybn").ToList(), plan, progress));
            progress?.Invoke("Preserving other assets unchanged…");
            PreserveOtherAssets(assets.Except(mergeable).ToList(), plan, preserveResources);
            progress?.Invoke("Checking placements, lights and map bounds…");
            MapRepairer.Apply(plan, assets, progress);
            if (preserveResources) SnapshotResourceFolders(plan);
            else ResourceManifestMerger.Include(Packs.Where(p => p.Enabled).ToList(), plan, progress);
            plan.Operations.Insert(0, new MergeOperation { Kind = "Automatic merge", Title = preserveResources ? "Separate resource copies" : "Combined map resource", Detail = plan.Summary + (preserveResources ? ". Each source exports separately with _merged; manifests, .fxap metadata and other files stay unchanged." : ". Named interiors and streaming replacements retain the names required by GTA."), Count = plan.Outputs.Count });
            var duplicate = plan.Outputs.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null) throw new InvalidDataException("Merge produced a duplicate streamed filename: " + duplicate.Key);
            foreach (var output in plan.Outputs)
                if ((output.Data == null && output.CopySourcePath == null) || System.IO.Path.GetFileName(output.Name) != output.Name)
                    throw new InvalidDataException("Invalid generated output: " + output.Name);
            plan.Fingerprint = AutomaticFingerprint();
            LastAutomaticMerge = plan;
            progress?.Invoke("Automatic merge ready · " + plan.Summary);
            return plan;
        }

        private static void PreserveOtherAssets(List<MapAsset> assets, AutomaticMergePlan plan, bool deferCopy = false)
        {
            // Non-map XML remains XML; never write XML bytes under a binary resource extension.
            string OutputName(MapAsset a) => IsXml(a.Path) ? System.IO.Path.GetFileName(a.Path) : a.Name;
            foreach (var group in assets.GroupBy(OutputName, StringComparer.OrdinalIgnoreCase))
            {
                var sources = group.ToList(); var first = sources[0];
                bool skipped = sources.Any(a => a.Error != null);
                int identical = sources.Count - sources.Select(a => a.Digest).Distinct(StringComparer.Ordinal).Count();
                plan.DuplicateFiles += identical;
                plan.Outputs.Add(new AutomaticMergeOutput { Name = group.Key, Data = deferCopy ? null : File.ReadAllBytes(first.Path), CopySourcePath = deferCopy ? first.Path : null, Sources = sources,
                    Action = skipped ? "Skipped decoding; original bytes preserved" : "Copied unchanged" });
                string detail = "Only readable YMAP and YBN contents are merged. This file is copied byte-for-byte without content merging.";
                if (skipped) detail += " Encrypted, invalid or unsupported input was skipped by the merge/preview decoder: " + string.Join("; ", sources.Where(a => a.Error != null).Select(a => a.Pack.Name + ": " + a.Error));
                if (sources.Count > 1) detail += deferCopy
                    ? " Each separate resource retains its own original version; non-map files are not substituted between folders."
                    : " Multiple files share this streamed name; the highest-priority complete original is retained (" + first.Pack.Name + ").";
                DictionaryMerger.Log(plan, skipped ? "Skipped encrypted / unsupported" : "Asset preserved", group.Key, detail, sources.Count);
            }
        }

        private string AutomaticFingerprint() => StableId(string.Join("|", Packs.Where(p => p.Enabled).SelectMany(p => p.Assets).Select(a => a.Id + ":" + a.Digest)));

        public ExportResult ExportAutomatic(string parentDirectory, string resourceName, Action<string> progress = null)
        {
            if (!Regex.IsMatch(resourceName ?? "", "^[a-zA-Z][a-zA-Z0-9_-]{0,63}$")) throw new ArgumentException("Resource names must start with a letter and use letters, digits, _ or - (up to 64 characters).");
            if (!Directory.Exists(parentDirectory)) throw new DirectoryNotFoundException("Choose an existing output folder.");
            var parent = System.IO.Path.GetFullPath(parentDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            foreach (var pack in Packs)
            {
                var root = System.IO.Path.GetFullPath(pack.Root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                if (parent.Equals(root, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Choose an output folder outside the source map folders.");
            }
            var plan = LastAutomaticMerge;
            if (plan == null || plan.PreserveResourceFolders || plan.Fingerprint != AutomaticFingerprint()) plan = AutoMerge(progress);
            foreach (var source in plan.SourceFiles)
                if (!File.Exists(source.Key) || HashFile(source.Key) != source.Value) throw new IOException("Source changed after merging: " + source.Key + ". Import it again.");
            var final = System.IO.Path.Combine(parent, resourceName);
            for (int suffix = 2; Directory.Exists(final) || File.Exists(final); suffix++) final = System.IO.Path.Combine(parent, resourceName + "_" + suffix);
            var stage = System.IO.Path.Combine(parent, ".seam-" + Guid.NewGuid().ToString("N"));
            var result = new ExportResult { Path = final, RemovedEntities = plan.DuplicateEntities, RemovedBounds = plan.DuplicateBounds };
            try
            {
                Directory.CreateDirectory(System.IO.Path.Combine(stage, "stream"));
                foreach (var output in plan.Outputs.Concat(plan.AuxiliaryFiles))
                {
                    var relative = output.RelativePath ?? "stream/" + output.Name;
                    var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(stage, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(stage + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Output path escapes its resource: " + relative);
                    if (File.Exists(target)) throw new InvalidDataException("Duplicate output path: " + relative);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    progress?.Invoke("Writing merged resource · " + relative);
                    File.WriteAllBytes(target, output.Data); result.Files++;
                }
                var manifest = new StringBuilder("fx_version 'cerulean'\ngame 'gta5'\n\nthis_is_a_map 'yes'\n\n");
                var declarations = new HashSet<string>(StringComparer.Ordinal);
                // Low priority first, so a repeated archetype name follows the chosen pack priority.
                foreach (var output in plan.Outputs.Where(o => o.Name.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)).Reverse())
                {
                    string line = "data_file 'DLC_ITYP_REQUEST' 'stream/" + LuaEscape(output.Name) + "'";
                    if (declarations.Add(line)) manifest.AppendLine(line);
                }
                foreach (var directive in plan.ManifestDirectives) if (declarations.Add(directive)) manifest.AppendLine(directive);
                File.WriteAllText(System.IO.Path.Combine(stage, "fxmanifest.lua"), manifest.ToString(), new UTF8Encoding(false));
                File.WriteAllText(System.IO.Path.Combine(stage, "seam-merge-report.json"), Json().Serialize(new
                {
                    application = "Seam 1.1.1", exportedUtc = DateTime.UtcNow.ToString("o"), mode = "automatic-ymap-ybn-only",
                    summary = plan.Summary, plan.InputFiles, plan.InputMaps, plan.InputCollisions, plan.MergedMaps, plan.MergedCollisions,
                    plan.DuplicateEntities, plan.DuplicateBounds, plan.DuplicateFiles, plan.RemovedTriangles, plan.RemovedPolygons,
                    priority = Packs.Where(p => p.Enabled).Select(p => p.Name).ToArray(),
                    operations = plan.Operations.ToArray(),
                    outputs = plan.Outputs.Select(o => new { o.Name, o.Action, sources = o.Sources.Select(s => new { pack = s.Pack.Name, file = s.Name }).ToArray() }).ToArray(),
                    sourceHashes = plan.SourceFiles.ToArray()
                }), new UTF8Encoding(false));
                File.WriteAllText(System.IO.Path.Combine(stage, "README.txt"),
                    "Automatically merged by Seam.\r\n\r\nUse this resource in place of the original map resources; disable their original copies. Check seam-merge-report.json for every automatic operation.\r\n\r\nNamed MLO maps, LOD chains and collision tiles keep their streaming names. Only readable YMAP placements and YBN collision resources are content-merged. All other assets, including encrypted or unsupported files, are copied unchanged; protected files are not decrypted. Shared base-game collision patches can combine removed surfaces without a pristine base-game baseline; inspect those operations and test in GTA V. Distinct overlapping surfaces are retained unless verified duplicate geometry is found. This does not perform general mesh boolean subtraction.\r\n\r\nLocal scripts and declared manifest data are included. External references such as @OtherResource/path.lua still require the original external resource.\r\n", new UTF8Encoding(false));
                foreach (var source in plan.SourceFiles)
                    if (!File.Exists(source.Key) || HashFile(source.Key) != source.Value) throw new IOException("Source changed during export: " + source.Key);
                CommitExport(stage, final);
                progress?.Invoke("Export complete · " + result.Files + " merged files");
                return result;
            }
            finally
            {
                if (Directory.Exists(stage) && System.IO.Path.GetDirectoryName(stage).Equals(parent, StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileName(stage).StartsWith(".seam-", StringComparison.Ordinal)) Directory.Delete(stage, true);
            }
        }
    }
}
