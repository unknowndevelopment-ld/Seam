param([string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Seam.exe'))
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this UI check in an STA PowerShell process.' }
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml,System.Windows.Forms
$probe = @'
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

public static class OptimizerUiProbe
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
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do { Pump(); if (ready()) return; Thread.Sleep(20); } while (DateTime.UtcNow < deadline);
        throw new TimeoutException(operation + " did not finish in thirty seconds.");
    }
    private static bool Inside(FrameworkElement element, Window window)
    {
        var origin = element.TransformToAncestor(window).Transform(new Point(0, 0));
        return origin.X >= -0.5 && origin.Y >= -0.5 &&
            origin.X + element.ActualWidth <= window.ActualWidth + 0.5 &&
            origin.Y + element.ActualHeight <= window.ActualHeight + 0.5;
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
        Window dialog = null;
        try
        {
            window.Show(); Pump();
            var optimize = (Button)window.FindName("OptimizeButton");
            Check(optimize != null && !optimize.IsEnabled, "map optimizer action exists in the action bar");

            var demo = FindButton(window, "Try the demo");
            demo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var mapCount = (TextBlock)window.FindName("MapCount");
            Wait(() => mapCount.Text == "2" && optimize.IsEnabled, "Demo import for optimizer");
            Check(optimize.IsEnabled, "map optimizer action enables once sources are loaded");

            var workspace = window.GetType().GetField("workspace", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
            var options = workspace.GetType().GetProperty("Optimizer").GetValue(workspace, null);
            var dialogType = assembly.GetType("Seam.MapOptimizerWindow", true);
            dialog = (Window)Activator.CreateInstance(dialogType, new object[] { options });
            dialog.Owner = window;
            dialog.ShowActivated = false;
            dialog.Show(); Pump();

            var enabled = (CheckBox)dialog.FindName("EnabledBox");
            var duplicates = (CheckBox)dialog.FindName("DuplicatesBox");
            var empty = (CheckBox)dialog.FindName("EmptyBox");
            var distance = (CheckBox)dialog.FindName("DrawDistanceBox");
            var distanceText = (TextBox)dialog.FindName("DrawDistanceText");
            var validation = (TextBlock)dialog.FindName("ValidationText");
            var merge = (Button)dialog.FindName("MergeButton");
            var save = (Button)dialog.FindName("SaveButton");
            Check(enabled != null && duplicates != null && empty != null && distance != null && distanceText != null && merge != null && save != null,
                "optimizer exposes its three rules, distance field and actions");
            Check(enabled.IsChecked == false && duplicates.IsChecked == true && empty.IsChecked == true && distance.IsChecked == false,
                "optimizer starts off with safe defaults selected");
            Check(!distanceText.IsEnabled, "draw distance stays disabled until its rule is selected");
            Check(merge.IsVisible && Inside(merge, dialog) && save.IsVisible && Inside(save, dialog),
                "optimizer actions stay reachable inside the dialog");
            Check(dialog.ActualHeight < 760 && dialog.ActualWidth < 760, "optimizer keeps a compact single-column layout");

            enabled.IsChecked = true;
            enabled.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Check(duplicates.IsEnabled && empty.IsEnabled && distance.IsEnabled, "enabling the optimizer unlocks its rules");
            distance.IsChecked = true;
            distance.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Check(distanceText.IsEnabled, "draw distance field unlocks with its rule");

            var tryRead = dialogType.GetMethod("TryRead", BindingFlags.NonPublic | BindingFlags.Instance);
            distanceText.Text = "0";
            var invalid = new object[] { null };
            Check((bool)tryRead.Invoke(dialog, invalid) == false && validation.Text.Length > 0,
                "optimizer rejects an out-of-range draw distance");
            distanceText.Text = "250";
            var valid = new object[] { null };
            Check((bool)tryRead.Invoke(dialog, valid), "optimizer accepts a valid draw distance");
            var read = valid[0];
            Check((bool)read.GetType().GetProperty("Enabled").GetValue(read, null) &&
                  (bool)read.GetType().GetProperty("LimitDrawDistance").GetValue(read, null) &&
                  Math.Abs((float)read.GetType().GetProperty("MaxDrawDistance").GetValue(read, null) - 250f) < 0.001f,
                "optimizer reads the selected rules and distance");

            duplicates.IsChecked = false; empty.IsChecked = false; distance.IsChecked = false;
            var none = new object[] { null };
            Check((bool)tryRead.Invoke(dialog, none) == false && validation.Text.Contains("at least one"),
                "optimizer requires a rule while it is enabled");

            distance.IsChecked = true; distanceText.Text = "250"; duplicates.IsChecked = true;
            dialog.UpdateLayout(); Pump();
            var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(dialog);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot));
            using (var stream = File.Create(screenshot)) encoder.Save(stream);
            Console.WriteLine("Screenshot: " + screenshot);
        }
        finally
        {
            if (dialog != null) dialog.Close();
            window.GetType().GetField("dirty", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(window, false);
            window.Close(); app.Shutdown();
        }
        Console.WriteLine("PASS: " + count + " map optimizer UI assertions");
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
$screenshot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\seam-optimizer.png'))
[OptimizerUiProbe]::Run($executable, $screenshot)
