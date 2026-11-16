using System;
using System.Globalization;
using System.Windows;

namespace Seam
{
    public partial class MapOptimizerWindow : Window
    {
        public MapOptimizerOptions Options { get; private set; }
        public bool MergeRequested { get; private set; }

        public MapOptimizerWindow(MapOptimizerOptions options)
        {
            InitializeComponent();
            var current = options?.Copy() ?? new MapOptimizerOptions();
            if (!(current.MaxDrawDistance > 0) || float.IsInfinity(current.MaxDrawDistance)) current.MaxDrawDistance = 500f;
            EnabledBox.IsChecked = current.Enabled;
            DuplicatesBox.IsChecked = current.RemoveDuplicatePlacements;
            EmptyBox.IsChecked = current.RemoveEmptyMaps;
            DrawDistanceBox.IsChecked = current.LimitDrawDistance;
            DrawDistanceText.Text = current.MaxDrawDistance.ToString("0.###", CultureInfo.InvariantCulture);
            SourceInitialized += (s, e) => App.ApplyNativeTheme(this);
            Loaded += (s, e) => { MergeButton.Focus(); ApplyEnabledState(); };
            Enabled_Click(null, null);
        }

        private void ApplyEnabledState()
        {
            bool enabled = EnabledBox.IsChecked == true;
            DuplicatesBox.IsEnabled = EmptyBox.IsEnabled = DrawDistanceBox.IsEnabled = enabled;
            DrawDistanceText.IsEnabled = enabled && DrawDistanceBox.IsChecked == true;
        }

        private void Enabled_Click(object sender, RoutedEventArgs e) => ApplyEnabledState();

        private void DrawDistance_Click(object sender, RoutedEventArgs e) => ApplyEnabledState();

        private bool TryRead(out MapOptimizerOptions options)
        {
            options = null;
            ValidationText.Text = "";
            bool enabled = EnabledBox.IsChecked == true;
            bool duplicates = DuplicatesBox.IsChecked == true;
            bool empty = EmptyBox.IsChecked == true;
            bool distance = DrawDistanceBox.IsChecked == true;
            if (enabled && !duplicates && !empty && !distance)
            {
                ValidationText.Text = "Choose at least one rule, or turn the optimizer off.";
                return false;
            }
            float maximum = 500f;
            if (enabled && distance)
            {
                if (!float.TryParse(DrawDistanceText.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out maximum) ||
                    float.IsNaN(maximum) || float.IsInfinity(maximum) || maximum < 1f || maximum > 100000f)
                {
                    ValidationText.Text = "Enter a draw distance between 1 and 100,000 m.";
                    DrawDistanceText.Focus();
                    DrawDistanceText.SelectAll();
                    return false;
                }
                DrawDistanceText.Text = maximum.ToString("0.###", CultureInfo.InvariantCulture);
            }
            options = new MapOptimizerOptions
            {
                Enabled = enabled, RemoveDuplicatePlacements = duplicates,
                RemoveEmptyMaps = empty, LimitDrawDistance = distance, MaxDrawDistance = maximum
            };
            return true;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead(out var options)) return;
            Options = options; MergeRequested = false;
            DialogResult = true;
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead(out var options)) return;
            Options = options; MergeRequested = true;
            DialogResult = true;
        }
    }
}
