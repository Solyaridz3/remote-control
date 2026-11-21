[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{2,49}$')][string]$PackageName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Publisher,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$PublisherDisplayName,
    [Parameter(Mandatory)][uri]$PrivacyPolicyUrl,
    [version]$Version = '1.0.0.0',
    [version]$MaxVersionTested = '10.0.22000.0',
    [string]$MakeAppxPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($Version.Major -lt 1 -or $Version.Revision -ne 0 -or $Version.Build -lt 0 -or
    $Version.Major -gt 65535 -or $Version.Minor -gt 65535 -or $Version.Build -gt 65535) {
    throw 'Specify a four-part Store version with major version 1–65535 and revision 0.'
}
if ($MaxVersionTested -lt [version]'10.0.22000.0' -or $MaxVersionTested.Revision -lt 0) {
    throw 'Specify a four-part MaxVersionTested version of Windows 11.'
}
if ($Publisher -notmatch '^CN=.+' -or $PublisherDisplayName.Trim().Length -eq 0) {
    throw 'Use the Publisher and Publisher display name shown in Partner Center.'
}
if (!$MakeAppxPath) {
    $tool = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if ($tool) { $MakeAppxPath = $tool.Source }
    else {
        $sdkFolder = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $tool = Get-ChildItem -LiteralPath $sdkFolder -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        $MakeAppxPath = $tool
    }
}
if (!$MakeAppxPath -or !(Test-Path -LiteralPath $MakeAppxPath -PathType Leaf)) {
    throw 'MakeAppx.exe was not found. Install Windows SDK Build Tools or pass -MakeAppxPath.'
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'dist\store' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = [IO.Path]::GetFullPath((Join-Path $OutputDirectory "$PackageName-$Version-x64.msix"))
if (Test-Path -LiteralPath $output) { throw "Package already exists at $output. Use a new version or output directory." }

& "$PSScriptRoot\build-windows.ps1" -Store -PrivacyPolicyUrl $PrivacyPolicyUrl
$packageRoot = Join-Path $projectRoot ('build\store-layout-' + [guid]::NewGuid().ToString('N'))
$assets = Join-Path $packageRoot 'Assets'
New-Item -ItemType Directory -Path $assets | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'build\store-binary\RemoteController.exe') -Destination $packageRoot

$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ "/out:$packageRoot\RemoteController.Startup.exe" (Join-Path $projectRoot 'store\StartupLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the Store startup launcher.' }

Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Bitmap]::new((Join-Path $projectRoot 'web\remote-icon-v2.png'))
try {
    foreach ($logo in @(@{Name='StoreLogo'; Size=50}, @{Name='Square150x150Logo'; Size=150}, @{Name='Square44x44Logo'; Size=44})) {
        $size = $logo.Size
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($source, 0, 0, $size, $size)
            $bitmap.Save((Join-Path $assets ($logo.Name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
} finally { $source.Dispose() }

$manifest = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'store\AppxManifest.xml') -Raw -Encoding UTF8)
$manifest.Package.Identity.SetAttribute('Name', $PackageName)
$manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
$manifest.Package.Identity.SetAttribute('Version', $Version.ToString())
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$manifest.Package.Dependencies.TargetDeviceFamily.SetAttribute('MaxVersionTested', $MaxVersionTested.ToString())
$manifest.Save((Join-Path $packageRoot 'AppxManifest.xml'))
& $MakeAppxPath pack /d $packageRoot /p $output
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx rejected the package.' }
Write-Host "Store package: $output"
