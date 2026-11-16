using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Seam;

internal static class ResourceCollectionChecks
{
    public static int Run(string directory)
    {
        int passed = 0;
        Action<bool, string> check = (ok, text) => { if (!ok) throw new InvalidOperationException(text); passed++; Console.WriteLine("  OK " + text); };
        Directory.CreateDirectory(directory);

        var generated = MergeWorkspace.CreateDemo(Path.Combine(directory, "sources"));
        var collection = Path.Combine(directory, "[mlos]");
        var first = Path.Combine(collection, "Interior One");
        var category = Path.Combine(collection, "[interiors]");
        var second = Path.Combine(category, "Interior Two");
        Directory.CreateDirectory(category);
        Directory.Move(generated[0], first);
        Directory.Move(generated[1], second);
        // MLO packs sometimes omit a manifest. A stream folder still marks a resource.
        File.Delete(Path.Combine(second, "fxmanifest.lua"));
        foreach (var resource in new[] { first, second })
        {
            string map = Directory.GetFiles(resource, "*.ymap", SearchOption.AllDirectories).Single();
            File.Copy(map, Path.Combine(Path.GetDirectoryName(map), "shared_interior.ymap"));
        }

        var workspace = new MergeWorkspace();
        var imported = workspace.Import(new[] { collection });
        check(imported.IsCollection && imported.AutoMergeRecommended && imported.ResourceCount == 2,
            "dropped collection reports every nested resource and recommends automatic merging");
        check(workspace.Packs.Count == 2 && workspace.Packs.All(p => !p.Root.Equals(collection, StringComparison.OrdinalIgnoreCase)),
            "nested and manifest-less resources become separate sources instead of one pack");
        check(workspace.Packs.Select(p => p.Root).OrderBy(p => p).SequenceEqual(new[] { first, second }.OrderBy(p => p)),
            "each source keeps its original resource root");

        var plan = workspace.AutoMergeResources();
        check(plan.PreserveResourceFolders && plan.MergedMaps == 1,
            "collection merge combines the shared YMAP name only");
        check(plan.Outputs.Count(o => o.Name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)) == 3,
            "the two unique maps remain separate outputs");
        check(plan.Outputs.Single(o => o.Name.Equals("shared_interior.ymap", StringComparison.OrdinalIgnoreCase)).Sources.Count == 2,
            "shared map output retains both contributing sources");

        var exports = Path.Combine(directory, "exports");
        Directory.CreateDirectory(exports);
        var result = workspace.ExportSeparateResources(exports);
        check(result.ResourcePaths.Count == 2, "collection exports one _merged folder per original resource");
        var sharedA = Path.Combine(result.ResourcePaths[0], "stream", "shared_interior.ymap");
        var sharedB = Path.Combine(result.ResourcePaths[1], "stream", "shared_interior.ymap");
        check(File.Exists(sharedA) && File.Exists(sharedB) && Digest(sharedA) == Digest(sharedB),
            "both exports receive the same merged shared map bytes");
        var unique = Directory.GetFiles(result.ResourcePaths[0], "*.ymap", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).Equals("shared_interior.ymap", StringComparison.OrdinalIgnoreCase)).ToArray();
        check(unique.Length == 1 && Digest(unique[0]) == Digest(Directory.GetFiles(first, "*.ymap", SearchOption.AllDirectories)
                .Single(p => !Path.GetFileName(p).Equals("shared_interior.ymap", StringComparison.OrdinalIgnoreCase))),
            "unique maps are copied unchanged into their own resource");
        return passed;
    }

    private static string Digest(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream));
    }
}
