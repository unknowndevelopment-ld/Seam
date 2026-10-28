using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using CodeWalker.GameFiles;

namespace Seam
{
    public sealed class MapPack
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        public string Root { get; set; }
        public string Color { get; set; }
        public bool Enabled { get; set; } = true;
        public List<MapAsset> Assets { get; } = new List<MapAsset>();
        public int EntityCount => Assets.Sum(a => a.Entities.Count);
        public string Summary => $"{EntityCount:N0} entities · {Assets.Count:N0} files";
    }

    public sealed class MapAsset
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public MapPack Pack { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public string Extension { get; set; }
        public string Digest { get; set; }
        public string Error { get; set; }
        public bool Excluded { get; set; }
        public YmapFile Ymap { get; set; }
        public YbnFile Ybn { get; set; }
        public YtypFile Ytyp { get; set; }
        public List<MapEntity> Entities { get; } = new List<MapEntity>();
        public List<CollisionPiece> Collisions { get; } = new List<CollisionPiece>();
    }

    public sealed class MapEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public MapAsset Asset { get; set; }
        public int Index { get; set; }
        public YmapEntityDef Entity { get; set; }
        public bool Excluded { get; set; }
        public string Label { get; set; }
        public Box3 Bounds { get; set; }
    }

    public sealed class CollisionPiece
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public MapAsset Asset { get; set; }
        public int Index { get; set; }
        public Bounds Bound { get; set; }
        public Box3 Bounds { get; set; }
        public bool Excluded { get; set; }
        public string Label => Asset.Name + " · bound " + Index;
    }

    public sealed class Box3
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public bool Estimated;
        public double X => (MinX + MaxX) / 2;
        public double Y => (MinY + MaxY) / 2;
        public double Z => (MinZ + MaxZ) / 2;
        public double Size => Math.Max(MaxX - MinX, Math.Max(MaxY - MinY, MaxZ - MinZ));
        public bool Overlaps(Box3 b, double inset = 0.02) =>
            Math.Min(MaxX, b.MaxX) - Math.Max(MinX, b.MinX) > inset &&
            Math.Min(MaxY, b.MaxY) - Math.Max(MinY, b.MinY) > inset &&
            Math.Min(MaxZ, b.MaxZ) - Math.Max(MinZ, b.MinZ) > inset;
        public bool Touches(Box3 b, double tolerance = 0.002) =>
            MaxX + tolerance >= b.MinX && b.MaxX + tolerance >= MinX &&
            MaxY + tolerance >= b.MinY && b.MaxY + tolerance >= MinY &&
            MaxZ + tolerance >= b.MinZ && b.MaxZ + tolerance >= MinZ;
    }

    public sealed class MergeIssue
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public string LeftLabel { get; set; }
        public string RightLabel { get; set; }
        public string LeftId { get; set; }
        public string RightId { get; set; }
        public Box3 Bounds { get; set; }
        public bool CanAutoFix { get; set; }
        public bool Resolved { get; set; }
        public string Resolution { get; set; }
        public string Status => Resolved ? Resolution : Kind;
    }

    public sealed class SceneItem
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string PackId { get; set; }
        public string Color { get; set; }
        public bool IsCollision { get; set; }
        public bool IsProxy { get; set; }
        public bool Excluded { get; set; }
        public Box3 Bounds { get; set; }
        // Frozen on the loading thread, safe to attach to the UI thread.
        public Model3DGroup Model { get; set; }
    }

    public sealed class ExportResult
    {
        public string Path { get; set; }
        public int Files { get; set; }
        public int RemovedEntities { get; set; }
        public int RemovedBounds { get; set; }
        public List<string> ResourcePaths { get; } = new List<string>();
    }

    // Engine API consumed by the desktop shell. First pack has highest priority.
    public sealed partial class MergeWorkspace
    {
        public List<MapPack> Packs { get; } = new List<MapPack>();
        public List<MergeIssue> Issues { get; } = new List<MergeIssue>();
        public List<SceneItem> Scene { get; } = new List<SceneItem>();
        public List<string> Notices { get; } = new List<string>();
        public int EntityCount => GetEffectiveAssets().Sum(a => a.Entities.Count(e => !e.Excluded));
        public int ActiveIssueCount => Issues.Count(i => !i.Resolved);
    }
}
