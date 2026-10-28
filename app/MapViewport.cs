using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Seam
{
    public sealed class SceneSelectionEventArgs : EventArgs
    {
        public SceneItem Item { get; }
        public SceneSelectionEventArgs(SceneItem item) { Item = item; }
    }

    public sealed class MapViewport : Grid
    {
        private readonly Viewport3D viewport = new Viewport3D();
        private readonly PerspectiveCamera camera = new PerspectiveCamera { FieldOfView = 48, NearPlaneDistance = 0.05, FarPlaneDistance = 1000000, UpDirection = new Vector3D(0, 0, 1) };
        private readonly ModelVisual3D content = new ModelVisual3D();
        private readonly ModelVisual3D grid = new ModelVisual3D();
        private readonly ModelVisual3D selection = new ModelVisual3D();
        private readonly Dictionary<Visual3D, SceneItem> hitItems = new Dictionary<Visual3D, SceneItem>();
        private List<SceneItem> items = new List<SceneItem>();
        private Point3D target = new Point3D(0, 0, 0);
        private double distance = 70, azimuth = -1.05, elevation = 0.65;
        private Point lastPoint, pressPoint;
        private bool dragging, pan, collisions = true;
        private SceneItem selected;
        public event EventHandler<SceneSelectionEventArgs> ItemSelected;

        public MapViewport()
        {
            Background = new SolidColorBrush(Color.FromRgb(19, 22, 28));
            ClipToBounds = true;
            Focusable = true;
            viewport.Camera = camera;
            viewport.Children.Add(grid);
            viewport.Children.Add(content);
            viewport.Children.Add(selection);
            Children.Add(viewport);
            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseUp += OnUp;
            LostMouseCapture += (s, e) => dragging = false;
            MouseWheel += (s, e) => { distance = Math.Max(0.1, Math.Min(500000, distance * Math.Exp(-e.Delta / 1000.0))); UpdateCamera(); e.Handled = true; };
            BuildGrid(new Box3 { MinX = -25, MaxX = 25, MinY = -25, MaxY = 25, MinZ = 0, MaxZ = 0 });
            UpdateCamera();
        }

        public void SetScene(IEnumerable<SceneItem> scene, bool frame = false)
        {
            var next = scene.Where(s => !s.Excluded).ToList();
            if (items.SequenceEqual(next))
            {
                if (frame || items.Count == 0) FrameAll();
                else DrawSelection();
                return;
            }
            items = next;
            selected = selected == null ? null : items.FirstOrDefault(i => i.Id == selected.Id);
            Rebuild();
            if (frame || items.Count == 0) FrameAll();
            else DrawSelection();
        }

        public bool ShowCollisions
        {
            get => collisions;
            set { if (collisions == value) return; collisions = value; Rebuild(); }
        }

        private void Rebuild()
        {
            var group = new Model3DGroup();
            group.Children.Add(new AmbientLight(Color.FromRgb(140, 146, 143)));
            group.Children.Add(new DirectionalLight(Color.FromRgb(205, 214, 207), new Vector3D(-0.3, 0.5, -1)));
            group.Children.Add(new DirectionalLight(Color.FromRgb(100, 117, 121), new Vector3D(0.7, -0.5, -0.3)));
            hitItems.Clear();
            content.Children.Clear();
            foreach (var item in items.Where(i => !i.IsCollision || collisions))
            {
                if (item.Model == null) continue;
                Model3D model = item.Model;
                if (item.IsCollision)
                {
                    var clone = item.Model.Clone();
                    PaintCollision(clone, Math.Max(0.005, (item.Bounds?.Size ?? 5) * 0.00035));
                    model = clone;
                }
                var visual = new ModelVisual3D { Content = model };
                content.Children.Add(visual);
                hitItems[visual] = item;
            }
            content.Content = group;
            DrawSelection();
        }

        private static void PaintCollision(Model3DGroup group, double width)
        {
            foreach (var model in group.Children.ToArray())
            {
                if (model is GeometryModel3D geometry)
                {
                    geometry.Material = null;
                    geometry.BackMaterial = null;
                    if (geometry.Geometry is MeshGeometry3D source)
                    {
                        // ponytail: sample dense CPU wire meshes; use GPU lines if every edge is needed.
                        int stride = Math.Max(1, (int)Math.Ceiling(source.TriangleIndices.Count / 12000.0));
                        var wire = new MeshGeometry3D();
                        var seen = new HashSet<long>();
                        for (int triangle = 0; triangle + 2 < source.TriangleIndices.Count; triangle += 3 * stride)
                        {
                            for (int side = 0; side < 3; side++)
                            {
                                int a = source.TriangleIndices[triangle + side], b = source.TriangleIndices[triangle + (side+1)%3];
                                long key = ((long)Math.Min(a,b) << 32) | (uint)Math.Max(a,b);
                                if (a < 0 || b < 0 || a >= source.Positions.Count || b >= source.Positions.Count || !seen.Add(key)) continue;
                                AddRod(wire, source.Positions[a], source.Positions[b], width);
                            }
                        }
                        var edge = new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(193, 143, 97)));
                        group.Children.Add(new GeometryModel3D(wire, edge) { BackMaterial = edge, Transform = geometry.Transform });
                    }
                }
                else if (model is Model3DGroup child) PaintCollision(child, width);
            }
        }

        public void FrameAll()
        {
            var bounds = items.Where(i => i.Bounds != null && (!i.IsCollision || collisions)).Select(i => i.Bounds).ToArray();
            if (bounds.Length == 0) { target = new Point3D(0, 0, 0); distance = 70; UpdateCamera(); return; }
            var box = new Box3 { MinX = bounds.Min(b => b.MinX), MinY = bounds.Min(b => b.MinY), MinZ = bounds.Min(b => b.MinZ), MaxX = bounds.Max(b => b.MaxX), MaxY = bounds.Max(b => b.MaxY), MaxZ = bounds.Max(b => b.MaxZ) };
            Frame(box);
            BuildGrid(box);
        }

        public void Frame(Box3 bounds)
        {
            if (bounds == null) return;
            target = new Point3D(bounds.X, bounds.Y, bounds.Z);
            distance = Math.Max(5, bounds.Size * 2.3);
            UpdateCamera();
        }

        public void Select(string id)
        {
            selected = items.FirstOrDefault(i => i.Id == id);
            DrawSelection();
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right && e.ChangedButton != MouseButton.Middle) return;
            Focus();
            pressPoint = lastPoint = e.GetPosition(this);
            pan = e.ChangedButton != MouseButton.Left || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            dragging = true;
            CaptureMouse();
            Cursor = pan ? Cursors.ScrollAll : Cursors.SizeAll;
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            Point current = e.GetPosition(this);
            double dx = current.X - lastPoint.X, dy = current.Y - lastPoint.Y;
            lastPoint = current;
            if (pan)
            {
                Vector3D view = target - camera.Position;
                view.Normalize();
                Vector3D right = Vector3D.CrossProduct(view, camera.UpDirection); right.Normalize();
                Vector3D up = Vector3D.CrossProduct(right, view); up.Normalize();
                target += right * (-dx * distance / Math.Max(200, ActualHeight) * 0.85) + up * (dy * distance / Math.Max(200, ActualHeight) * 0.85);
            }
            else { azimuth -= dx * 0.008; elevation = Math.Max(0.02, Math.Min(Math.PI / 2 - 0.015, elevation + dy * 0.007)); }
            UpdateCamera();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!dragging) return;
            bool click = !pan && (e.GetPosition(this) - pressPoint).Length < 5;
            dragging = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            if (click)
            {
                SceneItem hit = null;
                VisualTreeHelper.HitTest(viewport, null, result =>
                {
                    if (result is RayMeshGeometry3DHitTestResult mesh && hitItems.TryGetValue(mesh.VisualHit, out var item)) { hit = item; return HitTestResultBehavior.Stop; }
                    return HitTestResultBehavior.Continue;
                }, new PointHitTestParameters(e.GetPosition(viewport)));
                selected = hit;
                DrawSelection();
                ItemSelected?.Invoke(this, new SceneSelectionEventArgs(hit));
            }
            e.Handled = true;
        }

        private void UpdateCamera()
        {
            Vector3D offset = new Vector3D(Math.Cos(azimuth) * Math.Cos(elevation), Math.Sin(azimuth) * Math.Cos(elevation), Math.Sin(elevation)) * distance;
            camera.Position = target + offset;
            camera.LookDirection = -offset;
            camera.NearPlaneDistance = Math.Max(0.02, distance / 150);
            camera.FarPlaneDistance = Math.Max(10000, distance * 80);
        }

        private void DrawSelection()
        {
            if (selected?.Bounds == null) { selection.Content = null; return; }
            var b = selected.Bounds;
            double p = Math.Max(0.03, b.Size * 0.006);
            Point3D[] points = {
                new Point3D(b.MinX-p,b.MinY-p,b.MinZ-p),new Point3D(b.MaxX+p,b.MinY-p,b.MinZ-p),
                new Point3D(b.MaxX+p,b.MaxY+p,b.MinZ-p),new Point3D(b.MinX-p,b.MaxY+p,b.MinZ-p),
                new Point3D(b.MinX-p,b.MinY-p,b.MaxZ+p),new Point3D(b.MaxX+p,b.MinY-p,b.MaxZ+p),
                new Point3D(b.MaxX+p,b.MaxY+p,b.MaxZ+p),new Point3D(b.MinX-p,b.MaxY+p,b.MaxZ+p)
            };
            var mesh = new MeshGeometry3D();
            for (int i = 0; i < 4; i++) { AddRod(mesh, points[i], points[(i + 1) % 4], p / 2); AddRod(mesh, points[i+4], points[(i + 1) % 4+4], p / 2); AddRod(mesh, points[i], points[i + 4], p / 2); }
            var material = new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(158, 184, 255)));
            selection.Content = new GeometryModel3D(mesh, material) { BackMaterial = material };
        }

        private void BuildGrid(Box3 box)
        {
            double span = Math.Max(50, box.Size * 1.6);
            double step = Math.Pow(10, Math.Floor(Math.Log10(span / 12)));
            if (span / step > 30) step *= 5;
            else if (span / step > 18) step *= 2;
            double x = Math.Round(box.X / step) * step, y = Math.Round(box.Y / step) * step;
            double z = box.MinZ - Math.Max(0.05, box.Size * 0.002);
            double extent = step * 10, line = Math.Max(0.008, span / 6000);
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
            var lines = new GeometryGroup();
            lines.Children.Add(new LineGeometry(new Point(0.5,0), new Point(0.5,1)));
            lines.Children.Add(new LineGeometry(new Point(0,0.5), new Point(1,0.5)));
            drawing.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(42,49,64)), 0.012), lines));
            var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0,0,1,1), Stretch = Stretch.Fill };
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection {new Point3D(x-extent,y-extent,z),new Point3D(x+extent,y-extent,z),new Point3D(x+extent,y+extent,z),new Point3D(x-extent,y+extent,z)},
                TriangleIndices = new Int32Collection {0,1,2,0,2,3},
                TextureCoordinates = new PointCollection {new Point(0,0),new Point(20,0),new Point(20,20),new Point(0,20)}
            };
            var material = new EmissiveMaterial(brush);
            var group = new Model3DGroup();
            group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
            var xAxis = new MeshGeometry3D(); var yAxis = new MeshGeometry3D();
            AddRod(xAxis, new Point3D(x-extent,y,z),new Point3D(x+extent,y,z),line*1.5);
            AddRod(yAxis, new Point3D(x,y-extent,z),new Point3D(x,y+extent,z),line*1.5);
            group.Children.Add(new GeometryModel3D(xAxis, new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(92,65,58)))));
            group.Children.Add(new GeometryModel3D(yAxis, new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(57,85,68)))));
            group.Freeze(); grid.Content = group;
        }

        private static void AddRod(MeshGeometry3D mesh, Point3D a, Point3D b, double radius)
        {
            Vector3D direction = b - a;
            if (direction.LengthSquared < 1e-20) return;
            direction.Normalize();
            Vector3D side = Vector3D.CrossProduct(direction, Math.Abs(direction.Z) < 0.9 ? new Vector3D(0,0,1) : new Vector3D(0,1,0)); side.Normalize();
            Vector3D up = Vector3D.CrossProduct(direction, side);
            side *= radius; up *= radius;
            int first = mesh.Positions.Count;
            mesh.Positions.Add(a-side-up); mesh.Positions.Add(a+side-up); mesh.Positions.Add(a+side+up); mesh.Positions.Add(a-side+up);
            mesh.Positions.Add(b-side-up); mesh.Positions.Add(b+side-up); mesh.Positions.Add(b+side+up); mesh.Positions.Add(b-side+up);
            int[] indices = {0,1,2,0,2,3,4,6,5,4,7,6,0,4,5,0,5,1,1,5,6,1,6,2,2,6,7,2,7,3,3,7,4,3,4,0};
            foreach (int index in indices) mesh.TriangleIndices.Add(first + index);
        }
    }
}
