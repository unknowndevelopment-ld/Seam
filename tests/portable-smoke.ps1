param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Seam.exe')
)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run with powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File tests\portable-smoke.ps1'
}
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,UIAutomationClient,UIAutomationTypes
$source = (Resolve-Path -LiteralPath $ExePath).Path
$testRoot = Join-Path $PSScriptRoot ('..\artifacts\portable checks\' + [Guid]::NewGuid().ToString('N'))
$portableFolder = Join-Path $testRoot 'Moved app with spaces'
New-Item -ItemType Directory -Force -Path $portableFolder | Out-Null
$portable = Join-Path $portableFolder 'Portable Seam.exe'
Copy-Item -LiteralPath $source -Destination $portable
$script:assertions = 0
function Assert([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $script:assertions++
    Write-Output ('OK ' + $message)
}
function Find-Control($root, [string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Has-Texture($model) {
    if ($model -is [Windows.Media.Media3D.GeometryModel3D]) {
        return $model.Material -is [Windows.Media.Media3D.DiffuseMaterial] -and
            $model.Material.Brush -is [Windows.Media.ImageBrush] -and
            $model.Material.Brush.ImageSource -is [Windows.Media.Imaging.BitmapSource]
    }
    if ($model -is [Windows.Media.Media3D.Model3DGroup]) {
        foreach ($child in $model.Children) { if (Has-Texture $child) { return $true } }
    }
    return $false
}

# A separate process verifies the real Windows entry point, without any sidecars.
$process = $null
try {
    Assert (@(Get-ChildItem -LiteralPath $portableFolder -File).Count -eq 1) 'portable folder contains only the renamed executable'
    $process = Start-Process -FilePath $portable -ArgumentList '--maps' -WorkingDirectory $portableFolder -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $demoButton = $null
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Try the demo')
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw ('Portable executable exited early: ' + $process.ExitCode) }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            try {
                $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
                $demoButton = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            } catch [System.Windows.Automation.ElementNotAvailableException] { $demoButton = $null }
        }
    } while ($null -eq $demoButton -and [DateTime]::UtcNow -lt $deadline)
    Assert ($process.MainWindowHandle -ne [IntPtr]::Zero) 'renamed standalone executable opens a Windows app window'
    Assert ($null -ne $demoButton) 'compiled WPF resources expose the demo action'
    $demoButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $count = Find-Control $root 'MapCount'
        $ready = $null -ne $count -and $count.Current.Name -eq '2'
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    Assert $ready 'actual executable imports both demo packs through its UI action'
    $summary = Find-Control $root 'SceneSummary'
    Assert ($null -ne $summary -and $summary.Current.Name -match '13 objects' -and $summary.Current.Name -notmatch 'proxies') 'standalone demo displays its 13 objects without missing drawable proxies'
    $autoMerge = Find-Control $root 'AutoMergeButton'
    Assert ($null -ne $autoMerge -and $autoMerge.Current.IsEnabled) 'standalone app exposes the automatic merge action'
    $autoMerge.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $outputCount = Find-Control $root 'OutputCount'
        $mergeState = Find-Control $root 'MergeState'
        $merged = $null -ne $outputCount -and $outputCount.Current.Name -eq '2' -and $null -ne $mergeState -and $mergeState.Current.Name -eq 'Merge complete'
    } while (-not $merged -and [DateTime]::UtcNow -lt $deadline)
    Assert $merged 'actual standalone UI prepares separate resource output without conflict choices'
    Assert ($null -eq (Find-Control $root 'KeepA') -and $null -eq (Find-Control $root 'KeepB') -and $null -eq (Find-Control $root 'KeepBoth')) 'standalone UI contains no keep/remove conflict controls'
}
finally {
    # Own only this test process. Its demo is unsaved, so direct termination avoids an unattended save prompt.
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}

# A fresh PowerShell AppDomain cannot see project output DLLs: only the copied EXE
# is loaded. Verify its in-memory dependencies and real decoded texture materials.
$assembly = [Reflection.Assembly]::LoadFrom($portable)
$app = [Activator]::CreateInstance($assembly.GetType('Seam.App', $true))
$app.InitializeComponent()
foreach ($name in @('CodeWalker.Core','SharpDX','SharpDX.Mathematics')) {
    $dependency = [Reflection.Assembly]::Load($name)
    Assert ($dependency.Location -eq '') ($name + ' loads from embedded bytes without a DLL file')
    $resource = $assembly.GetManifestResourceStream('Seam.Dependencies.'+$name+'.dll.gz')
    $gzip = New-Object IO.Compression.GZipStream($resource,[IO.Compression.CompressionMode]::Decompress)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $unpackedHash = [BitConverter]::ToString($sha.ComputeHash($gzip)).Replace('-','')
        $original = Join-Path $PSScriptRoot ('..\lib\'+$name+'.dll')
        Assert ($unpackedHash -eq (Get-FileHash -LiteralPath $original).Hash) ($name+' decompresses to the unmodified original DLL')
    } finally { $sha.Dispose(); $gzip.Dispose(); $resource.Dispose() }
}
$notices = $assembly.GetType('Seam.App').GetProperty('ThirdPartyNotices').GetValue($null, $null)
Assert ($notices -match 'dexyfex') 'third-party notices are retained inside the executable'
$paths = [Seam.MergeWorkspace]::CreateDemo((Join-Path $testRoot 'Procedural sources'))
$workspace = New-Object Seam.MergeWorkspace
$workspace.Import([string[]]$paths, $null)
Assert ($workspace.Packs.Count -eq 2 -and $workspace.Scene.Count -eq 13) 'embedded parsers produce the full native demo scene'
$textured = @($workspace.Scene | Where-Object { -not $_.IsCollision -and (Has-Texture $_.Model) })
Assert ($textured.Count -eq 9) 'all nine drawable models use decoded YTD texture images'
Assert (@(Get-ChildItem -LiteralPath $portableFolder -File).Count -eq 1) 'running and loading the app creates no DLL or config sidecars'
$app.Shutdown()
Write-Output ('PASS: ' + $script:assertions + ' portable executable assertions')
Write-Output ('Artifacts: ' + $testRoot)
