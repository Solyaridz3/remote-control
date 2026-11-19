$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appPath = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist\RemoteController.exe'))
if (-not (Test-Path -LiteralPath $appPath)) { throw 'Build RemoteController.exe first.' }
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcutLocations = @(
    @{ Path = Join-Path ([Environment]::GetFolderPath('Desktop')) 'RemoteController.exe — ярлык.lnk'; Arguments = '' },
    @{ Path = Join-Path ([Environment]::GetFolderPath('Startup')) 'RemoteController.lnk'; Arguments = '--minimized' }
)
foreach ($location in $shortcutLocations) {
    $shortcut = $shortcutShell.CreateShortcut($location.Path)
    $shortcut.TargetPath = $appPath
    $shortcut.WorkingDirectory = Split-Path -Parent $appPath
    $shortcut.Arguments = $location.Arguments
    $shortcut.IconLocation = "$appPath,0"
    $shortcut.Description = 'Remote — тачпад, клавиатура, лупа и трансляция экрана с iPhone'
    $shortcut.Save()
    $verified = $shortcutShell.CreateShortcut($location.Path)
    if ($verified.TargetPath -ne $appPath -or $verified.Arguments -ne $location.Arguments) { throw 'Shortcut verification failed.' }
}
Write-Host 'Desktop and Windows startup shortcuts updated.'
