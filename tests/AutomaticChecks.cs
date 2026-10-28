using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodeWalker.GameFiles;
using Seam;

internal static class AutomaticChecks
{
    private static int passed;

    public static int Run(string directory)
    {
        passed = 0;
        string root = Path.Combine(directory, "automatic");
        Directory.CreateDirectory(root);
        string exports = Path.Combine(root, "exports"); Directory.CreateDirectory(exports);
        string[] sources = MergeWorkspace.CreateDemo(Path.Combine(root, "sources"));
        var before = HashTree(sources);
        var workspace = new MergeWorkspace(); workspace.Import(sources);
        Assert(workspace.ActiveIssueCount > 0, "automatic fixture starts with conflicts and no manual choices");
        var plan = workspace.AutoMerge();
        Assert(ReferenceEquals(plan, workspace.LastAutomaticMerge), "automatic plan is exposed to the desktop results panel");
        var maps = plan.Outputs.Where(o => o.Name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert(maps.Length == 1, "automatic merge combines compatible placement maps into one native YMAP");
        var combined = LoadMap(maps[0].Data);
        Assert(combined.AllEntities.Length == 8 && plan.DuplicateEntities == 1, "combined map retains all eight distinct demo placements and removes the exact duplicate");
        Assert(plan.Outputs.Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == plan.Outputs.Count,
            "automatic outputs contain no duplicate streaming filenames");
        Assert(plan.Outputs.Count(o => o.Name == "seam_materials.ytd") == 1 && plan.DuplicateFiles >= 6,
            "shared model, archetype and texture assets are consolidated automatically");
        Assert(plan.Outputs.Count(o => o.Name.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase)) == 2,
            "separate named collision resources are retained instead of deleting overlapping files");
        var result = workspace.ExportAutomatic(exports, "demo_auto");
        Assert(File.Exists(Path.Combine(result.Path, "fxmanifest.lua")) && File.ReadAllText(Path.Combine(result.Path, "seam-merge-report.json")).Contains("automatic"),
            "automatic export writes a resource and operation report without Resolve calls");
        foreach (var output in plan.Outputs)
        {
            var data = File.ReadAllBytes(Path.Combine(result.Path, "stream", output.Name));
            Assert(data.SequenceEqual(output.Data), "automatic output bytes committed: " + output.Name);
            if (output.Name.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase)) LoadMap(data);
            else if (output.Name.EndsWith(".ybn", StringComparison.OrdinalIgnoreCase)) { var file = new YbnFile(); file.Load(data); Assert(file.Bounds != null, "exported collision is readable: " + output.Name); }
            else if (output.Name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase)) { var file = new YtdFile(); file.Load(data); Assert(file.TextureDict.Textures.Count > 0, "exported texture dictionary is readable"); }
            else if (output.Name.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)) { var file = new YtypFile(); file.Load(data); Assert(file.AllArchetypes.Length == 4, "exported archetypes remain readable"); }
        }
        AssertUnchanged(before, "automatic demo merge leaves every source file unchanged");

        string changed = Directory.GetFiles(sources[0], "*.ytd", SearchOption.AllDirectories).Single();
        byte[] original = File.ReadAllBytes(changed);
        try
        {
            File.WriteAllBytes(changed, original.Concat(new byte[] { 0x42 }).ToArray());
            bool rejected = false;
            try { workspace.ExportAutomatic(exports, "changed_source"); } catch (IOException) { rejected = true; }
            Assert(rejected && !Directory.Exists(Path.Combine(exports, "changed_source")), "cached automatic plans reject changed source bytes before export");
        }
        finally { File.WriteAllBytes(changed, original); }

        CheckCollisionOnly(root, sources, exports);
        CheckTextureUnion(root, exports);
        string school = Environment.GetEnvironmentVariable("SEAM_SCHOOL_FIXTURE");
        if (!string.IsNullOrWhiteSpace(school) && Directory.Exists(school)) CheckSchool(school, exports);
        AssertUnchanged(before, "additional automatic checks preserve procedural sources");
        Console.WriteLine("PASS: " + passed + " automatic merge assertions");
        return passed;
    }

    private static void CheckCollisionOnly(string root, string[] sources, string exports)
    {
        string folder = Path.Combine(root, "collision-only");
        var inputs = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            string pack = Path.Combine(folder, "source" + i); Directory.CreateDirectory(pack);
            File.Copy(Directory.GetFiles(sources[i], "*.ybn", SearchOption.AllDirectories).Single(), Path.Combine(pack, "shared_collision.ybn"));
            inputs.Add(pack);
        }
        var workspace = new MergeWorkspace(); workspace.Import(inputs.ToArray());
        var plan = workspace.AutoMerge();
        Assert(plan.Outputs.Count == 1 && plan.MergedCollisions == 1, "same-name collision-only input merges into one resource");
        var collision = new YbnFile(); collision.Load(plan.Outputs.Single().Data);
        Assert(Leaves(collision.Bounds).Count() == 4, "collision union preserves four distinct components and their transforms");
        var export = workspace.ExportAutomatic(exports, "collision_only");
        Assert(File.Exists(Path.Combine(export.Path, "stream", "shared_collision.ybn")), "collision-only resource exports without a YMAP requirement");
    }

    private static void CheckTextureUnion(string root, string exports)
    {
        var folders = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            string folder = Path.Combine(root, "texture-only", "source" + i); Directory.CreateDirectory(folder); folders.Add(folder);
            var dict = new TextureDictionary();
            dict.BuildFromTextureList(new List<Texture> { Texture("shared", (byte)(10 + i)), Texture("unique_" + i, (byte)(30 + i)) });
            File.WriteAllBytes(Path.Combine(folder, "combined.ytd"), new YtdFile { TextureDict = dict }.Save());
        }
        var workspace = new MergeWorkspace(); workspace.Import(folders.ToArray());
        var plan = workspace.AutoMerge();
        var file = new YtdFile(); file.Load(plan.Outputs.Single().Data);
        Assert(file.TextureDict.Textures.Count == 2, "YTD contents are left unchanged rather than merged");
        Assert(file.TextureDict.Lookup(JenkHash.GenHash("shared")).Data.FullData[0] == 10, "same-name texture content follows source priority automatically");
        Assert(plan.Outputs.Single().Data.SequenceEqual(File.ReadAllBytes(Path.Combine(folders[0], "combined.ytd"))) && plan.Operations.All(o => o.Kind != "Textures combined"), "non-map dictionary stays byte-identical and no texture-content merge runs");
        var export = workspace.ExportAutomatic(exports, "texture_only");
        Assert(File.Exists(Path.Combine(export.Path, "stream", "combined.ytd")), "texture-only input exports as a standalone merged resource");
    }

    private static void CheckSchool(string school, string exports)
    {
        var before = HashTree(new[] { school });
        var workspace = new MergeWorkspace(); workspace.Import(new[] { school });
        Assert(workspace.Packs.Count == 3, "school container automatically identifies all three map resource folders");
        Assert(workspace.Packs.SelectMany(p => p.Assets).All(a => a.Error == null), "all supplied school stream assets import successfully");
        var originalMaps = workspace.Packs.SelectMany(p => p.Assets).Where(a => a.Ymap != null).ToArray();
        Assert(originalMaps.Length == 8, "school import includes eight complete placement/replacement maps");
        var plan = workspace.AutoMerge();
        Assert(plan.MergedCollisions >= 3, "school same-name base collision variants merge automatically");
        foreach (var tile in new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "bh1_29_0.ybn", 11738 },
            { "hi@bh1_35_0.ybn", 6553 },
            { "bh1_35_0.ybn", 9444 }
        })
        {
            var output = plan.Outputs.Single(o => o.Name.Equals(tile.Key, StringComparison.OrdinalIgnoreCase));
            var file = new YbnFile(); file.Load(output.Data);
            int polygons = Leaves(file.Bounds).OfType<BoundGeometry>().Sum(bound => bound.Polygons?.Length ?? 0);
            Assert(polygons == tile.Value, "school collision patch preserves combined removal edits without duplicating primitive polygons: " + tile.Key);
        }
        foreach (var source in originalMaps)
        {
            var output = plan.Outputs.SingleOrDefault(o => o.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
            Assert(output != null && output.Data.SequenceEqual(File.ReadAllBytes(source.Path)), "named school/MLO/replacement map retains exact source bytes: " + source.Name);
        }
        Assert(plan.Outputs.Count(o => o.Name.EndsWith(".ymf", StringComparison.OrdinalIgnoreCase)) == 5,
            "all five MLO streaming manifests are retained");
        var exported = workspace.ExportAutomatic(exports, "school_auto");
        string manifest = File.ReadAllText(Path.Combine(exported.Path, "fxmanifest.lua"));
        string originalClient = Path.Combine(school, "MilosSLGC", "client.lua");
        Assert(File.Exists(Path.Combine(exported.Path, "client.lua")) && Digest(originalClient) == Digest(Path.Combine(exported.Path, "client.lua")),
            "school local client behavior is copied byte for byte");
        Assert(manifest.Contains("@WaveShield/resource/include.lua"), "external WaveShield manifest declaration remains present");
        Assert(Directory.GetFiles(Path.Combine(exported.Path, "stream"), "*.ymf").Length == 5,
            "school export writes every MLO streaming manifest");
        AssertUnchanged(before, "automatic merge/export never alters the supplied school pack files");
    }

    private static Texture Texture(string name, byte value)
    {
        JenkIndex.Ensure(name);
        byte[] data = new byte[4 * 4 * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = value; data[i + 3] = 255; }
        return new Texture { Name = name, NameHash = JenkHash.GenHash(name), Width = 4, Height = 4, Depth = 1, Stride = 16, Levels = 1,
            Format = TextureFormat.D3DFMT_A8R8G8B8, Data = new TextureData { FullData = data } };
    }
    private static YmapFile LoadMap(byte[] bytes) { var file = new YmapFile(); file.Load(bytes); return file; }
    private static IEnumerable<Bounds> Leaves(Bounds bound)
    {
        if (bound is BoundComposite composite)
        {
            foreach (var child in composite.Children?.data_items ?? new Bounds[0])
                if (child != null) foreach (var leaf in Leaves(child)) yield return leaf;
        }
        else if (bound != null) yield return bound;
    }
    private static Dictionary<string, string> HashTree(IEnumerable<string> roots) => roots.SelectMany(p => Directory.GetFiles(p, "*", SearchOption.AllDirectories)).ToDictionary(p => p, Digest);
    private static string Digest(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return Convert.ToBase64String(sha.ComputeHash(stream)); }
    private static void AssertUnchanged(Dictionary<string, string> hashes, string label) => Assert(hashes.All(pair => File.Exists(pair.Key) && Digest(pair.Key) == pair.Value), label);
    private static void Assert(bool value, string label) { if (!value) throw new InvalidOperationException("Automatic check failed: " + label); passed++; Console.WriteLine("  OK " + label); }
}
