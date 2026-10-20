# Public entry CLI checks; never execute a build or app against a hostile path.
param([Parameter(Mandatory=$true)][string]$RunRoot)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $RunRoot) { throw 'Use a fresh guard-test root.' }
$null = New-Item -ItemType Directory -Path $RunRoot
$entry = Join-Path $PSScriptRoot 'Invoke-Acceptance.ps1'
$child = Join-Path $RunRoot 'rejected-cache'
$real = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TJDesktopTimetable'
$ps = Join-Path $PSHOME 'powershell.exe'
$ErrorActionPreference = 'Continue'
& $ps -NoProfile -ExecutionPolicy Bypass -File $entry -RunRoot $child -SkipBuild -AppPath 'Z:\does-not-exist\Tjt.App.exe' -NugetPackages $real *> (Join-Path $RunRoot 'cache-guard.log')
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($code -eq 0) { throw 'Unsafe cache path was accepted.' }
if (Test-Path -LiteralPath $child) { throw 'Unsafe cache path was not rejected before creating run artifacts.' }
Write-Host 'PASS unsafe NuGet cache rejected before writes'
