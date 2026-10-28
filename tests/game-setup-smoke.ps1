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
public static class GameSetupProbe
{
 public static void Run(string executable,string output)
 {
  Directory.CreateDirectory(output);Environment.SetEnvironmentVariable("SEAM_SETTINGS_PATH",Path.Combine(output,Guid.NewGuid().ToString("N")+".json"));
  var asm=Assembly.LoadFrom(executable);var app=(Application)Activator.CreateInstance(asm.GetType("Seam.App"));app.GetType().GetMethod("InitializeComponent").Invoke(app,null);
  var setting=asm.GetType("Seam.GameInstallation");setting.GetMethod("Reload").Invoke(null,null);int passed=0;bool chose=false,finished=false;Exception failure=null;
  Action<bool,string> check=(ok,message)=>{if(!ok)throw new Exception(message);passed++;Console.WriteLine("OK "+message);};
  var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};var deadline=DateTime.UtcNow.AddSeconds(30);
  timer.Tick+=(s,e)=>{try{
   var setup=app.Windows.Cast<Window>().FirstOrDefault(w=>w.GetType().Name=="GameSetupWindow");
   if(setup!=null&&setup.IsVisible&&!chose){
    check(true,"first run asks for an explicit GTA installation choice");Capture(setup,Path.Combine(output,"first-run.png"));
    var text=(TextBox)LogicalTreeHelper.FindLogicalNode(setup,"GameFolderPath");text.Text=Path.Combine(output,"missing-game");
    ((Button)LogicalTreeHelper.FindLogicalNode(setup,"UseGameFolder")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    check(setup.IsVisible&&!(bool)setting.GetProperty("Available").GetValue(null),"invalid paths leave setup open and previews disabled");
    chose=true;((Button)LogicalTreeHelper.FindLogicalNode(setup,"ContinueWithoutGame")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));return;
   }
   if(chose&&app.MainWindow!=null&&app.MainWindow.GetType().Name=="HubWindow"&&app.MainWindow.IsVisible&&!finished){
    finished=true;check(!(bool)setting.GetProperty("NeedsSetup").GetValue(null),"the continue-without-GTA choice is saved");
    app.GetType().GetMethod("OpenMapMerger").Invoke(app,null);var map=app.MainWindow;map.UpdateLayout();
    check(((FrameworkElement)map.FindName("Viewport")).Visibility==Visibility.Collapsed,"map 3D viewport is disabled without GTA");
    check(!((Button)map.FindName("FrameAllButton")).IsEnabled&&!((CheckBox)map.FindName("CollisionToggle")).IsEnabled,"map camera and collision preview controls are disabled");
    Capture(map,Path.Combine(output,"map-without-gta.png"));map.Close();
    app.GetType().GetMethod("OpenClothingStudio").Invoke(app,null);var clothes=app.MainWindow;clothes.UpdateLayout();
    check(((FrameworkElement)clothes.FindName("Preview")).Visibility==Visibility.Collapsed&&!((Button)clothes.FindName("TattooTab")).IsEnabled,"clothing preview and tattoo editor are disabled without GTA");
    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
    clothes.GetType().GetMethod("SwitchMode",flags).Invoke(clothes,new object[]{"tattoo"});clothes.GetType().GetMethod("NewTattoo",flags).Invoke(clothes,null);
    check((string)clothes.GetType().GetField("mode",flags).GetValue(clothes)=="wardrobe","disabled tattoo editor cannot be entered programmatically");
    check(((Button)clothes.FindName("ExportButton")).IsEnabled,"clothing pack creation remains enabled");
    var sources=(string[])asm.GetType("Seam.DemoFactory").GetMethod("Create").Invoke(null,new object[]{Path.Combine(output,"demo-"+Guid.NewGuid().ToString("N"))});
    clothes.GetType().GetMethod("SwitchMode",flags).Invoke(clothes,new object[]{"texture"});clothes.GetType().GetMethod("OpenYtd",flags).Invoke(clothes,new object[]{Directory.GetFiles(sources[0],"*.ytd",SearchOption.AllDirectories).First()});
    check(((ListBox)clothes.FindName("TextureList")).Items.Count>0&&((FrameworkElement)clothes.FindName("Artwork")).Visibility==Visibility.Visible,"native 2D texture editing works without GTA");
    Capture(clothes,Path.Combine(output,"textures-without-gta.png"));clothes.GetType().GetField("dirty",flags).SetValue(clothes,false);clothes.Close();timer.Stop();app.Shutdown();return;
   }
   if(DateTime.UtcNow>deadline)throw new TimeoutException("GTA setup checks timed out.");
  }catch(Exception ex){failure=ex;timer.Stop();app.Shutdown(1);}};
  timer.Start();app.Run();if(failure!=null)throw failure;check(chose&&finished,"first-run setup reaches usable desktop tools without GTA");Console.WriteLine("PASS: "+passed+" installation UI assertions");
 }
 static void Capture(Window w,string path){w.UpdateLayout();var content=(FrameworkElement)w.Content;var bitmap=new RenderTargetBitmap(Math.Max(1,(int)content.ActualWidth),Math.Max(1,(int)content.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);}
}
'@
$references=@([Windows.Application].Assembly.Location,[Windows.Media.Visual].Assembly.Location,[Windows.Threading.Dispatcher].Assembly.Location,[Windows.Markup.IXamlTypeResolver].Assembly.Location,'System.dll','System.Core.dll')
Add-Type -TypeDefinition $probe -ReferencedAssemblies $references
[GameSetupProbe]::Run((Resolve-Path -LiteralPath $ExePath).Path,[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\installation-ui')))
