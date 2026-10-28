param([string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Seam.exe'))
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this UI check in an STA PowerShell process.' }
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml
$probe = @'
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static class AutomaticUiProbe
{
    private static int count;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        count++; Console.WriteLine("OK " + message);
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Wait(Func<bool> ready, string operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do { Pump(); if (ready()) return; Thread.Sleep(20); } while (DateTime.UtcNow < deadline);
        throw new TimeoutException(operation + " did not finish in twenty seconds.");
    }
    public static void Run(string executable, string screenshot)
    {
        count = 0;
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
            Check(window.FindName("KeepA") == null && window.FindName("KeepB") == null && window.FindName("KeepBoth") == null,
                "automatic workflow has no keep/remove conflict controls");
            Check(window.FindName("ExcludeItemButton") == null && window.FindName("ExcludeFileButton") == null,
                "source inspection has no object/file exclusion actions");
            var demo = FindButton(window, "Try the demo");
            Check(demo != null, "demo action is present in the automatic interface");
            demo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var mapCount = (TextBlock)window.FindName("MapCount");
            var autoMerge = (Button)window.FindName("AutoMergeButton");
            Wait(() => mapCount.Text == "2" && autoMerge.IsEnabled, "Demo import");
            Check(mapCount.Text == "2", "actual async demo handler imports both source maps");
            var viewport = window.FindName("Viewport");
            var sceneVisual = (System.Windows.Media.Media3D.ModelVisual3D)viewport.GetType().GetField("content", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(viewport);
            var firstGeometry = sceneVisual.Children[0];
            autoMerge.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var state = (TextBlock)window.FindName("MergeState");
            var outputCount = (TextBlock)window.FindName("OutputCount");
            Wait(() => state.Text == "Merge complete" && autoMerge.IsEnabled, "Automatic merge");
            Check(outputCount.Text == "2", "actual AutoMerge_Click prepares two separate resource copies");
            Check(ReferenceEquals(firstGeometry, sceneVisual.Children[0]), "status-only refresh reuses existing 3D visuals instead of rebuilding geometry");
            var results = (ListBox)window.FindName("ResultList");
            Check(results.Items.Count > 0, "automatic operations populate the read-only results panel");
            Check(((Button)window.FindName("ExportButton")).IsEnabled, "automatic merge enables resource export");
            results.SelectedIndex = 0; Pump();
            int posted = 0;
            DispatcherHookEventHandler onPosted = (sender, args) => posted++;
            var hooks = Dispatcher.CurrentDispatcher.Hooks;
            var progress = window.GetType().GetMethod("Progress", BindingFlags.NonPublic | BindingFlags.Instance);
            var busy = window.GetType().GetField("busy", BindingFlags.NonPublic | BindingFlags.Instance);
            var status = (TextBlock)window.FindName("StatusText");
            string previousStatus = status.Text;
            busy.SetValue(window, true);
            hooks.OperationPosted += onPosted;
            try {
                for (int i = 0; i < 10000; i++) progress.Invoke(window, new object[] { "Progress " + i });
                hooks.OperationPosted -= onPosted;
                Pump();
                Check(posted <= 2 && ((TextBlock)window.FindName("BusyText")).Text == "Progress 9999", "ten thousand progress updates queue at most two UI callbacks and retain the latest message");
            } finally { hooks.OperationPosted -= onPosted; busy.SetValue(window, false); status.Text = previousStatus; }
            Check(((Border)window.FindName("ResultDetailCard")).Visibility == Visibility.Visible,
                "selecting an operation displays its explanation without a conflict decision");
            var search = (TextBox)window.FindName("Search");
            search.Text = "no-such-map-7e8e3"; Pump();
            Check(results.Items.Count == 0 && ((Border)window.FindName("ResultDetailCard")).Visibility == Visibility.Collapsed,
                "searching for a missing asset clears stale operation details");
            search.Text = ""; Pump();
            Check(results.Items.Count > 0 && ((FrameworkElement)window.FindName("SourcesEmpty")).Visibility == Visibility.Collapsed,
                "clearing activity search restores results and loaded sources hide the empty state");
            results.SelectedIndex = 0; Pump();
            Check(window.FindName("MinimizeButton") != null && window.FindName("MaximizeButton") != null && window.FindName("CloseButton") != null,
                "native window controls remain available");
            window.UpdateLayout(); Pump();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot));
            using (var stream = File.Create(screenshot)) encoder.Save(stream);
            Console.WriteLine("Screenshot: " + screenshot);
        }
        finally
        {
            // This window is owned by the check. Clear only its unsaved-demo flag before closing.
            window.GetType().GetField("dirty", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, false);
            window.Close(); app.Shutdown();
        }
        Console.WriteLine("PASS: " + count + " automatic UI assertions");
    }
    private static Button FindButton(DependencyObject node, string content)
    {
        var button = node as Button;
        if (button != null && Equals(button.Content, content)) return button;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = FindButton(VisualTreeHelper.GetChild(node, i), content);
            if (found != null) return found;
        }
        return null;
    }
}
'@
$references = @([Windows.Application].Assembly.Location, [Windows.Media.Visual].Assembly.Location, [Windows.Threading.Dispatcher].Assembly.Location, [Windows.Markup.IXamlTypeResolver].Assembly.Location, 'System.dll')
Add-Type -TypeDefinition $probe -ReferencedAssemblies $references
$executable = (Resolve-Path -LiteralPath $ExePath).Path
$screenshot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\seam-auto-preview.png'))
[AutomaticUiProbe]::Run($executable, $screenshot)
