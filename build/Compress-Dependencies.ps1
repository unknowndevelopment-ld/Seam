param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\lib') -Filter '*.dll' -File) {
    $source = [IO.File]::OpenRead($file.FullName)
    try {
        $destination = [IO.File]::Create((Join-Path $OutputDirectory ($file.Name+'.gz')))
        try {
            $gzip = New-Object IO.Compression.GZipStream($destination,[IO.Compression.CompressionLevel]::Optimal,$true)
            try { $source.CopyTo($gzip) } finally { $gzip.Dispose() }
        } finally { $destination.Dispose() }
    } finally { $source.Dispose() }
}
