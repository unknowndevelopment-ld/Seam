using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Seam
{
    public partial class App : Application
    {
        private static readonly Dictionary<string, Assembly> EmbeddedAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,Window> toolWindows = new Dictionary<string,Window>();
        private HubWindow launcher;
        private bool directMode, closingTools;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle,int attribute,ref int value,int size);

        internal static void ApplyNativeTheme(Window window)
        {
            try { int dark=1; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle,20,ref dark,sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        public void ShowLauncher()
        {
            directMode=false;
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            if(launcher==null)
            {
                launcher=new HubWindow();
                launcher.Closed+=(s,e)=>{launcher=null;if(!closingTools)Shutdown();};
            }
            MainWindow=launcher;
            launcher.Show();
            if(launcher.WindowState==WindowState.Minimized)launcher.WindowState=WindowState.Normal;
            launcher.Activate();
        }

        public void OpenMapMerger() => OpenTool("maps",()=>new Seam.MainWindow());
        public void OpenClothingStudio() => OpenTool("clothing",()=>new ClothingWindow());

        private void OpenTool(string key,Func<Window> create)
        {
            if(!toolWindows.TryGetValue(key,out var window))
            {
                window=create(); toolWindows.Add(key,window);
                window.SourceInitialized+=(s,e)=>ApplyNativeTheme(window);
                window.Closed+=(s,e)=>{toolWindows.Remove(key);if(!directMode && !closingTools)ShowLauncher();};
            }
            MainWindow=window;
            ShutdownMode=directMode ? ShutdownMode.OnMainWindowClose : ShutdownMode.OnExplicitShutdown;
            window.Show();
            if(window.WindowState==WindowState.Minimized)window.WindowState=WindowState.Normal;
            window.Activate();
            launcher?.Hide();
        }

        internal bool CloseTools()
        {
            if(closingTools)return true;
            closingTools=true;
            try
            {
                foreach(var tool in toolWindows.Values.ToArray())
                {
                    tool.Close();
                    if(toolWindows.ContainsValue(tool))return false;
                }
                return true;
            }
            finally {closingTools=false;}
        }

        public App()
        {
            // Generated WPF Main constructs App before loading any XAML or MainWindow.
            // Register here so the single EXE can resolve its bundled libraries in memory.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
        }

        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            lock (EmbeddedAssemblies)
            {
                if (EmbeddedAssemblies.TryGetValue(name, out var assembly)) return assembly;
                using (var resource = typeof(App).Assembly.GetManifestResourceStream("Seam.Dependencies." + name + ".dll.gz"))
                {
                    // Ignore framework/satellite requests; only bundled names belong to us.
                    if (resource == null) return null;
                    // GZip's footer gives the exact size of our bundled DLL. Fill one
                    // array instead of growing a MemoryStream and copying it again.
                    resource.Position = resource.Length - sizeof(int);
                    int length;
                    using (var footer = new BinaryReader(resource, System.Text.Encoding.UTF8, true)) length = footer.ReadInt32();
                    resource.Position = 0;
                    var bytes = new byte[length];
                    using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
                    {
                        int offset = 0;
                        while (offset < bytes.Length)
                        {
                            int count = gzip.Read(bytes, offset, bytes.Length - offset);
                            if (count == 0) throw new InvalidDataException("Incomplete bundled assembly: " + name);
                            offset += count;
                        }
                        if (gzip.ReadByte() != -1) throw new InvalidDataException("Invalid bundled assembly size: " + name);
                    }
                    assembly = Assembly.Load(bytes);
                    EmbeddedAssemblies.Add(name, assembly);
                    return assembly;
                }
            }
        }

        public static string ThirdPartyNotices
        {
            get
            {
                return ReadEmbeddedText("Seam.ThirdParty.txt") + "\n\n" + ReadEmbeddedText("Seam.CodeWalker-Notice.txt");
            }
        }

        private static string ReadEmbeddedText(string name)
        {
            using (var stream = typeof(App).Assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (sender, args) =>
            {
                MessageBox.Show(args.Exception.Message, "Seam", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var loading = new StartupWindow();
            loading.Show();
            try
            {
                // Let WPF paint the loading indicator before loading the codecs.
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Run(() =>
                {
                    Assembly.Load("SharpDX");
                    Assembly.Load("SharpDX.Mathematics");
                    Assembly.Load("CodeWalker.Core");
                });
                if (GameInstallation.NeedsSetup)
                {
                    loading.Hide();
                    GameSetupWindow.Configure(null);
                    if (GameInstallation.NeedsSetup) { loading.Close(); Shutdown(); return; }
                    loading.Show();
                }
                directMode = e.Args.Contains("--maps",StringComparer.OrdinalIgnoreCase) || e.Args.Any(p=>File.Exists(p)||Directory.Exists(p));
                if(directMode) OpenMapMerger();
                else if(e.Args.Contains("--clothing",StringComparer.OrdinalIgnoreCase)) OpenClothingStudio();
                else ShowLauncher();
                var window = MainWindow;
                EventHandler ready = null;
                ready = (sender, args) => { window.ContentRendered -= ready; loading.Close(); };
                window.ContentRendered += ready;
            }
            catch (Exception error)
            {
                loading.Close();
                MessageBox.Show(error.Message, "Seam — could not start", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }
}
