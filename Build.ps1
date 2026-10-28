param([switch]$Check)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
Push-Location -LiteralPath $taskRoot
try {
    # Regenerate all WPF markup so the packaged EXE cannot reuse stale BAML.
    dotnet build app/Seam.csproj -c Release -t:Rebuild --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    if ($Check) {
        dotnet build tests/Seam.Checks.csproj -c Release --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw 'Check build failed.' }
        & (Join-Path $taskRoot 'tests\bin\Release\net48\Seam.Checks.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed.' }
    }
    $packagePath = Join-Path $taskRoot 'dist'
    New-Item -ItemType Directory -Force -Path $packagePath | Out-Null
    # All private libraries, XAML, the manifest, and notices are embedded in this file.
    Copy-Item -LiteralPath 'app\bin\Release\net48\Seam.exe' -Destination (Join-Path $packagePath 'Seam.exe') -Force
    Copy-Item -LiteralPath 'app\bin\Release\net48\Seam.exe' -Destination (Join-Path $taskRoot 'Seam.exe') -Force
    if ($Check) {
        & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -Command "& { & '.\tests\startup-smoke.ps1' }"
        if ($LASTEXITCODE -ne 0) { throw 'Startup checks failed.' }
        & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File 'tests\portable-smoke.ps1'
        if ($LASTEXITCODE -ne 0) { throw 'Portable executable checks failed.' }
        # Use -Command so startup does not mistake the test script for an imported asset.
        & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -Command "& { & '.\tests\window-smoke.ps1' }"
        if ($LASTEXITCODE -ne 0) { throw 'Window behavior checks failed.' }
        & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -Command "& { & '.\tests\automatic-ui-smoke.ps1' }"
        if ($LASTEXITCODE -ne 0) { throw 'Automatic merger UI checks failed.' }
        & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -Command "& { & '.\tests\clothing-ui-smoke.ps1' }"
        if ($LASTEXITCODE -ne 0) { throw 'Clothing Studio UI checks failed.' }
    }
    Write-Host "Ready: $taskRoot\Seam.exe (single executable)"
}
finally { Pop-Location }
