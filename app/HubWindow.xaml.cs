using System;
using System.ComponentModel;
using System.Windows;

namespace Seam
{
    public partial class HubWindow : Window
    {
        public HubWindow()
        {
            InitializeComponent();
            VersionLabel.Text = "Seam " + typeof(App).Assembly.GetName().Version.ToString(3);
            SourceInitialized += (s,e) => App.ApplyNativeTheme(this);
            Loaded += (s,e) => { var area=SystemParameters.WorkArea; MaxHeight=area.Height; MaxWidth=area.Width; };
        }
        private void Map_Click(object sender,RoutedEventArgs e) => ((App)Application.Current).OpenMapMerger();
        private void Clothing_Click(object sender,RoutedEventArgs e) => ((App)Application.Current).OpenClothingStudio();
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!((App)Application.Current).CloseTools()) e.Cancel=true;
            base.OnClosing(e);
        }
    }
}
