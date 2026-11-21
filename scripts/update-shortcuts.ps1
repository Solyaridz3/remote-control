$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appPath = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist\RemoteController.exe'))
if (-not (Test-Path -LiteralPath $appPath)) { throw 'Build RemoteController.exe first.' }
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcutLocations = @(
    @{ Path = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PocketPointer.lnk'; LegacyName = 'RemoteController.exe — ярлык.lnk'; Arguments = '' },
    @{ Path = Join-Path ([Environment]::GetFolderPath('Startup')) 'PocketPointer.lnk'; LegacyName = 'RemoteController.lnk'; Arguments = '--minimized' }
)
foreach ($location in $shortcutLocations) {
    $shortcut = $shortcutShell.CreateShortcut($location.Path)
    if ((Test-Path -LiteralPath $location.Path) -and $shortcut.TargetPath -ne $appPath) { throw 'A different shortcut already uses the PocketPointer name.' }
    $shortcut.TargetPath = $appPath
    $shortcut.WorkingDirectory = Split-Path -Parent $appPath
    $shortcut.Arguments = $location.Arguments
    $shortcut.IconLocation = "$appPath,0"
    $shortcut.Description = 'PocketPointer — тачпад, клавиатура, лупа и трансляция экрана с iPhone'
    $shortcut.Save()
    $verified = $shortcutShell.CreateShortcut($location.Path)
    if ($verified.TargetPath -ne $appPath -or $verified.Arguments -ne $location.Arguments) { throw 'Shortcut verification failed.' }
}
foreach ($location in $shortcutLocations) {
    $legacyPath = Join-Path (Split-Path -Parent $location.Path) $location.LegacyName
    if (-not (Test-Path -LiteralPath $legacyPath)) { continue }
    $legacy = $shortcutShell.CreateShortcut($legacyPath)
    if ($legacy.TargetPath -eq $appPath -and $legacy.Arguments -eq $location.Arguments) {
        Remove-Item -LiteralPath $legacyPath
    }
}
Write-Host 'PocketPointer desktop and Windows startup shortcuts updated.'
