using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodeWalker.GameFiles;
using CodeWalker.Utils;

namespace Seam
{
    // Edits a private in-memory dictionary. Saving always requires an explicit output path.
    public sealed class TextureDocument
    {
        public string SourcePath { get; private set; }
        public List<Texture> Textures { get; private set; } = new List<Texture>();
        public bool IsDirty { get; private set; }
        public static TextureDocument Open(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 16 || BitConverter.ToUInt32(bytes, 0) != 0x37435352)
                throw new InvalidDataException("This dictionary is encrypted or isn't an extracted RSC7 YTD. Its source has been preserved.");
            var file = new YtdFile(); file.Load(bytes);
            return new TextureDocument { SourcePath = path, Textures = (file.TextureDict?.Textures?.data_items ?? new Texture[0]).Where(t => t != null).ToList() };
        }
        public static BitmapSource ReadBitmap(Texture texture)
        {
            var pixels = DDSIO.GetPixels(texture, 0);
            if (pixels == null || pixels.Length < texture.Width * texture.Height * 4) throw new InvalidDataException("This texture format cannot be decoded.");
            var bitmap = BitmapSource.Create(texture.Width, texture.Height, 96, 96, PixelFormats.Bgra32, null, pixels, texture.Width * 4);
            bitmap.Freeze(); return bitmap;
        }
        public static BitmapSource LoadImage(string path)
        {
            if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase)) return ReadBitmap(DDSIO.GetTexture(File.ReadAllBytes(path)));
            using (var stream = File.OpenRead(path))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0); bitmap.Freeze(); return bitmap;
            }
        }
        public static Texture FromBitmap(string name, BitmapSource bitmap)
        {
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || (width & (width - 1)) != 0 || (height & (height - 1)) != 0)
                throw new InvalidDataException("Use power-of-two dimensions between 1 and 4096 pixels (for example 1024 × 1024).");
            int dataLength = 0; byte levels = 0;
            for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
            { dataLength += w * h * 4; levels++; if (w == 1 && h == 1) break; }
            var data = new byte[dataLength]; int offset = 0;
            for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
            {
                BitmapSource mip = bitmap;
                if (w != width || h != height) mip = new TransformedBitmap(bitmap, new ScaleTransform((double)w / width, (double)h / height));
                mip = new FormatConvertedBitmap(mip, PixelFormats.Bgra32, null, 0);
                mip.CopyPixels(data, w * 4, offset); offset += w * h * 4;
                if (w == 1 && h == 1) break;
            }
            JenkIndex.Ensure(name);
            return new Texture { Name = name, NameHash = JenkHash.GenHash(name), Width = (ushort)width, Height = (ushort)height, Depth = 1,
                Stride = (ushort)(width * 4), Levels = levels, Format = TextureFormat.D3DFMT_A8R8G8B8, Data = new TextureData { FullData = data } };
        }
        public Texture Replace(Texture original, BitmapSource bitmap)
        {
            var texture = FromBitmap(original.Name, bitmap);
            texture.Usage = original.Usage; texture.UsageFlags = original.UsageFlags;
            Textures[Textures.IndexOf(original)] = texture; IsDirty = true; return texture;
        }
        public Texture Add(string name, BitmapSource bitmap)
        {
            if (Textures.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("A texture with this name already exists.");
            var texture = FromBitmap(name, bitmap); Textures.Add(texture); IsDirty = true; return texture;
        }
        public void Save(string path)
        {
            var dictionary = new TextureDictionary(); dictionary.BuildFromTextureList(Textures);
            var bytes = new YtdFile { TextureDict = dictionary }.Save();
            // Read our output before publishing it, so a bad codec result never replaces a file.
            var verify = new YtdFile(); verify.Load(bytes);
            if (verify.TextureDict?.Textures?.data_items?.Length != Textures.Count) throw new InvalidDataException("The saved dictionary failed verification.");
            File.WriteAllBytes(path, bytes); IsDirty = false;
        }
        public static void SavePng(BitmapSource bitmap, string path)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
        public static void SaveDds(Texture texture, string path) { File.WriteAllBytes(path, DDSIO.GetDDSFile(texture)); }
    }

    public sealed class ArtworkLayer
    {
        internal BitmapSource CachedImage;
        internal string CachedImagePath;
        public string Name { get; set; }
        public string ImagePath { get; set; }
        public string Text { get; set; }
        public string Color { get; set; } = "#FFFFFF";
        public double X { get; set; } = 256;
        public double Y { get; set; } = 256;
        public double Size { get; set; } = 256;
        public double Opacity { get; set; } = 1;
        public double Rotation { get; set; }
        public override string ToString() => Name;
    }
    public sealed class ArtworkData
    {
        public int Size { get; set; } = 1024;
        public string BackgroundPath { get; set; }
        public string Ink { get; set; }
        public List<ArtworkLayer> Layers { get; set; } = new List<ArtworkLayer>();
    }

    // Shared native paint surface for textures and tattoos. Pixel coordinates remain stable when the window resizes.
    public sealed class ArtworkSurface : Grid
    {
        private Canvas canvas = new Canvas();
        private readonly InkCanvas ink = new InkCanvas { Background = Brushes.Transparent };
        private readonly Viewbox zoom = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(24) };
        private readonly Dictionary<ArtworkLayer, FrameworkElement> elements = new Dictionary<ArtworkLayer, FrameworkElement>();
        private readonly Brush checker;
        private Point dragPoint;
        private ArtworkLayer dragged;
        private BitmapSource background;
        public ArtworkData Data { get; private set; } = new ArtworkData();
        public ArtworkLayer SelectedLayer { get; set; }
        public event Action Changed;
        public event Action<ArtworkLayer> LayerSelected;
        public ArtworkSurface()
        {
            Background = new SolidColorBrush(Color.FromRgb(19, 22, 28));
            var tile = new DrawingGroup(); tile.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(40, 44, 53)), null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
            tile.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(49, 54, 63)), null, Geometry.Parse("M0,0 H16 V16 H0 Z M16,16 H32 V32 H16 Z")));
            canvas.Background = new DrawingBrush(tile) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 32, 32) };
            checker = canvas.Background;
            zoom.Child = canvas; Children.Add(zoom); canvas.Width = canvas.Height = 1024; ink.Width = ink.Height = 1024;
            canvas.Children.Add(ink); SetMode("Select");
            ink.StrokeCollected += (s, e) => { Repaint(); Changed?.Invoke(); }; ink.StrokeErased += (s, e) => { Repaint(); Changed?.Invoke(); };
            ink.DefaultDrawingAttributes = new DrawingAttributes { Color = Colors.White, Width = 12, Height = 12, FitToCurve = true };
        }
        public void SetMode(string mode)
        {
            ink.EditingMode = mode == "Draw" ? InkCanvasEditingMode.Ink : mode == "Erase" ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.None;
            ink.IsHitTestVisible = mode != "Select";
            ink.Opacity = mode == "Draw" ? 1 : 0;
        }
        public void SetBrush(string color, double size)
        {
            ink.DefaultDrawingAttributes.Color = (Color)ColorConverter.ConvertFromString(color);
            ink.DefaultDrawingAttributes.Width = ink.DefaultDrawingAttributes.Height = size;
        }
        public void Reset(BitmapSource image = null, string imagePath = null)
        {
            Data = new ArtworkData { Size = image?.PixelWidth ?? 1024, BackgroundPath = imagePath };
            background = image; SelectedLayer = null; ink.Strokes.Clear(); Rebuild();
        }
        private void Rebuild()
        {
            canvas.Children.Clear(); zoom.Child = null;
            canvas = new Canvas { Width = Data.Size, Height = background?.PixelHeight ?? Data.Size, Background = checker };
            zoom.Child = canvas; elements.Clear();
            ink.Width = canvas.Width; ink.Height = canvas.Height;
            if (background != null) canvas.Children.Add(new Image { Source = background, Width = canvas.Width, Height = canvas.Height, IsHitTestVisible = false });
            foreach (var layer in Data.Layers) AddElement(layer);
            canvas.Children.Add(ink);
            canvas.InvalidateMeasure(); zoom.InvalidateMeasure(); InvalidateMeasure();
            Repaint();
        }
        public ArtworkLayer AddText(string text, string color)
        {
            ColorConverter.ConvertFromString(color);
            var layer = new ArtworkLayer { Name = text, Text = text, Color = color, X = Data.Size * .25, Y = Data.Size * .4, Size = Data.Size * .12 };
            Data.Layers.Add(layer); Rebuild(); SelectedLayer = layer; Changed?.Invoke(); return layer;
        }
        public ArtworkLayer AddImage(string path)
        {
            var layer = new ArtworkLayer { Name = Path.GetFileName(path), ImagePath = path, X = Data.Size * .25, Y = Data.Size * .25, Size = Data.Size * .5 };
            Data.Layers.Add(layer); Rebuild(); SelectedLayer = layer; Changed?.Invoke(); return layer;
        }
        private void AddElement(ArtworkLayer layer)
        {
            FrameworkElement element = CreateElement(layer);
            elements[layer] = element; canvas.Children.Add(element); UpdateLayer(layer, false);
            element.MouseLeftButtonDown += (s, e) => { dragged = SelectedLayer = layer; dragPoint = e.GetPosition(canvas); element.CaptureMouse(); LayerSelected?.Invoke(layer); e.Handled = true; };
            element.MouseMove += (s, e) =>
            {
                if (dragged != layer || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(canvas); layer.X += p.X - dragPoint.X; layer.Y += p.Y - dragPoint.Y; dragPoint = p; UpdateLayer(layer);
            };
            element.MouseLeftButtonUp += (s, e) => { dragged = null; element.ReleaseMouseCapture(); };
        }
        private static FrameworkElement CreateElement(ArtworkLayer layer)
        {
            if (!string.IsNullOrEmpty(layer.ImagePath) && (layer.CachedImage == null || layer.CachedImagePath != layer.ImagePath))
            { layer.CachedImage = TextureDocument.LoadImage(layer.ImagePath); layer.CachedImagePath = layer.ImagePath; }
            FrameworkElement element = !string.IsNullOrEmpty(layer.ImagePath)
                ? (FrameworkElement)new Image { Source = layer.CachedImage, Stretch = Stretch.Uniform }
                : new TextBlock { Text = layer.Text, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(layer.Color)) };
            PlaceLayer(layer, element); return element;
        }
        public void UpdateLayer(ArtworkLayer layer, bool notify = true)
        {
            if (layer == null || !elements.TryGetValue(layer, out var element)) return;
            PlaceLayer(layer, element);
            // Rasterize the composition once at source resolution; transparent
            // hit targets keep layer dragging independent of Viewbox glyph caches.
            element.Opacity = 0;
            if (notify) { Repaint(); Changed?.Invoke(); }
        }
        private static void PlaceLayer(ArtworkLayer layer, FrameworkElement element)
        {
            Canvas.SetLeft(element, layer.X); Canvas.SetTop(element, layer.Y); element.Opacity = layer.Opacity;
            element.RenderTransform = new RotateTransform(layer.Rotation); element.RenderTransformOrigin = new Point(.5, .5);
            if (element is TextBlock text) text.FontSize = layer.Size;
            else { element.Width = layer.Size; element.Height = layer.Size; }
        }
        public void DeleteSelected()
        {
            if (SelectedLayer == null) return; Data.Layers.Remove(SelectedLayer); SelectedLayer = null; Rebuild(); Changed?.Invoke();
        }
        public void UndoStroke() { if (ink.Strokes.Count > 0) { ink.Strokes.RemoveAt(ink.Strokes.Count - 1); Repaint(); Changed?.Invoke(); } }
        private void Repaint()
        {
            if (checker == null) return;
            var rect = new Rect(0, 0, Data.Size, background?.PixelHeight ?? Data.Size);
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(checker, null, new RectangleGeometry(rect)));
            drawing.Children.Add(new ImageDrawing(Render(), rect));
            canvas.Background = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        }
        public BitmapSource Render()
        {
            // Render an independent visual, so live previews cannot disturb the editor's Viewbox layout.
            var export = new Canvas { Width = Data.Size, Height = background?.PixelHeight ?? Data.Size };
            if (background != null) export.Children.Add(new Image { Source = background, Width = export.Width, Height = export.Height });
            foreach (var layer in Data.Layers) export.Children.Add(CreateElement(layer));
            export.Children.Add(new InkPresenter { Width = export.Width, Height = export.Height, Strokes = ink.Strokes.Clone() });
            var size = new Size(export.Width, export.Height); export.Measure(size); export.Arrange(new Rect(size)); export.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)export.Width, (int)export.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(export);
            var result = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0); result.Freeze(); return result;
        }
        public void SaveArtwork(string path)
        {
            using (var stream = new MemoryStream()) { ink.Strokes.Save(stream); Data.Ink = Convert.ToBase64String(stream.ToArray()); }
            File.WriteAllText(path, new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Serialize(Data));
        }
        public void LoadArtwork(string path)
        {
            Data = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Deserialize<ArtworkData>(File.ReadAllText(path));
            background = string.IsNullOrEmpty(Data.BackgroundPath) ? null : TextureDocument.LoadImage(Data.BackgroundPath);
            ink.Strokes.Clear(); if (!string.IsNullOrEmpty(Data.Ink)) using (var stream = new MemoryStream(Convert.FromBase64String(Data.Ink))) ink.Strokes = new StrokeCollection(stream);
            SelectedLayer = null; Rebuild();
        }
    }
}
