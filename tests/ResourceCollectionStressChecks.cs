using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodeWalker.GameFiles;
using SharpDX;
using Seam;

internal static class ResourceCollectionStressChecks
{
    public static int Run(string directory)
    {
        int passed = 0;
        Action<bool, string> check = (ok, text) => { if (!ok) throw new InvalidOperationException(text); passed++; Console.WriteLine("  OK " + text); };
        Directory.CreateDirectory(directory);

        var demo = MergeWorkspace.CreateDemo(Path.Combine(directory, "demo"));
        string baseResource = demo[0], alternateResource = demo[1];
        string baseMap = Directory.GetFiles(baseResource, "*.ymap", SearchOption.AllDirectories).Single();
        string baseCollision = Directory.GetFiles(baseResource, "*.ybn", SearchOption.AllDirectories).Single();
        string alternateCollision = Directory.GetFiles(alternateResource, "*.ybn", SearchOption.AllDirectories).Single();

        var collection = Path.Combine(directory, "[mlos stress]");
        Directory.CreateDirectory(collection);
        var roots = new List<string>();
        int index = 0;
        for (int category = 0; category < 6; category++)
        {
            string categoryPath = Path.Combine(collection, "[category " + category + "]");
            Directory.CreateDirectory(categoryPath);
            for (int item = 0; item < 6; item++, index++)
            {
                string root = Path.Combine(categoryPath, "Interior " + category + "-" + item);
                CopyDirectory(baseResource, root);
                PrepareMapResource(root, index, baseMap, baseCollision, alternateCollision);
                // Whole collections often contain a mix of manifest and manifest-less packs.
                if (index % 5 == 4) File.Delete(Path.Combine(root, "fxmanifest.lua"));
                roots.Add(root);
            }
        }

        // A resource buried under ten category folders must still be discovered.
        string deep = collection;
        for (int depth = 0; depth < 10; depth++) deep = Path.Combine(deep, "nested " + depth);
        Directory.CreateDirectory(deep);
        string deepRoot = Path.Combine(deep, "Deep Interior");
        CopyDirectory(baseResource, deepRoot);
        PrepareMapResource(deepRoot, index, baseMap, baseCollision, alternateCollision);
        roots.Add(deepRoot);
        index++;

        // A manifest resource can keep its stream files directly in its root.
        string direct = Path.Combine(collection, "Direct resource");
        Directory.CreateDirectory(direct);
        File.WriteAllText(Path.Combine(direct, "fxmanifest.lua"), "fx_version 'cerulean'\ngame 'gta5'\n\nthis_is_a_map 'yes'\n");
        WriteMap(baseMap, Path.Combine(direct, "direct_unique.ymap"), "direct_unique", index);
        File.WriteAllText(Path.Combine(direct, "notes.txt"), "direct resource " + index);
        roots.Add(direct);

        int sharedMaps = roots.Count - 1; // every resource except the direct one
        int uniqueMaps = roots.Count;
        var hashes = Directory.GetFiles(collection, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, Digest, StringComparer.OrdinalIgnoreCase);

        var workspace = new MergeWorkspace { BuildPreview = false };
        var watch = Stopwatch.StartNew();
        var imported = workspace.Import(new[] { collection });
        watch.Stop();
        Console.WriteLine($"  .. imported {imported.ResourceCount} resources / {imported.AssetCount} assets in {watch.ElapsedMilliseconds} ms");
        check(imported.IsCollection && imported.AutoMergeRecommended && !imported.AlreadyImported,
            "large nested collection is detected and marked for automatic merging");
        check(imported.ResourceCount == roots.Count && workspace.Packs.Count == roots.Count,
            "all collection resources import, including manifest-less, deep and direct-root packs");
        check(workspace.Packs.Select(p => p.Root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(roots.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)),
            "every resource keeps its own root instead of collapsing into the collection folder");

        watch.Restart();
        var plan = workspace.AutoMergeResources();
        watch.Stop();
        Console.WriteLine($"  .. auto-merged in {watch.ElapsedMilliseconds} ms");
        check(plan.PreserveResourceFolders && plan.MergedMaps == sharedMaps - 1,
            "only the shared YMAP name is combined across every resource");
        check(plan.MergedCollisions == 1, "shared collision name is combined while unique collisions stay single");
        // Every distinct streamed name is merged into exactly one output, while unique
        // names keep their own file. Compare against the source names so the test does
        // not hard-code a count that drifts when the fixture shape changes.
        var expectedYmaps = Directory.GetFiles(collection, "*.ymap", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        var expectedYbns = Directory.GetFiles(collection, "*.ybn", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        var outputYmaps = plan.Outputs.Where(o => o.Name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        var outputYbns = plan.Outputs.Where(o => o.Name.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        Console.WriteLine($"  .. {outputYmaps.Length} YMAP outputs / {outputYbns.Length} YBN outputs from {expectedYmaps.Length} / {expectedYbns.Length} distinct source names");
        check(outputYmaps.SequenceEqual(expectedYmaps, StringComparer.OrdinalIgnoreCase),
            "shared maps coalesce while every unique map keeps a distinct output name");
        check(outputYbns.SequenceEqual(expectedYbns, StringComparer.OrdinalIgnoreCase),
            "shared collisions coalesce while every unique collision keeps a distinct output name");
        check(outputYmaps.Length == uniqueMaps + 1 && outputYmaps.Length < roots.Count * 2,
            "shared map sources do not multiply into one output per resource");
        var shared = plan.Outputs.Single(o => o.Name.Equals("shared_interior.ymap", StringComparison.OrdinalIgnoreCase));
        check(shared.Sources.Count == sharedMaps, "shared map output records every contributing resource");
        var mergedMap = new YmapFile(); mergedMap.Load(shared.Data);
        check(mergedMap.AllEntities.Length == sharedMaps * 5, "merged shared map retains every unique placement from every resource");

        var exports = Path.Combine(directory, "exports");
        Directory.CreateDirectory(exports);
        watch.Restart();
        var result = workspace.ExportSeparateResources(exports);
        watch.Stop();
        Console.WriteLine($"  .. exported {result.ResourcePaths.Count} resources / {result.Files} files in {watch.ElapsedMilliseconds} ms");
        check(result.ResourcePaths.Count == roots.Count, "one _merged output folder is created for every resource");
        var sharedPaths = result.ResourcePaths.Select(p => Path.Combine(p, "stream", "shared_interior.ymap")).Where(File.Exists).ToArray();
        check(sharedPaths.Length == sharedMaps && sharedPaths.Select(Digest).Distinct(StringComparer.Ordinal).Count() == 1,
            "every resource receives identical merged bytes for the shared map");
        var noteTexts = result.ResourcePaths.Select(p => Path.Combine(p, "notes.txt")).Where(File.Exists).Select(File.ReadAllText).ToArray();
        check(noteTexts.Count(t => t.StartsWith("resource ", StringComparison.Ordinal)) == sharedMaps,
            "non-map resource files and their original layout are preserved");
        check(File.Exists(Path.Combine(result.ResourcePaths.Single(p => Path.GetFileName(p).StartsWith("Direct_resource", StringComparison.OrdinalIgnoreCase)), "direct_unique.ymap")),
            "direct-root resource layout is preserved in its own export");
        check(hashes.All(pair => File.Exists(pair.Key) && Digest(pair.Key) == pair.Value),
            "stress import, merge and export leave every source byte unchanged");

        var repeat = workspace.Import(new[] { collection });
        check(repeat.AlreadyImported && !repeat.AutoMergeRecommended && workspace.Packs.Count == roots.Count,
            "dropping the same collection again is a clean no-op instead of an error or duplicate sources");

        string empty = Path.Combine(directory, "empty"); Directory.CreateDirectory(empty);
        Expect<InvalidDataException>(() => new MergeWorkspace { BuildPreview = false }.Import(new[] { empty }),
            "empty folder still reports that no supported assets were found", check);
        string junk = Path.Combine(directory, "junk"); Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(junk, "readme.txt"), "nothing to import");
        Expect<InvalidDataException>(() => new MergeWorkspace { BuildPreview = false }.Import(new[] { junk }),
            "unsupported-only folder still reports that no supported assets were found", check);

        string duplicate = Path.Combine(directory, "[duplicate names]");
        foreach (var branch in new[] { "A", "B" })
        {
            Directory.CreateDirectory(Path.Combine(duplicate, branch));
            CopyDirectory(baseResource, Path.Combine(duplicate, branch, "Same Name"));
        }
        var duplicateWorkspace = new MergeWorkspace { BuildPreview = false };
        var duplicateImport = duplicateWorkspace.Import(new[] { duplicate });
        duplicateWorkspace.AutoMergeResources();
        string duplicateExports = Path.Combine(directory, "duplicate-exports"); Directory.CreateDirectory(duplicateExports);
        check(duplicateImport.ResourceCount == 2, "same leaf resource names in different categories still import separately");
        Expect<InvalidOperationException>(() => duplicateWorkspace.ExportSeparateResources(duplicateExports),
            "same leaf resource names stop export with a clear resource-name conflict", check);
        check(!Directory.EnumerateFileSystemEntries(duplicateExports).Any(),
            "failed duplicate-name export commits no partial output");
        return passed;
    }

    private static void PrepareMapResource(string root, int index, string baseMap, string baseCollision, string alternateCollision)
    {
        string stream = Path.Combine(root, "stream");
        Directory.CreateDirectory(stream);
        foreach (var file in Directory.GetFiles(root, "*.ymap", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var file in Directory.GetFiles(root, "*.ybn", SearchOption.AllDirectories)) File.Delete(file);
        WriteMap(baseMap, Path.Combine(stream, "shared_interior.ymap"), "shared_interior", index);
        WriteMap(baseMap, Path.Combine(stream, "unique_" + index.ToString("00") + ".ymap"), "unique_" + index.ToString("00"), index + 100);
        File.Copy(index % 2 == 0 ? baseCollision : alternateCollision, Path.Combine(stream, "shared_collision.ybn"));
        File.Copy(baseCollision, Path.Combine(stream, "unique_" + index.ToString("00") + ".ybn"));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "resource " + index);
    }

    private static void WriteMap(string source, string target, string name, int variant)
    {
        var map = new YmapFile();
        map.Load(File.ReadAllBytes(source));
        for (int i = 0; i < map.AllEntities.Length; i++)
        {
            var definition = map.AllEntities[i]._CEntityDef;
            float half = 0.01f * (variant + 1) / 2f;
            definition.rotation = new Vector4(0, 0, (float)Math.Sin(half), (float)Math.Cos(half));
            map.AllEntities[i]._CEntityDef = definition;
        }
        map.SetName(name);
        File.WriteAllBytes(target, map.Save());
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
        foreach (var child in Directory.GetDirectories(source))
            CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
    }

    private static string Digest(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream));
    }

    private static void Expect<T>(Action action, string label, Action<bool, string> check) where T : Exception
    {
        try { action(); }
        catch (T) { check(true, label); return; }
        check(false, "Expected " + typeof(T).Name + ": " + label);
    }
}
