# Public application CLI regression: smoke must opt into isolation before reading stores.
param(
    [Parameter(Mandatory=$true)][string]$AppPath,
    [Parameter(Mandatory=$true)][string]$RunRoot,
    [string]$Today = '2026-09-21',
    [string]$DotnetRoot
)
if ($DotnetRoot) { $env:DOTNET_ROOT = $DotnetRoot; $env:DOTNET_ROOT_X64 = $DotnetRoot }
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $RunRoot) { throw 'Use a fresh test root.' }
$null = New-Item -ItemType Directory -Path $RunRoot
$real = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TJDesktopTimetable'
function Fingerprint {
    $rows = @(if (Test-Path -LiteralPath $real) {
        Get-ChildItem -LiteralPath $real -File -Recurse | Sort-Object FullName | ForEach-Object {
            $_.FullName.Substring($real.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    return ($rows -join "`n")
}
$before = Fingerprint
try {
    . (Join-Path $PSScriptRoot 'Process.ps1')
    $appArgs = @('--smoke', '--diagnostic-dir', (Join-Path $RunRoot 'data'), '--today', $Today, '--no-desktop-layer', '--no-backdrop', '--log', (Join-Path $RunRoot 'app.log'))
    $null = Invoke-OwnedProcess -FilePath $AppPath -ArgumentList $appArgs -WorkingDirectory $RunRoot -TimeoutSeconds 90 -LogPath (Join-Path $RunRoot 'smoke')
    $log = [IO.File]::ReadAllText((Join-Path $RunRoot 'app.log'))
    if (-not $log.Contains('[smoke] ok')) { throw 'No successful smoke observation.' }
    if (-not (Test-Path -LiteralPath (Join-Path $RunRoot 'data\.tjt-session-check'))) { throw 'No isolation marker.' }
    Write-Host 'PASS isolated smoke CLI'
} finally {
    if ($before -cne (Fingerprint)) { throw 'Real application data changed. Never restore user data automatically.' }
}
