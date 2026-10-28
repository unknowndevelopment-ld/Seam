using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeWalker.GameFiles;
using Seam;

internal static class EncryptedSkipChecks
{
    public static int Run(string directory)
    {
        int count = 0;
        Action<bool, string> check = (ok, text) => { if (!ok) throw new InvalidOperationException(text); count++; Console.WriteLine("  OK " + text); };
        var sources = MergeWorkspace.CreateDemo(Path.Combine(directory, "sources"));
        var originals = new Dictionary<string, byte[]>();
        foreach (var extension in new[] { ".ydr", ".ydd", ".yft", ".ytd", ".ytyp", ".ymap", ".ybn" })
        {
            string name = "protected_asset" + extension;
            byte[] data = Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray();
            Array.Copy(new byte[] { (byte)'C', (byte)'F', (byte)'X', (byte)'E' }, data, 4);
            File.WriteAllBytes(Path.Combine(sources[0], "stream", name), data);
            originals[name] = data;
        }
        // A protected higher-priority map cannot be silently replaced by a merge
        // consisting only of the readable lower-priority copy.
        File.Copy(Directory.GetFiles(sources[1], "*.ymap", SearchOption.AllDirectories).Single(), Path.Combine(sources[1], "stream", "protected_asset.ymap"));
        byte[] protectedScript = new byte[] { 0x46, 0x58, 0x41, 0x50, 0, 9, 0xff, 12 };
        File.WriteAllBytes(Path.Combine(sources[0], "client.lua"), protectedScript);
        File.AppendAllText(Path.Combine(sources[0], "fxmanifest.lua"), "client_script 'client.lua'\n");
        var workspace = new MergeWorkspace(); workspace.Import(sources);
        check(workspace.Packs.Count == 2, "protected assets do not abort resource import");
        var plan = workspace.AutoMerge();
        check(plan.Outputs.Any(o => o.Name == "quay_district.ymap") && plan.Outputs.Any(o => o.Name == "foundry_yard.ymap"), "readable YMAPs are processed beside protected assets while script-linked names remain intact");
        foreach (var original in originals)
        {
            var output = plan.Outputs.Single(o => o.Name == original.Key);
            check(output.Data.SequenceEqual(original.Value), "encrypted/unsupported bytes are preserved without decoding: " + original.Key);
            check(plan.Operations.Any(o => o.Title == original.Key && o.Kind == "Skipped encrypted / unsupported"), "skipped asset is explained in the report: " + original.Key);
        }
        foreach (var asset in workspace.Packs[0].Assets.Where(a => a.Extension != ".ymap" && a.Extension != ".ybn"))
            check(plan.Outputs.Single(o => o.Name == asset.Name).Data.SequenceEqual(File.ReadAllBytes(asset.Path)), "non-map asset content remains byte-identical: " + asset.Name);
        check(plan.Operations.All(o => o.Kind != "Textures combined" && o.Kind != "Drawables combined" && o.Kind != "Archetypes combined"), "automatic content merging is restricted to YMAP and YBN");
        string exports = Path.Combine(directory, "exports"); Directory.CreateDirectory(exports);
        var result = workspace.ExportAutomatic(exports, "protected_test");
        foreach (var original in originals)
            check(File.ReadAllBytes(Path.Combine(result.Path, "stream", original.Key)).SequenceEqual(original.Value), "export passes through protected file bytes: " + original.Key);
        check(File.ReadAllBytes(Path.Combine(result.Path, "client.lua")).SequenceEqual(protectedScript), "protected Lua script is copied without reading or rewriting its content");
        check(File.ReadAllText(Path.Combine(result.Path, "seam-merge-report.json")).Contains("automatic-ymap-ybn-only"), "export report records the YMAP/YBN-only merge policy");
        foreach (var original in originals) check(File.ReadAllBytes(Path.Combine(sources[0], "stream", original.Key)).SequenceEqual(original.Value), "protected source is untouched: " + original.Key);
        return count;
    }
}
