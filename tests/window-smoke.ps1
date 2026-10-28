param([string]$Executable = "$PSScriptRoot\..\dist\Seam.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowProbe {
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
}
'@
[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($Executable)) | Out-Null
$app = New-Object Seam.App
$app.InitializeComponent()
$window = New-Object Seam.MainWindow
$app.MainWindow = $window
function Pump { [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle) }
function Assert($condition, $message) { if (!$condition) { throw "FAIL: $message" }; Write-Host "PASS: $message" }
function Hit($point) {
  $screen = $window.PointToScreen($point)
  $location = [IntPtr](([int]$screen.X -band 0xffff) -bor (([int]$screen.Y -band 0xffff) -shl 16))
  return [WindowProbe]::SendMessage($handle, 0x84, [IntPtr]::Zero, $location).ToInt32()
}
function Click($name) { $button = $window.FindName($name); $button.RaiseEvent((New-Object Windows.RoutedEventArgs([Windows.Controls.Button]::ClickEvent))); Pump }
try {
  $window.Show(); Pump
  $handle = (New-Object Windows.Interop.WindowInteropHelper($window)).Handle
  $bounds = New-Object WindowProbe+Rect
  [WindowProbe]::GetWindowRect($handle, [ref]$bounds) | Out-Null
  $work = [Windows.Forms.Screen]::FromHandle($handle).WorkingArea
  Assert ($bounds.Top -ge $work.Top -and $bounds.Left -ge $work.Left -and $bounds.Right -le $work.Right -and $bounds.Bottom -le $work.Bottom) 'Initial window fits the monitor work area'
  Assert ((Hit (New-Object Windows.Point(500,40))) -eq 2) 'Branded header reports native HTCAPTION for drag/snap/double-click'
  $projectControls = $window.FindName('ProjectControls')
  $projectPoint = $projectControls.TranslatePoint((New-Object Windows.Point(20,20)), $window)
  Assert ((Hit $projectPoint) -eq 1) 'Header project controls remain interactive HTCLIENT'
  $minimize = $window.FindName('MinimizeButton')
  $minPoint = $minimize.TranslatePoint((New-Object Windows.Point(20,20)), $window)
  Assert ((Hit $minPoint) -eq 1) 'Caption buttons remain interactive HTCLIENT'
  Assert ((Hit (New-Object Windows.Point(1,140))) -eq 10) 'Window edge reports native resize hit target'
  Click 'MaximizeButton'
  Assert ($window.WindowState -eq [Windows.WindowState]::Maximized) 'Maximize control works'
  Assert ($window.FindName('MaximizeButton').ToolTip -eq 'Restore') 'Maximize control changes to Restore'
  $contentTop = $window.FindName('CloseButton').PointToScreen((New-Object Windows.Point(0,0)))
  $layout = $window.FindName('WindowLayout')
  $contentBottom = $layout.PointToScreen((New-Object Windows.Point(0,$layout.ActualHeight)))
  Assert ($contentTop.Y -ge $work.Top -and $contentBottom.Y -le $work.Bottom) 'Maximized content and controls fit above the taskbar'
  Click 'MaximizeButton'
  Assert ($window.WindowState -eq [Windows.WindowState]::Normal) 'Restore control works'
  $caption = $window.PointToScreen((New-Object Windows.Point(500,40)))
  $captionLocation = [IntPtr](([int]$caption.X -band 0xffff) -bor (([int]$caption.Y -band 0xffff) -shl 16))
  [WindowProbe]::SendMessage($handle, 0xA3, [IntPtr]2, $captionLocation) | Out-Null
  Pump
  Assert ($window.WindowState -eq [Windows.WindowState]::Maximized) 'Native caption double-click maximizes the window'
  Click 'MaximizeButton'
  Click 'MinimizeButton'
  Assert ($window.WindowState -eq [Windows.WindowState]::Minimized) 'Minimize control works'
  [Windows.SystemCommands]::RestoreWindow($window); Pump
  $window.UpdateLayout()
  $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]$window.ActualWidth,[int]$window.ActualHeight,96,96,[Windows.Media.PixelFormats]::Pbgra32)
  $bitmap.Render($window)
  $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
  $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
  $output = [IO.Path]::GetFullPath('artifacts\seam-window-controls.png')
  $stream = [IO.File]::Create($output)
  try { $encoder.Save($stream) } finally { $stream.Dispose() }
  Write-Host "Screenshot: $output"
  Copy-Item -LiteralPath $output -Destination 'artifacts\seam-empty.png' -Force
  $window.Width = 1080; $window.Height = 700; Pump; $window.UpdateLayout()
  $viewport = $window.FindName('Viewport')
  Assert ($viewport.ActualWidth -ge 400 -and $viewport.ActualHeight -ge 350) 'Compact window retains a usable viewport'
  $bar = $window.FindName('ActionBar')
  $stats = $window.FindName('WorkspaceStats')
  $imports = $bar.Children[0]
  $actions = $bar.Children[2]
  $statsStart = $stats.TranslatePoint((New-Object Windows.Point(0,0)), $bar).X
  $statsEnd = $statsStart + $stats.ActualWidth
  $importEnd = $imports.TranslatePoint((New-Object Windows.Point($imports.ActualWidth,0)), $bar).X
  $actionsStart = $actions.TranslatePoint((New-Object Windows.Point(0,0)), $bar).X
  Assert ($statsStart -ge $importEnd -and $actionsStart -ge $statsEnd) 'Import controls, counts and merge actions do not overlap at compact size'
  $welcome = $window.FindName('Welcome')
  $welcomeContent = $welcome.Children[0]
  $welcomeTop = $welcomeContent.TranslatePoint((New-Object Windows.Point(0,0)), $welcome).Y
  Assert ($welcomeTop -ge 0 -and $welcomeTop + $welcomeContent.ActualHeight -le $welcome.ActualHeight) 'Empty-state artwork and actions fit inside the compact viewport'
  $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]$window.ActualWidth,[int]$window.ActualHeight,96,96,[Windows.Media.PixelFormats]::Pbgra32)
  $bitmap.Render($window)
  $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
  $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
  $stream = [IO.File]::Create([IO.Path]::GetFullPath('artifacts\seam-compact.png'))
  try { $encoder.Save($stream) } finally { $stream.Dispose() }
  Click 'CloseButton'
  Assert (!$window.IsVisible) 'Close control works'
} finally {
  if ($window.IsVisible) { $window.Close() }
  $app.Shutdown()
}
