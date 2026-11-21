param([switch]$NativeMagnifier, [switch]$NativeScreen)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourceDir = Join-Path $projectRoot 'windows\RemoteController'
$webDir = Join-Path $projectRoot 'web'
$webResources = @('index.html','app.css','app.js','viewport.js','keyboard.js','gestures.js','screen.js','manifest.webmanifest','icon.svg','apple-touch-icon-v2.png','remote-icon-v2.png','locales.json') | ForEach-Object { "/resource:$webDir\$_,Web.$_" }
& $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$outputDir\BrowserPreview.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /win32manifest:"$sourceDir\app.manifest" @webResources "$sourceDir\UiText.cs" "$sourceDir\ScreenCapture.cs" "$sourceDir\LocalTlsIdentity.cs" "$sourceDir\Magnifier.cs" "$sourceDir\Protocol.cs" "$sourceDir\WebGateway.cs" "$sourceDir\PairingStore.cs" "$projectRoot\tests\BrowserPreview.cs" "$projectRoot\tests\PreviewScreen.cs"
if ($LASTEXITCODE -ne 0) { throw 'Preview build failed.' }
$previewArguments = @(); if ($NativeMagnifier) { $previewArguments += "--magnifier" }; if ($NativeScreen) { $previewArguments += "--screen" }; & "$outputDir\BrowserPreview.exe" @previewArguments
