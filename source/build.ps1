param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '..\tools\build.ps1') @PSBoundParameters
