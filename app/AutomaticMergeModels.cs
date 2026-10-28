using System;
using System.Collections.Generic;
using System.Linq;

namespace Seam
{
    public sealed class MergeOperation
    {
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public int Count { get; set; }
    }

    public sealed class AutomaticMergeOutput
    {
        public string Name { get; set; }
        public byte[] Data { get; set; }
        public List<MapAsset> Sources { get; set; } = new List<MapAsset>();
        public string Action { get; set; }
        public string RelativePath { get; set; }
        public string CopySourcePath { get; set; }
    }

    public sealed class AutomaticMergePlan
    {
        public List<AutomaticMergeOutput> Outputs { get; } = new List<AutomaticMergeOutput>();
        public List<MergeOperation> Operations { get; } = new List<MergeOperation>();
        public List<AutomaticMergeOutput> AuxiliaryFiles { get; } = new List<AutomaticMergeOutput>();
        public List<string> ManifestDirectives { get; } = new List<string>();
        public Dictionary<string, string> SourceFiles { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int InputFiles { get; set; }
        public int InputMaps { get; set; }
        public int InputCollisions { get; set; }
        public int MergedMaps { get; set; }
        public int MergedCollisions { get; set; }
        public int DuplicateEntities { get; set; }
        public int DuplicateBounds { get; set; }
        public int DuplicateFiles { get; set; }
        public int RemovedTriangles { get; set; }
        public int RemovedPolygons { get; set; }
        public int MapRepairs { get; set; }
        public int MapChecks { get; set; }
        public string Fingerprint { get; set; }
        public bool PreserveResourceFolders { get; set; }
        public int ResourceCount { get; set; }
        public string Summary => PreserveResourceFolders
            ? $"{InputFiles:N0} source assets · {ResourceCount:N0} separate resources · {MergedMaps:N0} YMAP merges · {MergedCollisions:N0} YBN merges" + RepairSummary
            : $"{InputFiles:N0} source files → {Outputs.Count:N0} output files · {MergedMaps:N0} YMAP merges · {MergedCollisions:N0} YBN merges" + RepairSummary;
        private string RepairSummary => MapRepairs == 0 && MapChecks == 0 ? "" : $" · {MapRepairs:N0} repairs · {MapChecks:N0} checks";
    }

    public sealed partial class MergeWorkspace
    {
        public AutomaticMergePlan LastAutomaticMerge { get; internal set; }
    }
}
