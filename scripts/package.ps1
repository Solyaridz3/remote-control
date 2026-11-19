param([switch]$IncludeNativeIOS)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $projectRoot 'dist'
$executablePath = Join-Path $outputDir 'RemoteController.exe'
if (-not (Test-Path -LiteralPath $executablePath)) { throw 'Build the Windows application first.' }
Compress-Archive -LiteralPath $executablePath,(Join-Path $projectRoot 'README.md') -DestinationPath (Join-Path $outputDir 'RemoteController-Windows.zip') -Force
if ($IncludeNativeIOS) {
    Compress-Archive -LiteralPath (Join-Path $projectRoot 'ios'),(Join-Path $projectRoot 'README.md') -DestinationPath (Join-Path $outputDir 'RemoteController-iOS-source.zip') -Force
}
Write-Host "Packages written to $outputDir"
