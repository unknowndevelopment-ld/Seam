using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Seam;

internal static class TextureEditorChecks
{
    public static void Run(string directory, Action<bool, string> assert)
    {
        string folder = Path.Combine(directory, "texture-studio"); Directory.CreateDirectory(folder);
        string[] maps = DemoFactory.Create(Path.Combine(folder, "native-fixture"));
        string source = Path.Combine(maps[0], "stream", "seam_materials.ytd"); byte[] sourceBytes = File.ReadAllBytes(source);
        var document = TextureDocument.Open(source);
        assert(document.Textures.Count == 2, "Texture studio decodes actual native YTD dictionary entries");
        var original = document.Textures[0]; var image = TextureDocument.ReadBitmap(original);
        assert(image.PixelWidth == 64 && image.PixelHeight == 64, "Texture studio decodes native texture pixels");
        var artwork = new ArtworkSurface(); artwork.Reset(image);
        var layer = artwork.AddText("A", "#FF00FF"); layer.X = 8; layer.Y = 8; layer.Size = 32; artwork.UpdateLayer(layer);
        var rendered = artwork.Render(); byte[] before = Pixels(rendered);
        assert(!Pixels(image).SequenceEqual(before), "Native artwork text layers modify rendered pixels");
        string artPath = Path.Combine(folder, "design.artwork.json"); artwork.SaveArtwork(artPath);
        // Background textures are kept separately from editable layers in an exported dictionary.
        string imagePath = Path.Combine(folder, "background.png"); TextureDocument.SavePng(image, imagePath);
        artwork.Data.BackgroundPath = imagePath; artwork.SaveArtwork(artPath);
        var restored = new ArtworkSurface(); restored.LoadArtwork(artPath);
        assert(Pixels(restored.Render()).SequenceEqual(before), "Artwork sidecar restores image and editable text layers without pixel changes");
        var replacement = document.Replace(original, rendered);
        assert(replacement.Levels == 7 && replacement.Name == original.Name, "Texture replacement preserves sampler name and creates a full mip chain");
        string saved = Path.Combine(folder, "edited.ytd"); document.Save(saved);
        var reloaded = TextureDocument.Open(saved); var nativeImage = TextureDocument.ReadBitmap(reloaded.Textures.Single(t => t.Name == replacement.Name));
        assert(Pixels(nativeImage).SequenceEqual(before), "Edited artwork round-trips through saved native YTD");
        assert(File.ReadAllBytes(source).SequenceEqual(sourceBytes), "Texture editing and saving a copy preserves original source bytes");
        string dds = Path.Combine(folder, "edited.dds"); TextureDocument.SaveDds(replacement, dds);
        assert(Pixels(TextureDocument.LoadImage(dds)).SequenceEqual(before), "DDS export and import preserve edited pixels");
        string locked = Path.Combine(folder, "protected.ytd"); File.WriteAllBytes(locked, new byte[] { 70, 88, 65, 80 });
        bool refused = false; try { TextureDocument.Open(locked); } catch (InvalidDataException) { refused = true; }
        assert(refused && File.ReadAllBytes(locked).Length == 4, "Protected texture resources are reported without attempting to decrypt or overwrite");
        bool invalid = false; try { TextureDocument.FromBitmap("invalid", BitmapSource.Create(3, 3, 96, 96, PixelFormats.Bgra32, null, new byte[36], 12)); } catch (InvalidDataException) { invalid = true; }
        assert(invalid, "Texture studio rejects non-power-of-two dimensions before saving broken assets");
    }
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; converted.CopyPixels(data, bitmap.PixelWidth * 4, 0); return data;
    }
}
