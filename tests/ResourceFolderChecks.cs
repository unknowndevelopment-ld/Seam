using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodeWalker.GameFiles;
using Seam;

internal static class ResourceFolderChecks
{
    public static int Run(string directory)
    {
        int passed = 0;
        Action<bool, string> check = (ok, text) => { if (!ok) throw new InvalidOperationException(text); passed++; Console.WriteLine("  OK " + text); };
        Directory.CreateDirectory(directory);
        var generated = MergeWorkspace.CreateDemo(Path.Combine(directory, "sources"));
        var sources = new[] { Path.Combine(directory, "MapA"), Path.Combine(directory, "MapB") };
        for (int i = 0; i < 2; i++)
        {
            Directory.Move(generated[i], sources[i]);
            string originalCollision = Directory.GetFiles(sources[i], "*.ybn", SearchOption.AllDirectories).Single();
            File.Copy(originalCollision, Path.Combine(sources[i], "stream", "shared_collision.ybn"));
            File.SetAttributes(Path.Combine(sources[i], "stream", "shared_collision.ybn"), FileAttributes.ReadOnly);
            File.WriteAllBytes(Path.Combine(sources[i], ".fxap"), new byte[] { 0x46, 0x58, 0x41, 0x50, (byte)i, 0xff });
            File.SetAttributes(Path.Combine(sources[i], ".fxap"), FileAttributes.Hidden | FileAttributes.ReadOnly);
            Directory.CreateDirectory(Path.Combine(sources[i], "server"));
            Directory.CreateDirectory(Path.Combine(sources[i], "web"));
            File.WriteAllBytes(Path.Combine(sources[i], "server", "protected.lua"), new byte[] { 0xff, 0, (byte)i, 91 });
            File.WriteAllText(Path.Combine(sources[i], "web", "index.html"), "original web asset " + i);
            File.WriteAllBytes(Path.Combine(sources[i], "stream", "protected.ydr"), new byte[] { 0x43, 0x46, 0x58, (byte)i });
            File.AppendAllText(Path.Combine(sources[i], "fxmanifest.lua"), "server_script 'server/protected.lua'\nui_page 'web/index.html'\n-- original resource " + i + "\n");
        }
        var hashes = sources.SelectMany(p => Directory.GetFiles(p, "*", SearchOption.AllDirectories)).ToDictionary(p => p, Digest);
        var workspace = new MergeWorkspace(); workspace.Import(sources);
        var plan = workspace.AutoMergeResources();
        check(plan.PreserveResourceFolders && plan.MergedCollisions == 1, "separate-resource plan merges the shared collision tile");
        check(plan.Outputs.Count(o => o.Name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)) == 2, "separate resources retain both original YMAP names");
        string exports = Path.Combine(directory, "exports"); Directory.CreateDirectory(exports);
        var result = workspace.ExportSeparateResources(exports);
        check(result.ResourcePaths.Count == 2 && result.Path == exports, "two input resources produce two sibling output resources");
        check(result.ResourcePaths.Select(Path.GetFileName).SequenceEqual(new[] { "MapA_merged", "MapB_merged" }), "output folders append _merged to each original name");
        for (int i = 0; i < 2; i++)
        {
            var destination = result.ResourcePaths[i];
            foreach (var source in Directory.GetFiles(sources[i], "*", SearchOption.AllDirectories))
            {
                var relative = source.Substring(sources[i].Length + 1);
                var target = Path.Combine(destination, relative);
                check(File.Exists(target), "resource layout retained: " + Path.GetFileName(destination) + "/" + relative);
                if (!source.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase) && !source.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase))
                    check(Digest(target) == hashes[source], "non-map resource file is byte-identical: " + Path.GetFileName(destination) + "/" + relative);
            }
            check((File.GetAttributes(Path.Combine(destination, ".fxap")) & FileAttributes.Hidden) != 0, "hidden .fxap is copied with its resource");
            var merged = new YbnFile(); merged.Load(File.ReadAllBytes(Path.Combine(destination, "stream", "shared_collision.ybn")));
            check(Leaves(merged.Bounds).Count() == 4, "each resource receives the complete shared collision merge");
        }
        check(Digest(Path.Combine(result.ResourcePaths[0], ".fxap")) != Digest(Path.Combine(result.ResourcePaths[1], ".fxap")), "distinct escrow metadata is never combined or substituted between resources");
        check(Digest(Path.Combine(result.ResourcePaths[0], "stream", "shared_collision.ybn")) == Digest(Path.Combine(result.ResourcePaths[1], "stream", "shared_collision.ybn")), "shared collision filename has identical merged bytes in both resources");
        var originalOutputs = result.ResourcePaths.SelectMany(p => Directory.GetFiles(p, "*", SearchOption.AllDirectories)).ToDictionary(p => p, Digest);
        var again = workspace.ExportSeparateResources(exports);
        check(again.ResourcePaths.Select(Path.GetFileName).SequenceEqual(new[] { "MapA_2_merged", "MapB_2_merged" }), "repeated export keeps _merged at the end without overwriting earlier output");
        check(originalOutputs.All(p => Digest(p.Key) == p.Value), "earlier exported resources stay unchanged");
        check(hashes.All(p => Digest(p.Key) == p.Value), "all original resource files stay unchanged");
        byte[] metadata = File.ReadAllBytes(Path.Combine(sources[0], ".fxap"));
        try
        {
            File.SetAttributes(Path.Combine(sources[0], ".fxap"), FileAttributes.Normal);
            File.WriteAllBytes(Path.Combine(sources[0], ".fxap"), metadata.Concat(new byte[] { 1 }).ToArray());
            bool stopped = false;
            try { workspace.ExportSeparateResources(exports); } catch (IOException) { stopped = true; }
            check(stopped && Directory.GetDirectories(exports).Length == 4, "changed escrow metadata aborts export before any new resource commits");
        }
        finally { File.SetAttributes(Path.Combine(sources[0], ".fxap"), FileAttributes.Normal); File.WriteAllBytes(Path.Combine(sources[0], ".fxap"), metadata); File.SetAttributes(Path.Combine(sources[0], ".fxap"), FileAttributes.Hidden | FileAttributes.ReadOnly); }
        var loose = new MergeWorkspace(); loose.Import(new[] { Path.Combine(sources[0], "stream", "shared_collision.ybn") });
        string looseOutput = Path.Combine(directory, "loose-output"); Directory.CreateDirectory(looseOutput);
        var looseResult = loose.ExportSeparateResources(looseOutput);
        check(Path.GetFileName(looseResult.ResourcePaths.Single()) == "MapA_merged" && File.Exists(Path.Combine(looseResult.ResourcePaths[0], ".fxap")), "selecting a stream file finds its owning resource and retains the root .fxap");
        return passed;
    }
    private static IEnumerable<Bounds> Leaves(Bounds bound)
    { if (bound is BoundComposite composite) { foreach (var child in composite.Children.data_items) foreach (var item in Leaves(child)) yield return item; } else yield return bound; }
    private static string Digest(string path) { using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return Convert.ToBase64String(sha.ComputeHash(file)); }
}
