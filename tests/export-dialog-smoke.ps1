param([string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Seam.exe'))
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this UI check in an STA PowerShell process.' }
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml,System.Windows.Forms
$probe = @'
using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Reflection;
using System.Threading;

public static class ExportDialogProbe
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
    private static Rect WorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(handle);
        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var pixels = screen.WorkingArea;
        return new Rect(fromDevice.Transform(new Point(pixels.Left, pixels.Top)), fromDevice.Transform(new Point(pixels.Right, pixels.Bottom)));
    }
    private static bool Inside(FrameworkElement element, Window window)
    {
        var origin = element.TransformToAncestor(window).Transform(new Point(0, 0));
        return origin.X >= -0.5 && origin.Y >= -0.5 &&
            origin.X + element.ActualWidth <= window.ActualWidth + 0.5 &&
            origin.Y + element.ActualHeight <= window.ActualHeight + 0.5;
    }
    private static ScrollViewer FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer) return (ScrollViewer)root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
    public static void Run(string executable)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var assembly = Assembly.LoadFrom(executable);
        var app = (Application)Activator.CreateInstance(assembly.GetType("Seam.App", true));
        app.GetType().GetMethod("InitializeComponent").Invoke(app, null);

        var owner = new Window { Width = 900, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual, Left = 120, Top = 80 };
        app.MainWindow = owner;
        owner.ShowActivated = false;
        try
        {
            owner.Show(); Pump();

            var resultType = assembly.GetType("Seam.ExportResult", true);
            var result = Activator.CreateInstance(resultType);
            resultType.GetProperty("Path").SetValue(result, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Seam-export-summary"));
            resultType.GetProperty("Files").SetValue(result, 432);
            var paths = (IList)resultType.GetProperty("ResourcePaths").GetValue(result);
            for (int i = 0; i < 80; i++)
                paths.Add("C:\\Maps\\[mlos] Collection With Long Folder Names\\resource_" + i.ToString("000") + "_merged");

            var dialogType = assembly.GetType("Seam.ExportCompleteWindow", true);
            var dialog = (Window)Activator.CreateInstance(dialogType, new object[] { result });
            dialog.Owner = owner;
            dialog.ShowActivated = false;
            dialog.Show(); Pump();

            var close = (Button)dialog.FindName("CloseButton");
            var open = (Button)dialog.FindName("OpenButton");
            var copy = (Button)dialog.FindName("CopyButton");
            var list = (ListBox)dialog.FindName("ResourceList");
            var summary = (TextBlock)dialog.FindName("SummaryText");
            Check(close != null && open != null && copy != null && list != null && summary != null, "export summary exposes its close, open, copy and path controls");

            var work = WorkArea(dialog);
            Check(dialog.Left >= work.Left - 1 && dialog.Top >= work.Top - 1 &&
                dialog.Left + dialog.ActualWidth <= work.Right + 1 && dialog.Top + dialog.ActualHeight <= work.Bottom + 1,
                "export summary fits inside the monitor work area");
            Check(close.IsVisible && close.IsHitTestVisible && Inside(close, dialog),
                "close control is visible and inside the window with many exported paths");
            Check(list.Items.Count == 80, "export summary lists every exported resource");
            Check(list.Items[0].ToString().EndsWith("resource_000_merged", StringComparison.Ordinal) &&
                list.Items[79].ToString().EndsWith("resource_079_merged", StringComparison.Ordinal),
                "export summary keeps all resource paths in order");
            Check(summary.ActualHeight < 90 && summary.Text.Contains("80 separate resources") && summary.Text.Contains("432 files"),
                "export summary stays compact and reports counts");

            list.UpdateLayout();
            var scroller = FindScrollViewer(list);
            Check(scroller != null && scroller.ScrollableHeight > 0,
                "long path lists scroll instead of stretching the window");

            dialog.Height = dialog.MinHeight; Pump();
            Check(close.IsVisible && Inside(close, dialog) && open.IsVisible && Inside(open, dialog),
                "footer controls remain reachable when the window is resized to its minimum");

            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(!dialog.IsVisible, "close control dismisses the export summary");
        }
        finally
        {
            owner.Close(); app.Shutdown();
        }
        Console.WriteLine("PASS: " + count + " export summary assertions");
    }
}
'@
Add-Type -TypeDefinition $probe -ReferencedAssemblies @(
    [Windows.Application].Assembly.Location,
    [Windows.Media.Visual].Assembly.Location,
    [Windows.Threading.Dispatcher].Assembly.Location,
    [Windows.Markup.IXamlTypeResolver].Assembly.Location,
    [System.Windows.Forms.Screen].Assembly.Location,
    'System.Drawing.dll',
    'System.dll'
)
$executable = (Resolve-Path -LiteralPath $ExePath).Path
[ExportDialogProbe]::Run($executable)
