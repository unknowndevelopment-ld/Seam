using System;
using System.IO;
using System.Linq;
using Seam;

static class GameInstallationChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        string previous = Environment.GetEnvironmentVariable("SEAM_SETTINGS_PATH");
        string file = Path.Combine(root, "installation", "settings.json");
        try
        {
            Environment.SetEnvironmentVariable("SEAM_SETTINGS_PATH", file); GameInstallation.Reload();
            check(GameInstallation.NeedsSetup && !GameInstallation.Available, "fresh installations require an explicit first-run choice");
            GameInstallation.Select(null); GameInstallation.Reload();
            check(!GameInstallation.NeedsSetup && !GameInstallation.Available && GameInstallation.SelectedFolder == null, "continuing without GTA persists and does not auto-detect a different installation");
            bool rejected = false; try { GameInstallation.Select(Path.Combine(root, "missing-game")); } catch (ArgumentException) { rejected = true; }
            check(rejected && !GameInstallation.Available, "invalid game folders cannot enable previews");
            string game = Path.Combine(root, "validation-fixture"); Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "GTA5.exe"), new byte[1]); File.WriteAllBytes(Path.Combine(game, "x64v.rpf"), new byte[1]);
            GameInstallation.Select(game); GameInstallation.Reload();
            check(GameInstallation.Available && GameInstallation.SelectedFolder == game && !GameInstallation.NeedsSetup, "an explicit installation choice survives a settings reload");
            File.Delete(Path.Combine(game, "x64v.rpf"));
            check(!GameInstallation.Available && GameInstallation.NeedsSetup, "a removed installation disables previews and requests setup again");
            GameInstallation.Select(null);
            string[] inputs = DemoFactory.Create(Path.Combine(root, "no-preview-input"));
            var withPreview = new MergeWorkspace(); withPreview.Import(inputs); var expected = withPreview.AutoMergeResources();
            var withoutPreview = new MergeWorkspace { BuildPreview = false }; withoutPreview.Import(inputs); var actual = withoutPreview.AutoMergeResources();
            check(withoutPreview.Scene.Count == 0 && withoutPreview.EntityCount > 0, "map data imports without building a 3D scene");
            check(actual.MergedMaps == expected.MergedMaps && actual.MergedCollisions == expected.MergedCollisions && actual.MapRepairs == expected.MapRepairs, "map merging and repair counts are unchanged without previews");
            check(actual.Outputs.Count == expected.Outputs.Count && actual.Outputs.All(o => expected.Outputs.Any(e => e.Name == o.Name && ((e.Data == null && o.Data == null && e.CopySourcePath == o.CopySourcePath) || (e.Data != null && o.Data != null && e.Data.SequenceEqual(o.Data))))), "preview-free exports retain exactly the same merged native bytes");
            string project = Path.Combine(root, "without-preview.seam"); withoutPreview.SaveProject(project);
            var restored = MergeWorkspace.LoadProject(project, null, false);
            check(!restored.BuildPreview && restored.Scene.Count == 0 && restored.Packs.Count == 2, "saved map projects reopen without enabling 3D");
            string export = Path.Combine(root, "no-preview-export"); Directory.CreateDirectory(export);
            check(withoutPreview.ExportSeparateResources(export).ResourcePaths.Count == 2, "separate map resource exports work without a selected game");
            string clothing = Path.Combine(root, "no-game-clothes"); Directory.CreateDirectory(clothing);
            File.Copy(Directory.GetFiles(inputs[0], "seam_tower.ydr", SearchOption.AllDirectories).Single(), Path.Combine(clothing, "jbib_000_u.ydr"));
            File.Copy(Directory.GetFiles(inputs[0], "seam_materials.ytd", SearchOption.AllDirectories).Single(), Path.Combine(clothing, "jbib_diff_000_a_uni.ytd"));
            var collection = new ClothingProject(); collection.ImportFolder(clothing); string pack = collection.ExportFiveM(export, "without_gta");
            check(!GameInstallation.Available && Directory.GetFiles(pack, "*.ymt", SearchOption.AllDirectories).Length > 0, "native clothing packs build without a selected game installation");
        }
        finally { Environment.SetEnvironmentVariable("SEAM_SETTINGS_PATH", previous); GameInstallation.Reload(); }
    }
}
