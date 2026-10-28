using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Seam
{
    public sealed class GameSetupWindow : Window
    {
        private readonly TextBox folder;
        private readonly TextBlock error;
        public GameSetupWindow()
        {
            Title = "Seam — GTA V installation"; Width = 610; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = (Brush)Application.Current.FindResource("Background"); Foreground = (Brush)Application.Current.FindResource("Text");
            FontFamily = new FontFamily("Segoe UI");
            var panel = new StackPanel(); Content = new Border { Padding = new Thickness(30), Background = Background, Child = panel };
            panel.Children.Add(new TextBlock { Text = "Choose your GTA V installation", FontSize = 24, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "Select your GTA V Legacy folder to enable 3D previews and the live tattoo editor. You can change this later under GTA installation.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 22), FontSize = 13 });
            folder = new TextBox { Name = "GameFolderPath", Text = GameInstallation.SelectedFolder ?? ClothingGameAssets.DetectGameFolder(), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(folder);
            var browse = new Button { Content = "Browse for GTA V…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 8, 14, 8) };
            browse.Click += (s, e) => { using (var picker = new Forms.FolderBrowserDialog { Description = "Choose GTA V Legacy (GTA5.exe and x64v.rpf)", SelectedPath = folder.Text, ShowNewFolderButton = false }) if (picker.ShowDialog() == Forms.DialogResult.OK) folder.Text = picker.SelectedPath; }; panel.Children.Add(browse);
            error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(error);
            panel.Children.Add(new TextBlock { Text = "Without GTA: map merging/fixing, clothing pack creation and 2D texture editing remain available. All 3D previews and tattoo editing are disabled.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("Muted"), FontSize = 12, Margin = new Thickness(0, 18, 0, 23) });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var skip = new Button { Name = "ContinueWithoutGame", Content = "Continue without GTA", Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 10, 0) };
            skip.Click += (s, e) => Save(null);
            var use = new Button { Name = "UseGameFolder", Content = "Use this installation", Style = (Style)Application.Current.FindResource("Primary"), Padding = new Thickness(14, 10, 14, 10) };
            use.Click += (s, e) => { if (string.IsNullOrWhiteSpace(folder.Text)) { error.Text = "Choose a folder, or continue without GTA."; return; } Save(folder.Text.Trim().Trim('"')); };
            buttons.Children.Add(skip); buttons.Children.Add(use); panel.Children.Add(buttons);
            SourceInitialized += (s, e) => App.ApplyNativeTheme(this);
        }
        private void Save(string path)
        {
            try { GameInstallation.Select(path); Close(); }
            catch (Exception e) { error.Text = e.Message; }
        }
        public static void Configure(Window owner)
        {
            var dialog = new GameSetupWindow(); if (owner != null) { dialog.Owner = owner; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; } dialog.ShowDialog();
        }
    }
}
