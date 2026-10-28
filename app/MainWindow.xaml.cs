using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Automation;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Seam
{
    public partial class MainWindow : Window
    {
        private MergeWorkspace workspace = new MergeWorkspace();
        private bool busy, rendering, dirty;
        private string projectPath;
        private SceneItem selectedItem;
        private string pendingProgress;
        private int progressQueued;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) =>
            {
                var handle = new WindowInteropHelper(this).Handle;
                FitInitialWindow(handle);
                try { int dark = 1; DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            };
            Closing += (s, e) =>
            {
                if (busy) { e.Cancel = true; return; }
                if (!ConfirmDiscard()) e.Cancel = true;
            };
            SizeChanged += (s, e) => UpdateMaximizedInset();
            RefreshView(true);
            Loaded += async (s, e) =>
            {
                string[] paths = Environment.GetCommandLineArgs().Skip(1).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
                if (paths.Length == 1 && Path.GetExtension(paths[0]).Equals(".seam", StringComparison.OrdinalIgnoreCase)) await OpenProject(paths[0]);
                else if (paths.Length > 0) await Import(paths);
            };
        }

        private void FitInitialWindow(IntPtr handle)
        {
            // The preferred size is in DIPs; at 125–200% scaling it may exceed the
            // monitor's work area and put the entire native caption above the screen.
            var source = HwndSource.FromHwnd(handle);
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            var pixels = Forms.Screen.FromHandle(handle).WorkingArea;
            var topLeft = fromDevice.Transform(new Point(pixels.Left, pixels.Top));
            var bottomRight = fromDevice.Transform(new Point(pixels.Right, pixels.Bottom));
            var work = new Rect(topLeft, bottomRight);
            double availableWidth = Math.Max(1, work.Width - 32), availableHeight = Math.Max(1, work.Height - 32);
            MinWidth = Math.Min(MinWidth, availableWidth);
            MinHeight = Math.Min(MinHeight, availableHeight);
            Width = Math.Min(Width, availableWidth);
            Height = Math.Min(Height, availableHeight);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void Tools_Click(object sender,RoutedEventArgs e) => ((App)Application.Current).ShowLauncher();
        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
        private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (MaximizeButton == null) return;
            bool maximized = WindowState == WindowState.Maximized;
            MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
            AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");
            Dispatcher.BeginInvoke(new Action(UpdateMaximizedInset), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        private void UpdateMaximizedInset()
        {
            if (WindowLayout == null) return;
            if (WindowState != WindowState.Maximized) { WindowLayout.Margin = new Thickness(0); return; }
            var handle = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(handle);
            if (source?.CompositionTarget == null) return;
            var work = Forms.Screen.FromHandle(handle).WorkingArea;
            var first = PointToScreen(new Point(0, 0));
            var last = PointToScreen(new Point(ActualWidth, ActualHeight));
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            // A maximized HWND retains an invisible resize border outside the work area.
            // Inset the client content so custom controls remain entirely on screen.
            WindowLayout.Margin = new Thickness(Math.Max(0, work.Left - first.X) * fromDevice.M11,
                Math.Max(0, work.Top - first.Y) * fromDevice.M22,
                Math.Max(0, last.X - work.Right) * fromDevice.M11,
                Math.Max(0, last.Y - work.Bottom) * fromDevice.M22);
        }

        private async Task<bool> Work(string label, Action action, bool frame = false, bool markDirty = true)
        {
            if (busy) return false;
            busy = true;
            ActionBar.IsEnabled = ProjectControls.IsEnabled = WorkspaceGrid.IsEnabled = false;
            BusyText.Text = label;
            BusyOverlay.Visibility = Visibility.Visible;
            StatusText.Text = label;
            try
            {
                await Task.Run(action);
                dirty |= markDirty;
                RefreshView(frame);
                return true;
            }
            catch (Exception ex)
            {
                dirty |= markDirty;
                RefreshView(false);
                StatusText.Text = "Operation stopped · " + ex.Message;
                MessageBox.Show(this, ex.Message, "Seam — operation stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                busy = false;
                ActionBar.IsEnabled = ProjectControls.IsEnabled = WorkspaceGrid.IsEnabled = true;
                BusyOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void Progress(string message)
        {
            Interlocked.Exchange(ref pendingProgress, message);
            if (Interlocked.Exchange(ref progressQueued, 1) != 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref progressQueued, 0);
                if (!busy) return;
                BusyText.Text = StatusText.Text = Volatile.Read(ref pendingProgress);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RefreshView(bool frame)
        {
            rendering = true;
            string packId = (PackList.SelectedItem as MapPack)?.Id;
            PackList.ItemsSource = null;
            PackList.ItemsSource = workspace.Packs;
            PackList.SelectedItem = workspace.Packs.FirstOrDefault(p => p.Id == packId);
            var enabled = workspace.Packs.Where(p => p.Enabled).ToArray();
            var plan = workspace.LastAutomaticMerge;
            bool hasInputs = enabled.SelectMany(p => p.Assets).Any();
            MapCount.Text = enabled.Length.ToString("N0");
            EntityCount.Text = enabled.Sum(p => p.EntityCount).ToString("N0");
            OutputCount.Text = (plan?.ResourceCount ?? 0).ToString("N0");
            AutoMergeButton.IsEnabled = ExportButton.IsEnabled = hasInputs;
            ExportButton.ToolTip = "Export each original resource separately with _merged, keeping its own .fxap and manifest";
            AutoMergeButton.ToolTip = "Merge YMAP/YBN files and repair verified prop, light and map-bound faults. Uncertain placements are reported; protected assets stay unchanged.";
            Welcome.Visibility = workspace.Packs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SourcesEmpty.Visibility = Welcome.Visibility;
            Viewport.SetScene(workspace.Scene, frame);
            int proxies = workspace.Scene.Count(i => i.IsProxy && !i.IsCollision && !i.Excluded);
            SceneSummary.Text = workspace.Scene.Count(i => !i.Excluded).ToString("N0") + " objects" + (proxies > 0 ? " · " + proxies.ToString("N0") + " proxies" : "");
            NoticeButton.Visibility = workspace.Notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoticeButton.Content = workspace.Notices.Count.ToString("N0") + " notices · view details";
            MergeState.Text = plan != null ? "Merge complete" : hasInputs ? "Ready to merge" : "Add your maps";
            ActivityBadge.Text = plan != null ? "DONE" : hasInputs ? "READY" : "IDLE";
            StateDot.Fill = (System.Windows.Media.Brush)FindResource(plan != null ? "Success" : "Accent");
            MergeSummary.Text = plan?.Summary ?? (hasInputs ? "Set your source order, then run Auto merge. Conflicts follow that priority." : "Add source folders to get started. Merge details will appear below.");
            FilterResults();
            if (selectedItem != null)
            {
                selectedItem = workspace.Scene.FirstOrDefault(i => i.Id == selectedItem.Id && !i.Excluded);
                ShowSelection(selectedItem);
            }
            StatusText.Text = workspace.Packs.Count == 0 ? "Local workspace · source files are preserved" :
                plan != null ? "Automatic merge complete · ready to export" : "Ready · click Auto merge or export directly";
            Title = "Seam" + (projectPath == null ? " — GTA V Map Merger" : " — " + Path.GetFileNameWithoutExtension(projectPath)) + (dirty ? " *" : "");
            rendering = false;
        }

        private void FilterResults()
        {
            if (ResultList == null || workspace == null) return;
            var previous = ResultList.SelectedItem as MergeOperation;
            string query = Search?.Text?.Trim() ?? "";
            var operations = (workspace.LastAutomaticMerge?.Operations ?? new System.Collections.Generic.List<MergeOperation>()).Where(operation =>
                query.Length == 0 || ((operation.Title ?? "") + " " + operation.Kind + " " + operation.Detail).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            ResultList.ItemsSource = operations;
            if (previous != null) ResultList.SelectedItem = operations.FirstOrDefault(operation => operation.Title == previous.Title && operation.Kind == previous.Kind);
            NoResults.Visibility = operations.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoResultsTitle.Text = query.Length > 0 ? "No matching results" : workspace.LastAutomaticMerge == null ? "Nothing here yet" : "Merge complete";
            NoResultsDetail.Text = query.Length > 0 ? "Try a different name or file type." : workspace.LastAutomaticMerge == null ?
                "Merged, preserved and skipped files will appear here after you run Auto merge." : "Your resource copies are ready to export.";
            if (ResultList.SelectedItem == null) ResultDetailCard.Visibility = Visibility.Collapsed;
        }

        private async Task Import(string[] paths)
        {
            if (paths.Length == 0) return;
            await Work("Reading map assets…", () => workspace.Import(paths, Progress), true);
        }

        private async void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new Forms.FolderBrowserDialog { Description = "Select a GTA V map resource or stream folder", ShowNewFolderButton = false })
                if (dialog.ShowDialog() == Forms.DialogResult.OK) await Import(new[] { dialog.SelectedPath });
        }

        private async void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "Add GTA V map assets", Multiselect = true, Filter = "GTA V assets|*.ymap;*.ybn;*.ydr;*.ydd;*.ytyp;*.ytd;*.xml|All files|*.*" };
            if (dialog.ShowDialog(this) == true) await Import(dialog.FileNames);
        }

        private async void Demo_Click(object sender, RoutedEventArgs e)
        {
            await Work("Building the demo maps…", () =>
            {
                string demo = Path.Combine(Path.GetTempPath(), "Seam-demo-" + Guid.NewGuid().ToString("N"));
                workspace.Import(MergeWorkspace.CreateDemo(demo), Progress);
            }, true);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (!busy && e.Data.GetDataPresent(DataFormats.FileDrop)) await Import((string[])e.Data.GetData(DataFormats.FileDrop));
        }

        private void PackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (rendering) return;
            var pack = PackList.SelectedItem as MapPack;
            if (pack == null) return;
            var items = workspace.Scene.Where(i => i.PackId == pack.Id && i.Bounds != null && !i.Excluded).ToArray();
            if (items.Length == 0) return;
            Viewport.Frame(new Box3 { MinX=items.Min(i=>i.Bounds.MinX),MinY=items.Min(i=>i.Bounds.MinY),MinZ=items.Min(i=>i.Bounds.MinZ),MaxX=items.Max(i=>i.Bounds.MaxX),MaxY=items.Max(i=>i.Bounds.MaxY),MaxZ=items.Max(i=>i.Bounds.MaxZ) });
        }

        private async void PackEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (!rendering) await Work("Updating enabled maps…", () => workspace.Refresh(Progress));
        }

        private async void MoveUp_Click(object sender, RoutedEventArgs e) { await MovePack(-1); }
        private async void MoveDown_Click(object sender, RoutedEventArgs e) { await MovePack(1); }
        private async Task MovePack(int direction)
        {
            var pack = PackList.SelectedItem as MapPack;
            if (pack == null) return;
            int index = workspace.Packs.IndexOf(pack), next = index + direction;
            if (next < 0 || next >= workspace.Packs.Count) return;
            await Work("Updating map priority…", () => { workspace.Packs.RemoveAt(index); workspace.Packs.Insert(next, pack); workspace.Refresh(Progress); });
        }

        private async void RemovePack_Click(object sender, RoutedEventArgs e)
        {
            var pack = PackList.SelectedItem as MapPack;
            if (pack == null) return;
            await Work("Removing map from workspace…", () => { workspace.Packs.Remove(pack); workspace.Refresh(Progress); }, true);
        }

        private async void AutoMerge_Click(object sender, RoutedEventArgs e)
        {
            if (await Work("Merging maps automatically…", () => workspace.AutoMergeResources(Progress)))
                StatusText.Text = workspace.LastAutomaticMerge.Summary;
        }

        private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(ResultList.SelectedItem is MergeOperation operation)) { ResultDetailCard.Visibility = Visibility.Collapsed; return; }
            ResultList.ScrollIntoView(operation);
            ResultDetailCard.Visibility = Visibility.Visible;
            ResultDetail.Text = operation.Title + "\n\n" + operation.Detail;
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e) { FilterResults(); }
        private void CollisionToggle_Click(object sender, RoutedEventArgs e) { Viewport.ShowCollisions = CollisionToggle.IsChecked == true; }
        private void FrameAll_Click(object sender, RoutedEventArgs e) { Viewport.FrameAll(); }
        private void FrameSelection_Click(object sender, RoutedEventArgs e) { if (selectedItem != null) Viewport.Frame(selectedItem.Bounds); }
        private void Viewport_ItemSelected(object sender, SceneSelectionEventArgs e) { ShowSelection(e.Item); }

        private void ShowSelection(SceneItem item)
        {
            selectedItem = item;
            SelectionCard.Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
            if (item == null) return;
            SelectedLabel.Text = item.Label;
            string pack = workspace.Packs.FirstOrDefault(p => p.Id == item.PackId)?.Name;
            SelectedDetails.Text = pack + " · " + (item.IsCollision ? "collision bound" : item.IsProxy ? "proxy bounds · model not available" : "drawable geometry") +
                (item.Bounds == null ? "" : $" · {item.Bounds.X:0.0}, {item.Bounds.Y:0.0}, {item.Bounds.Z:0.0}");
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new Forms.FolderBrowserDialog { Description = "Choose an output folder for the separate <resource>_merged copies", ShowNewFolderButton = true })
            {
                if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
                ExportResult result = null;
                if (await Work("Exporting separate resources…", () => result = workspace.ExportSeparateResources(dialog.SelectedPath, Progress), false, false))
                {
                    StatusText.Text = $"Exported {result.ResourcePaths.Count:N0} separate resources · {result.Files:N0} files";
                    if (MessageBox.Show(this, "Export complete.\n\n" + string.Join("\n", result.ResourcePaths) + "\n\nOriginal .fxap, manifests and assets were retained. Open the output folder?", "Seam — export complete", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true });
                }
            }
        }

        private async void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = "Open Seam project", Filter = "Seam project|*.seam|All files|*.*" };
            if (dialog.ShowDialog(this) == true) await OpenProject(dialog.FileName);
        }

        private async Task OpenProject(string path)
        {
            if (!ConfirmDiscard()) return;
            MergeWorkspace loaded = null;
            if (await Work("Opening project…", () => loaded = MergeWorkspace.LoadProject(path, Progress), false, false))
            {
                workspace = loaded;
                projectPath = path; dirty = false; selectedItem = null;
                RefreshView(true);
            }
        }

        private void SaveProject_Click(object sender, RoutedEventArgs e) { SaveProject(); }
        private bool SaveProject()
        {
            if (workspace.Packs.Count == 0) { StatusText.Text = "Add a map before saving a project."; return false; }
            string destination = projectPath;
            if (destination == null)
            {
                var dialog = new SaveFileDialog { Title = "Save Seam project", Filter = "Seam project|*.seam", DefaultExt = ".seam", FileName = "map_merge.seam" };
                if (dialog.ShowDialog(this) != true) return false;
                destination = dialog.FileName;
            }
            try
            {
                workspace.SaveProject(destination);
                projectPath = destination; dirty = false;
                RefreshView(false);
                StatusText.Text = "Project saved · source paths and priority recorded";
                return true;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save project", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        }

        private bool ConfirmDiscard()
        {
            if (!dirty || workspace.Packs.Count == 0) return true;
            var answer = MessageBox.Show(this, "Save your map sources and priorities before continuing?", "Seam — unsaved project", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && SaveProject());
        }

        private void Notices_Click(object sender, RoutedEventArgs e)
        {
            ShowText("Workspace notices", string.Join("\n\n", workspace.Notices));
        }

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            ShowText("A quick guide to Seam", "1. ADD YOUR MAPS\nAdd extracted GTA V Legacy map folders or loose stream assets. Include YMAP, YBN, YTYP, YDR/YDD and YTD files. The preview shows your imported source geometry; missing models use proxy bounds.\n\n2. SET SOURCE PRIORITY\nMove your preferred map to the top. The merger combines compatible file contents and uses this priority for conflicting definitions. Enable only the map sources you want in the output.\n\n3. AUTO MERGE\nClick Auto merge. Seam merges readable YMAP placements and YBN collision bounds. Encrypted or unsupported files are skipped by the decoder and copied unchanged, alongside all other assets. Merge results lists every skipped file. Results describe what was merged or preserved; no per-conflict picking is required.\n\n4. EXPORT\nChoose Export resources and select a destination folder. Each source gets its own folder named <original>_merged, preserving its original .fxap files, manifest, scripts, assets and subfolders. Only readable YMAP/YBN output changes. Shared collision filenames receive the same merged data in each resource. Use all output copies together instead of the originals. Export runs Auto merge if needed.\n\nDrag to orbit, right-drag to pan, scroll to zoom, or press Home to frame the source scene. Click geometry to inspect it. Save a .seam project to retain your source paths and priorities.\n\nCollision overlap alone does not prove an unwanted surface. The merger preserves distinct geometry rather than deleting whole overlapping components. It does not perform arbitrary mesh boolean subtraction. Read the operation report and test your map set in GTA V. Local scripts and supported manifest data are carried into the export. External @resource references still require their external resources. RPF archives must be extracted first.");
        }

        private void ShowText(string title, string text)
        {
            var dialog = new Window { Owner = this, Title = title, Width = 650, Height = 620, MinWidth = 450, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new DockPanel { Margin = new Thickness(24) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            if (title == "A quick guide to Seam")
            {
                var notices = new Button { Content = "Third-party notices", Margin = new Thickness(0, 0, 8, 0) };
                notices.Click += (s, e) => ShowText("Third-party notices", App.ThirdPartyNotices);
                buttons.Children.Add(notices);
            }
            var close = new Button { Content = "Close", MinWidth = 85 };
            close.Click += (s, e) => dialog.Close(); buttons.Children.Add(close);
            DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
            panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 23, Foreground = (System.Windows.Media.Brush)FindResource("Text") } });
            dialog.Content = panel; dialog.ShowDialog();
        }

        private string Prompt(string title, string label, string initial)
        {
            var dialog = new Window { Owner = this, Title = title, Width = 400, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 9) });
            var input = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 18) }; panel.Children.Add(input);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var accept = new Button { Content = "Continue", IsDefault = true, Style = (Style)FindResource("Primary") };
            accept.Click += (s, e) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
            buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons); dialog.Content = panel;
            dialog.Loaded += (s, e) => { input.Focus(); input.SelectAll(); };
            return dialog.ShowDialog() == true ? input.Text.Trim() : null;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (busy) return;
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveProject(); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { OpenProject_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Home && !(Keyboard.FocusedElement is TextBox)) { Viewport.FrameAll(); e.Handled = true; }
        }
    }
}
