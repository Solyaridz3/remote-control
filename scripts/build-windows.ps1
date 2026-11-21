param([switch]$Test, [switch]$Store, [uri]$PrivacyPolicyUrl)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDir = Join-Path $projectRoot $(if ($Store) { 'build\store-binary' } else { 'dist' })
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.x compiler not found.' }
$sourceDir = Join-Path $projectRoot 'windows\RemoteController'
$iconPath = Join-Path $sourceDir 'RemoteController.ico'
$pairingSource = Join-Path $sourceDir 'PairingStore.cs'
$webDir = Join-Path $projectRoot 'web'
$webResources = @('index.html','app.css','app.js','viewport.js','keyboard.js','gestures.js','screen.js','manifest.webmanifest','icon.svg','apple-touch-icon-v2.png','remote-icon-v2.png','locales.json') | ForEach-Object { "/resource:$webDir\$_,Web.$_" }
$storeFlags = @()
if ($Store) {
    if (!$PrivacyPolicyUrl -or !$PrivacyPolicyUrl.IsAbsoluteUri -or $PrivacyPolicyUrl.Scheme -ne 'https' -or $PrivacyPolicyUrl.IsLoopback -or $PrivacyPolicyUrl.UserInfo) {
        throw 'Specify a public HTTPS privacy-policy URL for the Store build.'
    }
    $privacyResource = Join-Path $outputDir 'privacy-url.txt'
    [IO.File]::WriteAllText($privacyResource, $PrivacyPolicyUrl.AbsoluteUri)
    $storeFlags = @('/define:STORE', "/resource:$privacyResource,Release.privacy-url")
}
& $compilerPath /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$outputDir\RemoteController.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /win32manifest:"$sourceDir\app.manifest" "/win32icon:$iconPath" "/resource:$iconPath,RemoteController.RemoteController.ico" @storeFlags @webResources "$sourceDir\AssemblyInfo.cs" "$sourceDir\UiText.cs" "$sourceDir\StoreRelease.cs" "$sourceDir\DesktopTheme.cs" "$sourceDir\ScreenCapture.cs" "$sourceDir\LocalTlsIdentity.cs" "$sourceDir\Magnifier.cs" "$sourceDir\Program.cs" "$sourceDir\Protocol.cs" "$sourceDir\WebGateway.cs" "$sourceDir\WindowsInput.cs" "$sourceDir\TouchpadPinch.cs" $pairingSource
if ($LASTEXITCODE -ne 0) { throw 'Windows build failed.' }
Write-Host "Built: $outputDir\RemoteController.exe"
if ($Test) {
    & $compilerPath /nologo /target:exe /platform:x64 /codepage:65001 "/out:$outputDir\ProtocolTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Web.Extensions.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /win32manifest:"$sourceDir\app.manifest" @webResources "$sourceDir\UiText.cs" "$sourceDir\Protocol.cs" "$sourceDir\WebGateway.cs" "$sourceDir\TouchpadPinch.cs" "$sourceDir\WindowsInput.cs" $pairingSource "$projectRoot\tests\ProtocolTests.cs" "$projectRoot\tests\WebTests.cs" "$projectRoot\tests\PinchTests.cs" "$projectRoot\tests\PairingTests.cs" "$projectRoot\tests\KeyboardTests.cs" "$sourceDir\ScreenCapture.cs" "$sourceDir\LocalTlsIdentity.cs" "$sourceDir\Magnifier.cs" "$projectRoot\tests\MagnifierTests.cs" "$projectRoot\tests\DragTests.cs" "$projectRoot\tests\ScreenTests.cs" "$projectRoot\tests\TlsTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & "$outputDir\ProtocolTests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    & node --test "$projectRoot\tests\gestures.test.cjs" "$projectRoot\tests\browser-client.test.cjs" "$projectRoot\tests\viewport.test.cjs" "$projectRoot\tests\keyboard.test.cjs" "$projectRoot\tests\screen.test.cjs"
    if ($LASTEXITCODE -ne 0) { throw 'Browser client tests failed.' }
}
