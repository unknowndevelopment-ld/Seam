param([string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Seam.exe'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml
$probe=@'
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static class StartupProbe
{
    public static void Run(string executable, string screenshot)
    {
        var assembly = Assembly.LoadFrom(executable);
        var app = (Application)Activator.CreateInstance(assembly.GetType("Seam.App", true));
        app.GetType().GetMethod("InitializeComponent").Invoke(app, null);
        Exception failure = null;
        bool shown = false, closed = false, ready = false;
        int passed = 0;
        Action<bool, string> check = (condition, message) => {
            if (!condition) throw new InvalidOperationException(message);
            passed++; Console.WriteLine("OK " + message);
        };
        app.Startup += (sender, args) => app.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            try {
                var loading = app.Windows.Cast<Window>().Single(w => w.GetType().Name == "StartupWindow");
                shown = loading.IsVisible;
                loading.Closed += (s, e) => closed = true;
                loading.UpdateLayout();
                check(shown && !loading.ShowInTaskbar, "startup screen appears without an extra taskbar button");
                check(FindProgress(loading), "startup screen contains an animated indeterminate indicator");
                var bitmap = new RenderTargetBitmap((int)loading.ActualWidth, (int)loading.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(loading);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(screenshot)) encoder.Save(stream);
            } catch (Exception error) { failure = error; app.Shutdown(1); }
        }));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (sender, args) => {
            try {
                var main = app.MainWindow;
                if (main != null && main.GetType().Name == "HubWindow" && main.IsVisible && closed) {
                    timer.Stop();
                    check(shown && closed && main.FindName("MapMergerButton") != null, "startup screen closes automatically when the launcher renders");
                    var maps=(Button)main.FindName("MapMergerButton");
                    var clothes=(Button)main.FindName("ClothingStudioButton");
                    check(maps.IsEnabled && clothes!=null && clothes.IsEnabled,"launcher exposes separate map and clothing workspaces");
                    main.UpdateLayout();
                    var bitmap=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(main);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var stream=File.Create(Path.Combine(Path.GetDirectoryName(screenshot),"seam-hub.png")))encoder.Save(stream);
                    maps.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var mapWindow=app.MainWindow;
                    check(mapWindow.GetType().Name=="MainWindow" && mapWindow.IsVisible && mapWindow.FindName("AutoMergeButton")!=null,"launcher opens the actual map-merger window");
                    mapWindow.Close();
                    check(app.MainWindow==main && main.IsVisible,"closing a tool returns to the launcher");
                    ready = true; main.Close();
                } else if (DateTime.UtcNow >= deadline) throw new TimeoutException("Startup did not finish");
            } catch (Exception error) { timer.Stop(); failure = error; app.Shutdown(1); }
        };
        timer.Start();
        int exit = app.Run();
        timer.Stop();
        if (failure != null) throw failure;
        check(exit == 0 && ready && app.Windows.Count == 0, "closing the launcher exits cleanly with no leftover windows");
        Console.WriteLine("PASS: " + passed + " startup assertions");
    }
    private static bool FindProgress(DependencyObject node)
    {
        var progress = node as ProgressBar;
        if (progress != null) return progress.IsIndeterminate;
        for (int i=0; i<VisualTreeHelper.GetChildrenCount(node); i++) if (FindProgress(VisualTreeHelper.GetChild(node,i))) return true;
        return false;
    }
}
'@
$references=@([Windows.Application].Assembly.Location,[Windows.Media.Visual].Assembly.Location,[Windows.Threading.Dispatcher].Assembly.Location,[Windows.Markup.IXamlTypeResolver].Assembly.Location,'System.dll','System.Core.dll')
Add-Type -TypeDefinition $probe -ReferencedAssemblies $references
[StartupProbe]::Run((Resolve-Path -LiteralPath $ExePath).Path,[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\seam-startup.png')))
