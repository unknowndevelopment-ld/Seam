using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using CodeWalker.GameFiles;
using CodeWalker.Utils;
using Dx = SharpDX;

namespace Seam
{
    /// <summary>Native, textured rest-pose preview. No copyrighted ped assets ship with the application.</summary>
    public sealed class ClothingViewport : Grid
    {
        readonly Viewport3D view = new Viewport3D();
        readonly ModelVisual3D content = new ModelVisual3D();
        readonly PerspectiveCamera camera = new PerspectiveCamera { FieldOfView = 38, NearPlaneDistance = .01, FarPlaneDistance = 100, UpDirection = new Vector3D(0, 0, 1) };
        readonly Dictionary<string, BitmapSource> edits = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
        readonly List<Surface> surfaces = new List<Surface>();
        ClothingPedAsset ped;
        ClothingItem selected;
        List<Drawable> apparel = new List<Drawable>();
        List<TextureDictionary> apparelTextures = new List<TextureDictionary>();
        int request;
        double distance = 2.8, azimuth = -Math.PI / 2, elevation = .12;
        Point3D target = new Point3D(0, 0, 0);
        Point last;
        bool dragging, panning;
        BitmapSource tattoo;
        string tattooZone;
        double tattooU, tattooV, tattooScaleX, tattooScaleY, tattooRotation;
        public event Action<string> StatusChanged;
        public string Status { get; private set; } = "Select your GTA V folder to load the freemode ped.";
        public bool HasPed => ped != null;
        public int MeshCount { get; private set; }
        public string PedName => ped?.Name ?? "";

        public ClothingViewport()
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 22, 29));
            ClipToBounds = true;
            Focusable = true;
            view.Camera = camera;
            view.Children.Add(content);
            Children.Add(view);
            MouseDown += (s, e) => {
                if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right && e.ChangedButton != MouseButton.Middle) return;
                last = e.GetPosition(this); dragging = true; panning = e.ChangedButton != MouseButton.Left || Keyboard.Modifiers == ModifierKeys.Shift;
                CaptureMouse(); Focus(); e.Handled = true;
            };
            MouseMove += (s, e) => {
                if (!dragging) return;
                var next = e.GetPosition(this); double x = next.X-last.X, y = next.Y-last.Y; last = next;
                if (panning)
                {
                    var right = new Vector3D(-Math.Sin(azimuth), Math.Cos(azimuth), 0);
                    target += right * (-x * distance / Math.Max(200, ActualHeight)) + new Vector3D(0, 0, y * distance / Math.Max(200, ActualHeight));
                }
                else { azimuth -= x * .008; elevation = Math.Max(-1.4, Math.Min(1.4, elevation + y * .006)); }
                UpdateCamera(); e.Handled = true;
            };
            MouseUp += (s, e) => { dragging = false; ReleaseMouseCapture(); };
            LostMouseCapture += (s, e) => dragging = false;
            MouseWheel += (s, e) => { distance = Math.Max(.12, Math.Min(30, distance * Math.Exp(-e.Delta / 1000.0))); UpdateCamera(); e.Handled = true; };
            UpdateCamera();
            Rebuild();
        }

        public async Task SetPedAsync(string gender, string gameFolder)
        {
            int token = ++request;
            try
            {
                SetStatus("Loading the freemode ped from your game…");
                var asset = await Task.Run(() => ClothingGameAssets.LoadPed(gender, gameFolder,
                    message => Dispatcher.BeginInvoke(new Action(() => { if (token == request) SetStatus(message); }))));
                if (token != request) return;
                ped = asset; edits.Clear();
                if (selected != null && selected.Ped != gender) { selected = null; apparel.Clear(); apparelTextures.Clear(); }
                Rebuild(); FrameAll();
                SetStatus(ped.Name + " · textured rest pose · drag to orbit, right-drag to pan");
            }
            catch (Exception error)
            {
                if (token != request) return;
                ped = null; Rebuild(); SetStatus(error.Message);
            }
        }

        public void SetPed(string gender, string gameFolder)
        {
            ++request;
            try
            {
                ped = ClothingGameAssets.LoadPed(gender, gameFolder, SetStatus); edits.Clear();
                if (selected != null && selected.Ped != gender) { selected = null; apparel.Clear(); apparelTextures.Clear(); }
                Rebuild(); FrameAll(); SetStatus(ped.Name + " · textured rest pose");
            }
            catch (Exception error) { ped = null; Rebuild(); SetStatus(error.Message); }
        }

        public void ShowItem(ClothingItem item)
        {
            selected = item; apparel.Clear(); apparelTextures.Clear(); edits.Clear();
            if (item != null)
            {
                try
                {
                    string extension = Path.GetExtension(item.SourcePath).ToLowerInvariant();
                    if (extension == ".ydd")
                    {
                        var ydd = new YddFile(); ydd.Load(File.ReadAllBytes(item.SourcePath));
                        apparel.AddRange(ydd.Dict?.Values ?? Enumerable.Empty<Drawable>());
                    }
                    else if (extension == ".ydr")
                    {
                        var ydr = new YdrFile(); ydr.Load(File.ReadAllBytes(item.SourcePath));
                        if (ydr.Drawable != null) apparel.Add(ydr.Drawable);
                    }
                    foreach (var path in item.TexturePaths ?? new List<string>())
                        if (File.Exists(path) && Path.GetExtension(path).Equals(".ytd", StringComparison.OrdinalIgnoreCase))
                        {
                            var ytd = new YtdFile(); ytd.Load(File.ReadAllBytes(path));
                            if (ytd.TextureDict != null) apparelTextures.Add(ytd.TextureDict);
                        }
                    if (apparel.Count == 0) SetStatus("This item has no readable YDD/YDR mesh. Texture editing and export remain available.");
                    else SetStatus((ped?.Name ?? "Clothing mesh") + " · " + item.Name + " · rest pose");
                }
                catch (Exception error) { SetStatus("Mesh preview unavailable: " + error.Message); }
            }
            Rebuild();
            if (ped == null) FrameAll();
        }

        public void RefreshTexture(string textureName, BitmapSource image)
        {
            if (string.IsNullOrWhiteSpace(textureName) || image == null) return;
            edits[textureName] = image;
            bool diffuseVariant = textureName.IndexOf("_diff_", StringComparison.OrdinalIgnoreCase) >= 0;
            foreach (var surface in surfaces.Where(s => string.Equals(s.Name, textureName, StringComparison.OrdinalIgnoreCase) ||
                (diffuseVariant && !s.IsBody && s.Name.IndexOf("_diff_", StringComparison.OrdinalIgnoreCase) >= 0 &&
                 s.Name.Length >= 4 && textureName.Length >= 4 && s.Name.Substring(0,4).Equals(textureName.Substring(0,4),StringComparison.OrdinalIgnoreCase))))
            { edits[surface.Name] = image; surface.Original = image; UpdateSurface(surface); }
        }

        /// <summary>Preview a tattoo in body UV space. The game applies final decoration projection.</summary>
        public void ShowTattoo(BitmapSource image, string zone, double u, double v, double sx, double sy, double rotation)
        {
            tattoo = image; tattooZone = zone; tattooU = u; tattooV = v;
            tattooScaleX = sx; tattooScaleY = sy; tattooRotation = rotation;
            // Skin must remain visible while editing a decoration.
            if (image != null && selected != null) { selected = null; apparel.Clear(); apparelTextures.Clear(); Rebuild(); }
            else foreach (var surface in surfaces) UpdateSurface(surface);
        }

        public void FrameAll()
        {
            var group = content.Content as Model3DGroup;
            var bounds = group?.Bounds ?? Rect3D.Empty;
            if (bounds.IsEmpty) { target = new Point3D(0,0,0); distance = 2.8; }
            else
            {
                target = new Point3D(bounds.X + bounds.SizeX/2, bounds.Y + bounds.SizeY/2, bounds.Z + bounds.SizeZ/2);
                distance = Math.Max(.6, Math.Max(bounds.SizeZ, Math.Max(bounds.SizeX,bounds.SizeY)) * 1.65);
            }
            UpdateCamera();
        }

        void SetStatus(string status) { Status = status; StatusChanged?.Invoke(status); }
        void UpdateCamera()
        {
            var offset = new Vector3D(Math.Cos(azimuth)*Math.Cos(elevation), Math.Sin(azimuth)*Math.Cos(elevation), Math.Sin(elevation)) * distance;
            camera.Position = target + offset; camera.LookDirection = -offset;
        }

        void Rebuild()
        {
            var group = new Model3DGroup();
            group.Children.Add(new AmbientLight(Color.FromRgb(165,165,165)));
            group.Children.Add(new DirectionalLight(Color.FromRgb(190,196,211), new Vector3D(-.4,.6,-1)));
            group.Children.Add(new DirectionalLight(Color.FromRgb(100,109,132), new Vector3D(.8,-.5,.2)));
            surfaces.Clear(); MeshCount = 0;
            if (ped != null)
                foreach (var part in ped.Parts)
                    if (selected == null || selected.IsProp || selected.Component != part.Component)
                        Append(group, part.Drawable, part.Textures, part.Component, true);
            foreach (var drawable in apparel) Append(group, drawable, null, selected?.Component ?? -1, false);
            content.Content = group;
        }

        void Append(Model3DGroup group, Drawable drawable, TextureDictionary textures, int component, bool body)
        {
            var levels = drawable.DrawableModels;
            foreach (var model in levels?.High ?? levels?.Med ?? levels?.Low ?? new CodeWalker.GameFiles.DrawableModel[0])
                foreach (var geometry in model.Geometries ?? new DrawableGeometry[0])
                {
                    var data = geometry.VertexData ?? geometry.VertexBuffer?.Data1;
                    var indices = geometry.IndexBuffer?.Indices;
                    if (data?.Info == null || indices == null || data.VertexCount == 0) continue;
                    var mesh = new MeshGeometry3D();
                    var uvType = data.Info.GetComponentType((int)VertexSemantics.TexCoord0);
                    var normalType = data.Info.GetComponentType((int)VertexSemantics.Normal);
                    var skeleton = drawable.Skeleton ?? ped?.Skeleton;
                    var bones = skeleton?.Bones?.Items;
                    var rigid = Dx.Matrix.Identity;
                    if (model.HasSkin == 0 && bones != null)
                    {
                        if (!body && selected?.IsProp == true && drawable.Skeleton == null)
                        {
                            ushort[] anchors = { 31086, 31086, 31086, 31086, 18905, 57005, 18905, 57005, 11816, 14201, 52301, 60309, 28422 };
                            int anchor = selected.Component;
                            if (anchor >= 0 && anchor < anchors.Length && skeleton.BonesMap != null && skeleton.BonesMap.TryGetValue(anchors[anchor], out var attachment)) rigid = attachment.AnimTransform;
                        }
                        else if (model.BoneIndex < bones.Length) rigid = bones[model.BoneIndex].AnimTransform;
                    }
                    for (int i=0; i<data.VertexCount; i++)
                    {
                        var p = data.GetVector3(i,(int)VertexSemantics.Position);
                        // Rest-pose skin vertices are already in the bind space; rigid submeshes use their bone transform.
                        if (model.HasSkin == 0) p = Dx.Vector3.TransformCoordinate(p, rigid);
                        mesh.Positions.Add(new Point3D(p.X,p.Y,p.Z));
                        if (normalType == VertexComponentType.Float3)
                        {
                            var n = data.GetVector3(i,(int)VertexSemantics.Normal);
                            if (model.HasSkin == 0) n = Dx.Vector3.TransformNormal(n,rigid);
                            mesh.Normals.Add(new Vector3D(n.X,n.Y,n.Z));
                        }
                        if (uvType == VertexComponentType.Float2)
                        { var uv=data.GetVector2(i,(int)VertexSemantics.TexCoord0); mesh.TextureCoordinates.Add(new Point(uv.X,uv.Y)); }
                        else if (uvType == VertexComponentType.Half2)
                        { var uv=data.GetHalf2(i,(int)VertexSemantics.TexCoord0); mesh.TextureCoordinates.Add(new Point((float)uv.X,(float)uv.Y)); }
                    }
                    for(int i=0;i+2<indices.Length;i+=3)
                        if (indices[i]<data.VertexCount && indices[i+1]<data.VertexCount && indices[i+2]<data.VertexCount)
                        { mesh.TriangleIndices.Add(indices[i]); mesh.TriangleIndices.Add(indices[i+1]); mesh.TriangleIndices.Add(indices[i+2]); }
                    mesh.Freeze();
                    TextureBase diffuse = null;
                    var parameters = geometry.Shader?.ParametersList;
                    if (parameters?.Parameters != null)
                        for(int i=0;i<parameters.Parameters.Length;i++)
                            if (parameters.Parameters[i].Data is TextureBase texture && parameters.Hashes != null && i<parameters.Hashes.Length && (uint)parameters.Hashes[i] == (uint)ShaderParamNames.DiffuseSampler)
                            { diffuse=texture; break; }
                    var actual = diffuse as Texture;
                    if (actual?.Data == null && diffuse != null)
                    {
                        actual = drawable.ShaderGroup?.TextureDictionary?.Lookup(diffuse.NameHash) ?? textures?.Lookup(diffuse.NameHash);
                        if (!body) foreach(var dictionary in apparelTextures) { actual = actual ?? dictionary.Lookup(diffuse.NameHash); }
                    }
                    BitmapSource bitmap = null;
                    string name = diffuse?.Name ?? "";
                    if (!edits.TryGetValue(name,out bitmap) && actual?.Data != null)
                    {
                        try
                        {
                            int mip=0; while(mip+1<actual.Levels && Math.Max(actual.Width>>mip,actual.Height>>mip)>2048) mip++;
                            int width=Math.Max(1,actual.Width>>mip),height=Math.Max(1,actual.Height>>mip);
                            var pixels=DDSIO.GetPixels(actual,mip);
                            if(pixels!=null && pixels.Length>=width*height*4)
                            { bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);bitmap.Freeze(); }
                        }
                        catch (Exception error) when(error is InvalidDataException || error is ArgumentException || error is NotSupportedException) { }
                    }
                    var brush = new ImageBrush { TileMode=TileMode.Tile, ViewportUnits=BrushMappingMode.Absolute, Viewport=new Rect(0,0,1,1), Stretch=Stretch.Fill };
                    var material = new DiffuseMaterial(bitmap == null ? (Brush)new SolidColorBrush(body ? Color.FromRgb(181,161,148) : Color.FromRgb(171,181,195)) : brush);
                    var surface = new Surface { Name=name, Original=bitmap, Brush=brush, Material=material, Component=component, IsBody=body };
                    surfaces.Add(surface); UpdateSurface(surface);
                    group.Children.Add(new GeometryModel3D(mesh,material) { BackMaterial=material }); MeshCount++;
                }
        }

        void UpdateSurface(Surface surface)
        {
            if (surface.Original == null) return;
            surface.Material.Brush = surface.Brush;
            int component = tattooZone == "ZONE_HEAD" ? 0 : tattooZone == "ZONE_LEFT_LEG" || tattooZone == "ZONE_RIGHT_LEG" ? 4 : 3;
            if (tattoo == null || !surface.IsBody || surface.Component != component)
            { surface.Brush.ImageSource=surface.Original; return; }
            int width=surface.Original.PixelWidth,height=surface.Original.PixelHeight;
            var visual=new DrawingVisual();
            using(var drawing=visual.RenderOpen())
            {
                drawing.DrawImage(surface.Original,new Rect(0,0,width,height));
                double cx=tattooU*width,cy=tattooV*height;
                drawing.PushTransform(new RotateTransform(tattooRotation,cx,cy));
                double w=Math.Max(.001,tattooScaleX)*width,h=Math.Max(.001,tattooScaleY)*height;
                drawing.DrawImage(tattoo,new Rect(cx-w/2,cy-h/2,w,h));drawing.Pop();
            }
            var rendered=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);rendered.Render(visual);rendered.Freeze();surface.Brush.ImageSource=rendered;
        }
        sealed class Surface
        {
            public string Name;
            public BitmapSource Original;
            public ImageBrush Brush;
            public DiffuseMaterial Material;
            public int Component;
            public bool IsBody;
        }
    }
}
