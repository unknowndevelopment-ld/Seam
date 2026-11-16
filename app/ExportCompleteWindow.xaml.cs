using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Seam
{
    public partial class ExportCompleteWindow : Window
    {
        private readonly ExportResult result;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public ExportCompleteWindow(ExportResult result)
        {
            this.result = result ?? throw new ArgumentNullException(nameof(result));
            InitializeComponent();
            SummaryText.Text = $"{result.ResourcePaths.Count:N0} separate resource{(result.ResourcePaths.Count == 1 ? "" : "s")} · {result.Files:N0} files written to {result.Path}";
            ResourceList.ItemsSource = result.ResourcePaths;
            SourceInitialized += (s, e) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                FitToWorkArea(handle);
                try { int dark = 1; DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            };
            Loaded += (s, e) => CloseButton.Focus();
        }

        private void FitToWorkArea(IntPtr handle)
        {
            // The summary can contain dozens of long paths. Keep the window inside the
            // owner's monitor so the footer buttons and the caption stay reachable.
            var source = HwndSource.FromHwnd(handle);
            if (source?.CompositionTarget == null) return;
            var ownerHandle = Owner != null ? new WindowInteropHelper(Owner).Handle : IntPtr.Zero;
            var screen = Forms.Screen.FromHandle(ownerHandle != IntPtr.Zero ? ownerHandle : handle);
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            var pixels = screen.WorkingArea;
            var topLeft = fromDevice.Transform(new Point(pixels.Left, pixels.Top));
            var bottomRight = fromDevice.Transform(new Point(pixels.Right, pixels.Bottom));
            var work = new Rect(topLeft, bottomRight);
            const double margin = 24;
            double availableWidth = Math.Max(1, work.Width - margin * 2);
            double availableHeight = Math.Max(1, work.Height - margin * 2);
            MinWidth = Math.Min(MinWidth, availableWidth);
            MinHeight = Math.Min(MinHeight, availableHeight);
            Width = Math.Min(Width, availableWidth);
            Height = Math.Min(Height, availableHeight);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(string.Join("\r\n", result.ResourcePaths)); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Seam — could not copy paths", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true });
                Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Seam — could not open output folder", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
    }
}
