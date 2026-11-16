param([string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Seam.exe'))
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this UI check in an STA PowerShell process.' }
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml
$probe = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

public static class CollectionLoadProbe
{
    private static int count;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        count++; Console.WriteLine("OK " + message);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    public static void Run(string executable)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var assembly = Assembly.LoadFrom(executable);
        var app = (Application)Activator.CreateInstance(assembly.GetType("Seam.App", true));
        app.GetType().GetMethod("InitializeComponent").Invoke(app, null);
        var window = (Window)Activator.CreateInstance(assembly.GetType("Seam.MainWindow", true));
        app.MainWindow = window;
        window.ShowActivated = false;
        try
        {
            window.Show(); Pump();
            var workspace = window.GetType().GetField("workspace", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            // Measure import/loading behavior without paying for a full 3D scene build.
            workspace.GetType().GetProperty("BuildPreview").SetValue(workspace, false, null);

            string fixture = Path.Combine(Path.GetTempPath(), "Seam-ui-load-" + Guid.NewGuid().ToString("N"));
            var demo = (string[])assembly.GetType("Seam.MergeWorkspace")
                .GetMethod("CreateDemo", BindingFlags.Public | BindingFlags.Static)
                .Invoke(null, new object[] { Path.Combine(fixture, "demo") });
            string collection = Path.Combine(fixture, "[mlos load]");
            string category = Path.Combine(collection, "[category]");
            Directory.CreateDirectory(category);
            const int copies = 10;
            for (int resource = 0; resource < demo.Length; resource++)
                for (int copy = 0; copy < copies; copy++)
                    CopyDirectory(demo[resource], Path.Combine(category, Path.GetFileName(demo[resource]) + "-" + copy));
            int expected = demo.Length * copies;

            var import = window.GetType().GetMethod("Import", BindingFlags.NonPublic | BindingFlags.Instance);
            var progress = (ProgressBar)window.FindName("BusyProgress");
            var overlay = (Border)window.FindName("BusyOverlay");
            var watch = Stopwatch.StartNew();
            var task = (System.Threading.Tasks.Task)import.Invoke(window, new object[] { new[] { collection } });
            Check(overlay.Visibility == Visibility.Visible, "loading overlay appears before collection loading finishes");
            var seen = new HashSet<int>();
            int pumps = 0;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                seen.Add((int)progress.Value);
                Pump(); pumps++;
                Thread.Sleep(2);
            }
            if (!task.IsCompleted) throw new TimeoutException("Collection import did not finish.");
            task.GetAwaiter().GetResult(); Pump(); watch.Stop();
            seen.Add((int)progress.Value);
            Console.WriteLine("  .. " + expected + " resources imported in " + watch.ElapsedMilliseconds + " ms; sampled " + seen.Count + " progress values across " + pumps + " UI pumps");
            Check(seen.Count >= 2, "progress changes while the dispatcher keeps pumping");
            Check((int)progress.Maximum == expected && progress.Value == expected, "progress completes for every resource");
            Check(overlay.Visibility == Visibility.Collapsed, "loading overlay closes when import finishes");

            var packs = ((System.Collections.ICollection)workspace.GetType().GetProperty("Packs").GetValue(workspace, null)).Count;
            Check(packs == expected, "all collection resources load");
            var status = ((TextBlock)window.FindName("StatusText")).Text;
            Check(status.StartsWith("Imported " + expected + " resources", StringComparison.Ordinal), "status reports every imported resource");
            var plan = workspace.GetType().GetProperty("LastAutomaticMerge").GetValue(workspace, null);
            Check(plan != null && (int)plan.GetType().GetProperty("ResourceCount").GetValue(plan, null) == expected, "auto merge covers every resource");
        }
        finally
        {
            window.GetType().GetField("dirty", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, false);
            window.Close(); app.Shutdown();
        }
        Console.WriteLine("PASS: " + count + " collection loading assertions");
    }
    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), false);
        foreach (var child in Directory.GetDirectories(source)) CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
    }
}
'@
Add-Type -TypeDefinition $probe -ReferencedAssemblies @([Windows.Application].Assembly.Location, [Windows.Media.Visual].Assembly.Location, [Windows.Threading.Dispatcher].Assembly.Location, [Windows.Markup.IXamlTypeResolver].Assembly.Location, 'System.dll')
$executable = (Resolve-Path -LiteralPath $ExePath).Path
[CollectionLoadProbe]::Run($executable)
