param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $packageRoot 'build' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 编译器不可用。' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sourceRoot = Join-Path $packageRoot 'source'
$iconPath = Join-Path $sourceRoot 'ScreenPrivacy.ico'
if (-not (Test-Path -LiteralPath $iconPath)) { throw '缺少应用图标。' }
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
$outputPath = Join-Path $OutputDirectory 'ScreenPrivacy.exe'
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 "/out:$outputPath" "/win32icon:$iconPath" "/win32manifest:$sourceRoot\app.manifest" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll $sourceFiles
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Copy-Item -LiteralPath $outputPath -Destination (Join-Path $OutputDirectory 'RestoreSecondary.exe') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'ScreenPrivacy.exe.config') -Destination (Join-Path $OutputDirectory 'ScreenPrivacy.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'ScreenPrivacy.exe.config') -Destination (Join-Path $OutputDirectory 'RestoreSecondary.exe.config') -Force
Get-FileHash -LiteralPath $outputPath -Algorithm SHA256
