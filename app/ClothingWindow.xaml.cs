using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodeWalker.GameFiles;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Seam
{
    public partial class ClothingWindow : Window
    {
        private ClothingProject project = new ClothingProject();
        private ClothingItem selectedItem;
        private TattooDesign selectedTattoo;
        private TextureDocument textureDocument;
        private Texture selectedTexture;
        private FileSystemWatcher imageWatcher;
        private readonly DispatcherTimer liveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        private readonly DispatcherTimer watchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private string watchedImage, projectPath, gameFolder;
        private string dictionaryWorkingPath;
        private readonly HashSet<string> dictionaryLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly string workDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Seam", "Clothing", Guid.NewGuid().ToString("N"));
        private bool loadingControls = true, busy, dirty, artworkDirty;
        private string mode = "wardrobe";
        private int previewRequest;
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        public ClothingWindow() : this(true) { }
        public ClothingWindow(bool loadGame)
        {
            InitializeComponent(); loadingControls = false;
            Width = Math.Min(1440, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 32));
            Height = Math.Min(900, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32));
            Preview.StatusChanged += text => Dispatcher.InvokeAsync(() => { StatusText.Text = text; GameHint.Text = text; UpdateGameAvailability(); });
            Artwork.Changed += () => { if (loadingControls) return; artworkDirty = dirty = true; liveTimer.Stop(); liveTimer.Start(); };
            Artwork.LayerSelected += layer => { LayerList.SelectedItem = layer; RefreshLayerControls(layer); };
            liveTimer.Tick += (s, e) => { liveTimer.Stop(); Safe(UpdateLivePreview); };
            watchTimer.Tick += (s, e) => { watchTimer.Stop(); Safe(() => { if (selectedTexture == null || string.IsNullOrEmpty(watchedImage)) return; ReplaceBitmap(TextureDocument.LoadImage(watchedImage)); SetStatus("External image refreshed · " + Path.GetFileName(watchedImage)); }); };
            SourceInitialized += (s, e) => { try { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { } };
            gameFolder = GameInstallation.SelectedFolder;
            GameInstallation.Changed += InstallationChanged;
            Loaded += async (s, e) => { UpdateGameAvailability(); if (loadGame) await LoadPed(); };
            Closing += (s, e) => { if (busy || !ConfirmDiscard()) e.Cancel = true; };
            Closed += (s, e) => { GameInstallation.Changed -= InstallationChanged; imageWatcher?.Dispose(); liveTimer.Stop(); watchTimer.Stop(); };
            RefreshLibrary();
            UpdateGameAvailability();
        }
        private void SetStatus(string message) { StatusText.Text = message; }
        private void Safe(Action action) { try { action(); } catch (Exception ex) { ShowError(ex); } }
        private void ShowError(Exception ex) { SetStatus(ex.Message); MessageBox.Show(this, ex.Message, "Clothing Studio", MessageBoxButton.OK, MessageBoxImage.Warning); }
        private async Task Run(Func<Task> action, string message = null)
        {
            if (busy) return;
            busy = true; Progress.Visibility = Visibility.Visible; ExportButton.IsEnabled = ImportButton.IsEnabled = FolderButton.IsEnabled = false;
            if (message != null) SetStatus(message);
            try { await action(); } catch (Exception ex) { ShowError(ex); }
            finally { busy = false; Progress.Visibility = Visibility.Collapsed; ExportButton.IsEnabled = ImportButton.IsEnabled = FolderButton.IsEnabled = true; }
        }
        private string WorkPath(string name) { Directory.CreateDirectory(workDirectory); return Path.Combine(workDirectory, name); }
        private async Task LoadPed()
        {
            gameFolder = GameInstallation.SelectedFolder;
            if (string.IsNullOrEmpty(gameFolder)) { UpdateGameAvailability(); return; }
            try { await Preview.SetPedAsync(mode == "tattoo" ? (TattooPedBox.SelectedIndex == 1 ? "female" : "male") : (PedBox.SelectedIndex == 1 ? "female" : "male"), gameFolder); if (selectedItem != null && mode != "tattoo") Preview.ShowItem(selectedItem); }
            catch (Exception ex) { SetStatus("Character preview: " + ex.Message); }
            UpdateGameAvailability();
        }
        private void UpdateGameAvailability()
        {
            bool available = GameInstallation.Available;
            Preview.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            FramePreviewButton.IsEnabled = available && Preview.HasPed;
            TattooTab.IsEnabled = available && Preview.HasPed;
            TattooTab.ToolTip = TattooTab.IsEnabled ? "Edit tattoos with the live freemode preview" : "Tattoo editing requires a selected GTA V installation and a loaded live ped preview.";
            WelcomePanel.Visibility = available && Preview.HasPed ? Visibility.Collapsed : Visibility.Visible;
            if (!available) GameHint.Text = "3D previews and tattoo editing are disabled. Clothing packs and 2D textures still work. Choose GTA installation to enable previews.";
            if (mode == "tattoo" && !TattooTab.IsEnabled) SwitchMode("wardrobe");
        }
        private async void InstallationChanged(object sender, EventArgs e) { UpdateGameAvailability(); await LoadPed(); }
        private void ChooseGameFolder(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            GameSetupWindow.Configure(this);
        }
        private void GoHome(object sender, RoutedEventArgs e) { if (busy || !ConfirmDiscard()) return; dirty = false; ((App)Application.Current).ShowLauncher(); Close(); }
        private void ProjectNameChanged(object sender, TextChangedEventArgs e) { if (loadingControls) return; project.Name = ProjectNameBox.Text; dirty = true; }
        private void RefreshLibrary()
        {
            bool prior = loadingControls; loadingControls = true;
            ItemList.ItemsSource = null; ItemList.ItemsSource = project.Items;
            TattooList.ItemsSource = null; TattooList.ItemsSource = project.Tattoos;
            ItemList.SelectedItem = selectedItem; TattooList.SelectedItem = selectedTattoo; loadingControls = prior;
            LibraryCount.Text = mode == "tattoo" ? project.Tattoos.Count + " tattoo designs" : mode == "texture" ? (textureDocument?.Textures.Count ?? 0) + " textures" : project.Items.Count + " pieces";
        }
        private void SwitchMode(string next)
        {
            if (next == "tattoo" && (!GameInstallation.Available || !Preview.HasPed)) { SetStatus("Tattoo editing requires a loaded live GTA V character preview."); return; }
            if (mode == "texture" && next != mode) CommitTextureToWorkingCopy();
            if (mode == "tattoo" && next != mode)
            {
                if (selectedTattoo != null && artworkDirty) SaveTattoo();
                if (GameInstallation.Available) { Preview.ShowTattoo(null, null, 0, 0, 0, 0, 0); Preview.ShowItem(selectedItem); }
            }
            mode = next;
            ItemList.Visibility = next == "wardrobe" ? Visibility.Visible : Visibility.Collapsed;
            TextureList.Visibility = next == "texture" ? Visibility.Visible : Visibility.Collapsed;
            TattooList.Visibility = next == "tattoo" ? Visibility.Visible : Visibility.Collapsed;
            WardrobeProperties.Visibility = next == "wardrobe" ? Visibility.Visible : Visibility.Collapsed;
            EditorProperties.Visibility = next == "wardrobe" ? Visibility.Collapsed : Visibility.Visible;
            TextureActions.Visibility = ApplyTextureButton.Visibility = next == "texture" ? Visibility.Visible : Visibility.Collapsed;
            TattooActions.Visibility = next == "tattoo" ? Visibility.Visible : Visibility.Collapsed;
            Artwork.Visibility = next == "wardrobe" ? Visibility.Collapsed : Visibility.Visible;
            ArtworkColumn.Width = new GridLength(next == "wardrobe" ? 0 : 1.35, GridUnitType.Star);
            LibraryTitle.Text = next == "wardrobe" ? "YOUR WARDROBE" : next == "texture" ? "TEXTURE DICTIONARY" : "TATTOO DESIGNS";
            LibraryHint.Text = next == "wardrobe" ? "Drop YDD, YDR and YTD files here." : next == "texture" ? "PNG and DDS images. Live on your model." : "Original artwork, made for your character.";
            ImportButton.Content = next == "tattoo" ? "＋ New tattoo" : next == "texture" ? "Open YTD" : "＋ Import files";
            FolderButton.Content = next == "tattoo" ? "Import image" : next == "texture" ? "Add texture" : "Add folder";
            EditorTitle.Text = next == "tattoo" ? "TATTOO TOOLS" : "TEXTURE TOOLS";
            SceneHelp.Text = next == "wardrobe" ? "Drag to orbit  ·  Right-drag to pan  ·  Scroll to zoom" : "Select moves artwork layers  ·  Draw adds strokes  ·  Edits update the preview";
            WardrobeTab.Style = (Style)FindResource(next == "wardrobe" ? "Small" : "Quiet"); TextureTab.Style = (Style)FindResource(next == "texture" ? "Small" : "Quiet"); TattooTab.Style = (Style)FindResource(next == "tattoo" ? "Small" : "Quiet");
            RefreshLibrary();
        }
        private void WardrobeClicked(object s, RoutedEventArgs e) { Safe(() => SwitchMode("wardrobe")); }
        private void TextureClicked(object s, RoutedEventArgs e) { Safe(() => { bool entering = mode != "texture"; SwitchMode("texture"); if (textureDocument == null && selectedItem?.TexturePaths.Count > 0) OpenYtd(selectedItem.TexturePaths[0]); else if (entering && selectedTexture != null) ActivateTexture(selectedTexture); }); }
        private async void TattooClicked(object s, RoutedEventArgs e)
        {
            if (!GameInstallation.Available || !Preview.HasPed) { SetStatus("Choose a GTA V installation and load its character before editing tattoos."); return; }
            if (mode == "tattoo") return;
            Safe(() => { SwitchMode("tattoo"); Preview.ShowItem(null); if (selectedTattoo == null && project.Tattoos.Count == 0) NewTattoo(); else if (selectedTattoo != null) LoadTattoo(selectedTattoo); });
            await LoadPed(); Safe(UpdateLivePreview);
        }
        private async void ImportFilesClicked(object sender, RoutedEventArgs e)
        {
            if (mode == "tattoo") { Safe(NewTattoo); await LoadPed(); return; }
            var dialog = new OpenFileDialog { Filter = mode == "texture" ? "Texture dictionaries|*.ytd" : "GTA clothing assets|*.ydd;*.ydr;*.ytd|All files|*.*", Multiselect = mode != "texture" };
            if (dialog.ShowDialog(this) != true) return;
            if (mode == "texture") { Safe(() => OpenYtd(dialog.FileName)); return; }
            await Import(dialog.FileNames);
        }
        private async void ImportFolderClicked(object sender, RoutedEventArgs e)
        {
            if (mode == "tattoo") { Safe(() => { if (selectedTattoo == null) NewTattoo(); AddImageLayer(); }); return; }
            if (mode == "texture") { Safe(AddTexture); return; }
            using (var dialog = new Forms.FolderBrowserDialog { Description = "Select a folder of your clothing files." })
                if (dialog.ShowDialog() == Forms.DialogResult.OK) await Run(async () => { await Task.Run(() => project.ImportFolder(dialog.SelectedPath)); dirty = true; RefreshLibrary(); SetStatus("Clothing folder imported. Check each piece's slot before building."); }, "Reading clothing files…");
        }
        private async Task Import(string[] paths)
        {
            await Run(async () => { await Task.Run(() => { foreach (var path in paths) if (Directory.Exists(path)) project.ImportFolder(path); else project.ImportFiles(new[] { path }); }); dirty = true; RefreshLibrary(); SetStatus("Imported clothing. Select a piece to inspect and assign its slot."); }, "Importing clothing…");
            if (project.Items.Count > 0) ItemList.SelectedIndex = project.Items.Count - 1;
        }
        private async void FilesDropped(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) { Safe(() => SwitchMode("wardrobe")); await Import(paths); }
        }
        private async void ItemSelected(object sender, SelectionChangedEventArgs e)
        {
            if (loadingControls || !(ItemList.SelectedItem is ClothingItem item)) return;
            selectedItem = item; loadingControls = true;
            SelectedName.Text = item.Name; SelectedPath.Text = item.SourcePath; PedBox.SelectedIndex = item.Ped == "female" ? 1 : 0; PropBox.IsChecked = item.IsProp; PopulateSlots(item.IsProp, item.Component); DrawableBox.Text = item.DrawableId.ToString(); loadingControls = false;
            int request = ++previewRequest; await LoadPed(); if (request != previewRequest) return;
            Safe(() => { if (GameInstallation.Available) Preview.ShowItem(item); UpdateGameAvailability(); SetStatus(item.Error ?? "Piece loaded · " + item.Name); });
        }
        private async void AssignmentChanged(object sender, RoutedEventArgs e)
        {
            if (loadingControls || selectedItem == null) return;
            if (ReferenceEquals(sender, PropBox)) { loadingControls = true; PopulateSlots(PropBox.IsChecked == true, selectedItem.Component); loadingControls = false; }
            if (!int.TryParse(DrawableBox.Text, out int drawable) || drawable < 0 || drawable > 999) { SetStatus("Drawable index must be between 0 and 999."); return; }
            selectedItem.Ped = PedBox.SelectedIndex == 1 ? "female" : "male"; selectedItem.Component = ComponentBox.SelectedIndex; selectedItem.IsProp = PropBox.IsChecked == true; selectedItem.DrawableId = drawable; dirty = true; await LoadPed();
        }
        private void PopulateSlots(bool prop, int index)
        {
            string[] labels = prop ? new[] { "Hat", "Glasses", "Ears", "Mouth", "Left hand", "Right hand", "Watch", "Bracelet", "Hip", "Left foot", "Right foot", "Other 1", "Other 2" }
                : new[] { "Head", "Mask", "Hair", "Arms / torso", "Legs", "Bag", "Shoes", "Accessories", "Undershirt", "Armour", "Decal", "Top" };
            ComponentBox.Items.Clear(); for (int i = 0; i < labels.Length; i++) ComponentBox.Items.Add(new ComboBoxItem { Content = i + " · " + labels[i] });
            ComponentBox.SelectedIndex = Math.Max(0, Math.Min(labels.Length - 1, index));
        }
        private void FrameClicked(object sender, RoutedEventArgs e) { Preview.FrameAll(); }
        private void EditItemTextures(object sender, RoutedEventArgs e) { Safe(() => { SwitchMode("texture"); if (selectedItem?.TexturePaths.Count > 0) OpenYtd(selectedItem.TexturePaths[0]); else SetStatus("No associated YTD. Open a texture dictionary to edit it."); }); }
        private void OpenYtd(string path)
        {
            CommitTextureToWorkingCopy(); var opened = TextureDocument.Open(path); StopWatching(); textureDocument = opened; selectedTexture = null;
            dictionaryLinks.Clear(); dictionaryLinks.Add(path); dictionaryWorkingPath = Path.Combine(workDirectory, "textures-" + Guid.NewGuid().ToString("N"), Path.GetFileName(path));
            TextureList.ItemsSource = null; TextureList.ItemsSource = textureDocument.Textures; TextureList.SelectedIndex = textureDocument.Textures.Count > 0 ? 0 : -1; EditingName.Text = selectedTexture?.Name ?? Path.GetFileName(path); SceneCaption.Text = Path.GetFileName(path).ToUpperInvariant(); RefreshLibrary(); SetStatus("Dictionary opened · " + Path.GetFileName(path));
        }
        private void TextureSelected(object sender, SelectionChangedEventArgs e)
        {
            if (loadingControls || !(TextureList.SelectedItem is Texture texture)) return;
            Safe(() => { if (selectedTexture != null && artworkDirty) ApplyArtwork(); ActivateTexture(texture); });
        }
        private void ActivateTexture(Texture texture)
        {
            StopWatching(); selectedTexture = texture; artworkDirty = false; loadingControls = true;
            try { Artwork.Reset(TextureDocument.ReadBitmap(texture)); } finally { loadingControls = false; }
            LayerList.ItemsSource = null; EditingName.Text = texture.Name; if (GameInstallation.Available) Preview.RefreshTexture(texture.Name, TextureDocument.ReadBitmap(texture)); SetStatus(texture.Width + " × " + texture.Height + " · " + texture.Format);
        }
        private void ReplaceBitmap(BitmapSource bitmap)
        {
            if (selectedTexture == null || textureDocument == null) throw new InvalidOperationException("Select a texture first.");
            selectedTexture = textureDocument.Replace(selectedTexture, bitmap); loadingControls = true; Artwork.Reset(bitmap); TextureList.ItemsSource = null; TextureList.ItemsSource = textureDocument.Textures; TextureList.SelectedItem = selectedTexture; loadingControls = false; artworkDirty = false; dirty = true; if (GameInstallation.Available) Preview.RefreshTexture(selectedTexture.Name, bitmap); RefreshLibrary();
        }
        private void ReplaceTextureClicked(object sender, RoutedEventArgs e) { Safe(() => { string path = PickImage(); if (path != null) ReplaceBitmap(TextureDocument.LoadImage(path)); }); }
        private string PickImage()
        {
            var dialog = new OpenFileDialog { Filter = "Images|*.png;*.dds;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*" }; return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        private void AddTexture()
        {
            if (textureDocument == null) { SetStatus("Open a YTD before adding textures."); return; }
            string path = PickImage(); if (path == null) return;
            var texture = textureDocument.Add(Path.GetFileNameWithoutExtension(path), TextureDocument.LoadImage(path)); TextureList.ItemsSource = null; TextureList.ItemsSource = textureDocument.Textures; TextureList.SelectedItem = texture; dirty = true; RefreshLibrary();
        }
        private void ApplyArtwork()
        {
            if (selectedTexture == null || !artworkDirty) return;
            selectedTexture = textureDocument.Replace(selectedTexture, Artwork.Render()); artworkDirty = false; dirty = true;
            loadingControls = true; TextureList.ItemsSource = null; TextureList.ItemsSource = textureDocument.Textures; TextureList.SelectedItem = selectedTexture; loadingControls = false;
        }
        private void ApplyArtworkClicked(object sender, RoutedEventArgs e) { Safe(() => { ApplyArtwork(); UpdateLivePreview(); SetStatus("Artwork applied. Save a YTD copy or build the pack."); }); }
        private void UpdateLivePreview()
        {
            if (!GameInstallation.Available) return;
            if (mode == "texture" && selectedTexture != null) Preview.RefreshTexture(selectedTexture.Name, Artwork.Render());
            else if (mode == "tattoo" && selectedTattoo != null) Preview.ShowTattoo(Artwork.Render(), selectedTattoo.Zone, selectedTattoo.U, selectedTattoo.V, selectedTattoo.ScaleX, selectedTattoo.ScaleY, selectedTattoo.Rotation);
        }
        private void ExportTextureClicked(object sender, RoutedEventArgs e)
        {
            Safe(() => { if (selectedTexture == null) return; ApplyArtwork(); var dialog = new SaveFileDialog { Filter = "PNG image|*.png|DDS texture|*.dds", FileName = selectedTexture.Name }; if (dialog.ShowDialog(this) == true) { if (dialog.FilterIndex == 2) TextureDocument.SaveDds(selectedTexture, dialog.FileName); else TextureDocument.SavePng(TextureDocument.ReadBitmap(selectedTexture), dialog.FileName); SetStatus("Texture exported · " + dialog.FileName); } });
        }
        private void SaveYtdClicked(object sender, RoutedEventArgs e)
        {
            Safe(() => { if (textureDocument == null) return; ApplyArtwork(); var dialog = new SaveFileDialog { Filter = "Texture dictionary|*.ytd", FileName = Path.GetFileName(textureDocument.SourcePath) }; if (dialog.ShowDialog(this) == true) { textureDocument.Save(dialog.FileName); AttachEditedDictionary(dialog.FileName); SetStatus("Dictionary saved · " + dialog.FileName); } });
        }
        private void AttachEditedDictionary(string path)
        {
            foreach (var item in project.Items)
                for (int i = 0; i < item.TexturePaths.Count; i++) if (dictionaryLinks.Contains(item.TexturePaths[i])) item.TexturePaths[i] = path;
            dictionaryLinks.Add(path);
            dirty = true;
        }
        private void CommitTextureToWorkingCopy()
        {
            if (textureDocument == null) return; if (mode == "texture") ApplyArtwork(); if (!textureDocument.IsDirty) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dictionaryWorkingPath)); textureDocument.Save(dictionaryWorkingPath); AttachEditedDictionary(dictionaryWorkingPath);
        }
        private void WatchTextureClicked(object sender, RoutedEventArgs e)
        {
            Safe(() => { if (selectedTexture == null) return; string path = PickImage(); if (path == null) return; StopWatching(); watchedImage = path; ReplaceBitmap(TextureDocument.LoadImage(path)); imageWatcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true }; FileSystemEventHandler refresh = (s, args) => Dispatcher.InvokeAsync(() => { watchTimer.Stop(); watchTimer.Start(); }); imageWatcher.Changed += refresh; imageWatcher.Created += refresh; imageWatcher.Renamed += (s, args) => refresh(s, args); SetStatus("Watching " + Path.GetFileName(path) + " · Save it in your image editor for live updates."); });
        }
        private void StopWatching() { imageWatcher?.Dispose(); imageWatcher = null; watchedImage = null; watchTimer.Stop(); }
        private void NewTattoo()
        {
            if (!GameInstallation.Available || !Preview.HasPed) { SetStatus("Tattoo editing requires a loaded live GTA V character preview."); return; }
            if (selectedTattoo != null && artworkDirty) SaveTattoo();
            var tattoo = new TattooDesign { Id = Guid.NewGuid().ToString("N"), Name = "Tattoo " + (project.Tattoos.Count + 1), Ped = "male", Zone = "ZONE_TORSO", U = .5, V = .5, ScaleX = .2, ScaleY = .2 };
            project.Tattoos.Add(tattoo); selectedTattoo = tattoo; loadingControls = true; Artwork.Reset(); loadingControls = false; artworkDirty = dirty = true; RefreshLibrary(); TattooList.SelectedItem = tattoo; LoadTattooControls(tattoo); EditingName.Text = tattoo.Name;
        }
        private void TattooSelected(object sender, SelectionChangedEventArgs e)
        {
            if (loadingControls || !(TattooList.SelectedItem is TattooDesign tattoo) || tattoo == selectedTattoo) return;
            Safe(() => { if (selectedTattoo != null && artworkDirty) SaveTattoo(); LoadTattoo(tattoo); });
        }
        private async void LoadTattoo(TattooDesign tattoo)
        {
            selectedTattoo = tattoo; loadingControls = true;
            try
            {
                if (!string.IsNullOrEmpty(tattoo.ArtworkPath) && File.Exists(tattoo.ArtworkPath)) Artwork.LoadArtwork(tattoo.ArtworkPath);
                else if (!string.IsNullOrEmpty(tattoo.SourceImagePath) && File.Exists(tattoo.SourceImagePath)) Artwork.Reset(TextureDocument.LoadImage(tattoo.SourceImagePath), tattoo.SourceImagePath);
                else Artwork.Reset();
                LoadTattooControls(tattoo); EditingName.Text = tattoo.Name; RefreshLayers(); artworkDirty = false;
            }
            catch (Exception ex) { ShowError(ex); }
            finally { loadingControls = false; }
            await LoadPed(); Safe(UpdateLivePreview);
        }
        private void LoadTattooControls(TattooDesign tattoo)
        {
            bool wasLoading = loadingControls; loadingControls = true;
            TattooNameBox.Text = tattoo.Name; TattooPedBox.SelectedIndex = tattoo.Ped == "female" ? 1 : 0;
            ZoneBox.SelectedItem = ZoneBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tattoo.Zone) ?? ZoneBox.Items[0];
            TattooU.Value = tattoo.U; TattooV.Value = tattoo.V; TattooWidth.Value = tattoo.ScaleX; TattooHeight.Value = tattoo.ScaleY; TattooRotation.Value = tattoo.Rotation; loadingControls = wasLoading;
        }
        private async void TattooMetadataChanged(object sender, RoutedEventArgs e)
        {
            if (loadingControls || selectedTattoo == null) return;
            selectedTattoo.Name = TattooNameBox.Text; selectedTattoo.Ped = TattooPedBox.SelectedIndex == 1 ? "female" : "male"; selectedTattoo.Zone = (ZoneBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ZONE_TORSO"; dirty = true; EditingName.Text = selectedTattoo.Name;
            if (ReferenceEquals(sender, TattooPedBox)) await LoadPed(); Safe(UpdateLivePreview);
        }
        private void TattooPlacementChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loadingControls || selectedTattoo == null) return;
            selectedTattoo.U = TattooU.Value; selectedTattoo.V = TattooV.Value; selectedTattoo.ScaleX = TattooWidth.Value; selectedTattoo.ScaleY = TattooHeight.Value; selectedTattoo.Rotation = TattooRotation.Value; dirty = true; liveTimer.Stop(); liveTimer.Start();
        }
        private void SaveTattoo()
        {
            if (selectedTattoo == null) return;
            string png = WorkPath(selectedTattoo.Id + ".png"), artwork = WorkPath(selectedTattoo.Id + ".artwork.json");
            TextureDocument.SavePng(Artwork.Render(), png); Artwork.SaveArtwork(artwork); selectedTattoo.SourceImagePath = png; selectedTattoo.ArtworkPath = artwork; artworkDirty = false; dirty = true;
        }
        private void SaveTattooClicked(object sender, RoutedEventArgs e) { Safe(() => { SaveTattoo(); RefreshLibrary(); SetStatus("Tattoo artwork saved. Build the pack to export the tattoo collection."); }); }
        private void PaintModeChanged(object sender, SelectionChangedEventArgs e) { if (loadingControls) return; Artwork.SetMode(PaintMode.SelectedIndex == 1 ? "Draw" : PaintMode.SelectedIndex == 2 ? "Erase" : "Select"); }
        private void BrushChanged(object sender, RoutedEventArgs e) { if (loadingControls) return; Safe(() => { if (!double.TryParse(BrushSizeBox.Text, out double size) || size < 1 || size > 200) throw new InvalidOperationException("Brush size must be between 1 and 200 pixels."); Artwork.SetBrush(ColorBox.Text, size); }); }
        private void AddImageLayer() { string path = PickImage(); if (path == null) return; string copy = WorkPath(Guid.NewGuid().ToString("N") + Path.GetExtension(path)); File.Copy(path, copy); Artwork.AddImage(copy); RefreshLayers(); }
        private void AddImageLayerClicked(object sender, RoutedEventArgs e) { Safe(AddImageLayer); }
        private void AddTextLayerClicked(object sender, RoutedEventArgs e) { Safe(() => { Artwork.AddText(LayerTextBox.Text, ColorBox.Text); RefreshLayers(); }); }
        private void RefreshLayers() { LayerList.ItemsSource = null; LayerList.ItemsSource = Artwork.Data.Layers; LayerList.SelectedItem = Artwork.SelectedLayer; }
        private void LayerSelected(object sender, SelectionChangedEventArgs e) { if (loadingControls) return; Artwork.SelectedLayer = LayerList.SelectedItem as ArtworkLayer; RefreshLayerControls(Artwork.SelectedLayer); }
        private void RefreshLayerControls(ArtworkLayer layer) { if (layer == null) return; loadingControls = true; LayerSize.Value = layer.Size; LayerOpacity.Value = layer.Opacity; LayerRotation.Value = layer.Rotation; loadingControls = false; }
        private void LayerChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (loadingControls || Artwork.SelectedLayer == null) return; var layer = Artwork.SelectedLayer; layer.Size = LayerSize.Value; layer.Opacity = LayerOpacity.Value; layer.Rotation = LayerRotation.Value; Artwork.UpdateLayer(layer); }
        private void UndoClicked(object sender, RoutedEventArgs e) { Artwork.UndoStroke(); }
        private void DeleteLayerClicked(object sender, RoutedEventArgs e) { Artwork.DeleteSelected(); RefreshLayers(); }
        private void RemoveClicked(object sender, RoutedEventArgs e)
        {
            Safe(() => { if (mode == "wardrobe" && selectedItem != null) { project.Items.Remove(selectedItem); project.Reindex(); selectedItem = null; SelectedName.Text = "Select a piece"; Preview.ShowItem(null); } else if (mode == "tattoo" && selectedTattoo != null) { project.Tattoos.Remove(selectedTattoo); selectedTattoo = null; Artwork.Reset(); Preview.ShowTattoo(null, null, 0, 0, 0, 0, 0); } else if (mode == "texture") { SetStatus("Removing textures can break materials. Replace a texture or build a new dictionary instead."); return; } dirty = true; RefreshLibrary(); });
        }
        private bool ConfirmDiscard()
        {
            if (!dirty) return true; var answer = MessageBox.Show(this, "Save this clothing project before leaving?", "Unsaved project", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false; if (answer == MessageBoxResult.No) return true;
            try { return SaveProject(); } catch (Exception ex) { ShowError(ex); return false; }
        }
        private void SaveProjectClicked(object sender, RoutedEventArgs e) { Safe(() => SaveProject()); }
        private bool SaveProject()
        {
            if (projectPath == null) { var dialog = new SaveFileDialog { Filter = "Seam clothing project|*.seamclothes", FileName = project.Name }; if (dialog.ShowDialog(this) != true) return false; projectPath = dialog.FileName; }
            CommitTextureToWorkingCopy(); if (mode == "tattoo" && selectedTattoo != null) SaveTattoo(); project.Name = ProjectNameBox.Text; project.Save(projectPath); dirty = false; SetStatus("Project saved · " + projectPath); return true;
        }
        private void OpenProjectClicked(object sender, RoutedEventArgs e)
        {
            Safe(() => { if (busy || !ConfirmDiscard()) return; var dialog = new OpenFileDialog { Filter = "Seam clothing project|*.seamclothes;*.json|All files|*.*" }; if (dialog.ShowDialog(this) != true) return; var opened = ClothingProject.Load(dialog.FileName); StopWatching(); project = opened; projectPath = dialog.FileName; selectedItem = null; selectedTattoo = null; textureDocument = null; selectedTexture = null; loadingControls = true; ProjectNameBox.Text = project.Name; loadingControls = false; SwitchMode("wardrobe"); dirty = false; SetStatus("Project opened · " + projectPath); });
        }
        private async void ExportClicked(object sender, RoutedEventArgs e)
        {
            string directory; bool singleplayer = BuildTargetBox.SelectedIndex == 1;
            using (var dialog = new Forms.FolderBrowserDialog { Description = singleplayer ? "Choose an output folder for your Singleplayer DLC pack." : "Choose the parent folder for your new FiveM clothing resource." }) { if (dialog.ShowDialog() != Forms.DialogResult.OK) return; directory = dialog.SelectedPath; }
            await Run(async () => { CommitTextureToWorkingCopy(); if (mode == "tattoo" && selectedTattoo != null) SaveTattoo(); project.Name = ProjectNameBox.Text; project.Reindex(); string output = await Task.Run(() => singleplayer ? project.ExportSinglePlayer(directory, project.Name) : project.ExportFiveM(directory, project.Name)); SetStatus("Pack built · " + output); MessageBox.Show(this, "Your pack is ready:\n\n" + output + (singleplayer ? "\n\nFollow the included installation instructions for your Singleplayer mods folder." : "\n\nAdd it to your server's resources and ensure it in server.cfg."), "Pack built", MessageBoxButton.OK, MessageBoxImage.Information); }, "Building and validating your pack…");
        }
    }
}
