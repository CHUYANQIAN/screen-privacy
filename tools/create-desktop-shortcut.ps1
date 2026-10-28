param([string]$ShortcutName = '屏幕遮蔽')
$ErrorActionPreference = 'Stop'
if ($ShortcutName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid shortcut name.' }
$packageRoot = Split-Path -Parent $PSScriptRoot
$appDirectory = Join-Path $packageRoot 'app'
$appPath = (Resolve-Path -LiteralPath (Join-Path $appDirectory 'ScreenPrivacy.exe')).Path
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) ($ShortcutName + '.lnk')
$shellObject = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $shortcutPath) {
    $previous = $shellObject.CreateShortcut($shortcutPath)
    if ($previous.TargetPath -ne $appPath) { throw 'An existing shortcut points elsewhere. Choose another ShortcutName; no shortcut was overwritten.' }
}
$shortcut = $shellObject.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $appPath
$shortcut.WorkingDirectory = $appDirectory
$shortcut.IconLocation = $appPath + ',0'
$shortcut.Description = '屏幕遮蔽；当前恢复快捷键请在面板确认'
$shortcut.Hotkey = ''
$shortcut.Save()
Write-Output $shortcutPath
