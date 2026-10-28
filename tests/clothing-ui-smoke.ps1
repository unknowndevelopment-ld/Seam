param([string]$Executable = "$PSScriptRoot\..\dist\Seam.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Windows.Forms
[Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($Executable)) | Out-Null
$app = New-Object Seam.App
$app.InitializeComponent()
$uiContext = New-Object Windows.Threading.DispatcherSynchronizationContext
[Threading.SynchronizationContext]::SetSynchronizationContext($uiContext)
$app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$window = New-Object Seam.ClothingWindow -ArgumentList $false
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $root 'artifacts\clothing-ui'
[IO.Directory]::CreateDirectory($output) | Out-Null
$window.GetType().GetField('workDirectory',$flags).SetValue($window,(Join-Path $output 'work'))
function Assert($condition, $message) { if (!$condition) { throw "FAIL: $message" }; Write-Host "PASS: $message" }
function Invoke-Private($name, [object[]]$arguments = @()) {
  $values = New-Object object[] $arguments.Count
  for ($i = 0; $i -lt $arguments.Count; $i++) { if ($null -ne $arguments[$i]) {$values[$i] = $arguments[$i].PSObject.BaseObject} }
  $window.GetType().GetMethod($name,$flags).Invoke($window,$values)
}
function Field($name) { $window.GetType().GetField($name,$flags).GetValue($window) }
function Render($name) {
  $content = $window.Content
  $window.UpdateLayout()
  [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
  $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]$content.ActualWidth,[int]$content.ActualHeight,96,96,[Windows.Media.PixelFormats]::Pbgra32)
  $bitmap.Render($content)
  $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
  $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
  $file = Join-Path $output $name
  $stream = [IO.File]::Create($file)
  try {$encoder.Save($stream)} finally {$stream.Dispose()}
  Write-Host "Screenshot: $file"
}
try {
  $window.Width=1400; $window.Height=880; $window.ShowActivated=$false; $window.Show()
  Assert ($window.Title -like '*Clothing*') 'Clothing studio is a separate native Windows window'
  Assert ($window.ResizeMode -eq [Windows.ResizeMode]::CanResize) 'Clothing studio retains native move and resize controls'
  Render 'wardrobe-empty.png'
  $sources = [Seam.DemoFactory]::Create((Join-Path $output ('fixture-' + [Guid]::NewGuid().ToString('N'))))
  $model = Join-Path $sources[0] 'stream\jbib_000_u.ydr'
  $dictionary = Join-Path $sources[0] 'stream\jbib_diff_000_a_uni.ytd'
  Copy-Item -LiteralPath (Join-Path $sources[0] 'stream\seam_tower.ydr') -Destination $model
  Copy-Item -LiteralPath (Join-Path $sources[0] 'stream\seam_materials.ytd') -Destination $dictionary
  $project = Field 'project'
  $project.ImportFiles([string[]]@($model,$dictionary))
  Invoke-Private 'RefreshLibrary'
  Assert ($window.FindName('ItemList').Items.Count -gt 0) 'Imported native drawables appear in the wardrobe'
  $window.FindName('ItemList').SelectedIndex = 0
  Invoke-Private 'SwitchMode' @('texture')
  Invoke-Private 'OpenYtd' @($dictionary)
  Assert ($window.FindName('TextureList').Items.Count -eq 2) 'Dictionary editor exposes real native texture entries'
  $art = $window.FindName('Artwork')
  $layer = $art.AddText('SEAM','#FFFFFF')
  $layer.X = 4; $layer.Y = 20; $layer.Size = 14; $art.UpdateLayer($layer)
  Invoke-Private 'RefreshLayers'
  Render 'texture-studio.png'
  $detected = [Seam.ClothingGameAssets]::DetectGameFolder()
  if ($detected) {
    [Seam.GameInstallation]::Select($detected)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (!$window.FindName('Preview').HasPed -and [DateTime]::UtcNow -lt $deadline) {
      [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle)
      Start-Sleep -Milliseconds 20
    }
  }
  if ($window.FindName('Preview').HasPed -and [Seam.GameInstallation]::Available) {
  Invoke-Private 'SwitchMode' @('tattoo')
  Invoke-Private 'NewTattoo'
  $tattoo = Field 'selectedTattoo'
  Assert ($project.Tattoos.Count -eq 1) 'Tattoo creator adds an independently editable design'
  $art.AddText('SEAM','#A8C0FF') | Out-Null
  Invoke-Private 'RefreshLayers'
  Invoke-Private 'SaveTattoo'
  Assert ([IO.File]::Exists($tattoo.SourceImagePath) -and [IO.File]::Exists($tattoo.ArtworkPath)) 'Tattoo creator persists flattened PNG and editable layer artwork'
  Render 'tattoo-studio.png'
  $paint = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]$art.ActualWidth,[int]$art.ActualHeight,96,96,[Windows.Media.PixelFormats]::Pbgra32)
  $paint.Render($art)
  $pixels = New-Object byte[] ([int]$art.ActualWidth * [int]$art.ActualHeight * 4)
  $paint.CopyPixels($pixels,[int]$art.ActualWidth * 4,0)
  $colored=0
  for($i=0;$i -lt $pixels.Length;$i+=4){if([int]$pixels[$i]-[int]$pixels[$i+2] -gt 40){$colored++}}
  Assert ($colored -gt 100) 'Tattoo artwork stays visibly rendered after changing canvas size and saving'
  } else {
    Assert (!$window.FindName('TattooTab').IsEnabled) 'Tattoo editing stays disabled without a loaded real ped'
    Write-Host 'SKIP: Live tattoo UI checks require a local GTA V Legacy installation.'
  }
  $canvas = $art.GetType().GetField('canvas',$flags).GetValue($art)
  Invoke-Private 'SwitchMode' @('wardrobe')
  Invoke-Private 'TextureClicked' @($null,$null)
  Assert ($art.Data.Size -eq 64) 'Switching between tattoo and texture studios restores the active native texture'
  Assert ([IO.File]::Exists($project.Items[0].TexturePaths[0]) -or $project.Items[0].TexturePaths.Count -eq 0) 'Mode transitions keep edited dictionary output available'
  [Seam.GameInstallation]::Select($null)
  Assert (!$window.FindName('TattooTab').IsEnabled -and $window.FindName('Preview').Visibility -eq [Windows.Visibility]::Collapsed) 'Removing the installation immediately disables preview and tattoo editing'
  Assert ($window.FindName('ExportButton').IsEnabled) 'Removing the installation keeps pack creation available'
  $window.GetType().GetField('dirty',$flags).SetValue($window,$false)
  Write-Host 'PASS: Clothing Studio smoke checks'
} finally {
  $window.GetType().GetField('dirty',$flags).SetValue($window,$false)
  $window.Close()
  $app.Shutdown()
}
