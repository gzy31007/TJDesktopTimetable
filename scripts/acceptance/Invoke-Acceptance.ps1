# Windows PowerShell 5.1. All machine paths are parameters, never repository constants.
[CmdletBinding()]
param(
    [ValidateSet('All','Smoke','Board','Update')][string]$Mode = 'All',
    [string]$SourceRoot,
    [string]$RunRoot = (Join-Path ([IO.Path]::GetTempPath()) ('tjt-acceptance-' + [Guid]::NewGuid().ToString('N'))),
    [string]$DotnetPath = 'dotnet',
    [string]$NugetPackages,
    [string]$AppPath,
    [switch]$SkipBuild,
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')][string]$Today = '2026-09-21',
    [ValidateRange(1,3600)][int]$BuildTimeoutSeconds = 900,
    [ValidateRange(1,600)][int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $SourceRoot) { $SourceRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$null = [DateTime]::ParseExact($Today, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
# Reject aliases and overlap before creating anything. Never overwrite or delete a prior run.
if ($RunRoot -notmatch '^[A-Za-z]:[\\/]' -or $RunRoot.Contains('~') -or
    @($RunRoot.Replace('/','\').Substring(3).Split('\') | Where-Object { $_.EndsWith('.') -or $_.EndsWith(' ') -or $_.Contains(':') }).Count) {
    throw 'RunRoot must be an unaliased absolute local Windows path.'
}
$RunRoot = [IO.Path]::GetFullPath($RunRoot).TrimEnd('\')
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot).TrimEnd('\')
$realData = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TJDesktopTimetable'
function IsWithin([string]$path, [string]$parent) {
    return $path.Equals($parent, [StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
if ((IsWithin $RunRoot $realData) -or (IsWithin $realData $RunRoot) -or
    (IsWithin $RunRoot (Join-Path $SourceRoot 'dotnet')) -or (IsWithin $SourceRoot $RunRoot)) {
    throw 'RunRoot must not overlap real application data or source inputs.'
}
for ($ancestor = New-Object IO.DirectoryInfo($RunRoot); $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'RunRoot ancestors must not be links or junctions.' }
}
if (Test-Path -LiteralPath $RunRoot) { throw 'RunRoot already exists; use a fresh run directory.' }
if ($NugetPackages) {
    if ($NugetPackages -notmatch '^[A-Za-z]:[\\/]' -or $NugetPackages.Contains('~') -or
        @($NugetPackages.Replace('/','\').Substring(3).Split('\') | Where-Object { $_.EndsWith('.') -or $_.EndsWith(' ') -or $_.Contains(':') }).Count) {
        throw 'NugetPackages must be an unaliased absolute local Windows path.'
    }
    $NugetPackages = [IO.Path]::GetFullPath($NugetPackages).TrimEnd('\')
    if ((IsWithin $NugetPackages $realData) -or (IsWithin $realData $NugetPackages)) { throw 'NugetPackages must not overlap real application data.' }
    for ($ancestor = New-Object IO.DirectoryInfo($NugetPackages); $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'NugetPackages ancestors must not be links or junctions.' }
    }
    # A pre-existing package subdirectory could itself be a link into real data.
    $pending = New-Object 'Collections.Generic.Stack[string]'
    if ([IO.Directory]::Exists($NugetPackages)) { $pending.Push($NugetPackages) }
    while ($pending.Count) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
            $attributes = [IO.File]::GetAttributes($entry)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'NugetPackages must not contain links or junctions.' }
            if ($attributes -band [IO.FileAttributes]::Directory) { $pending.Push($entry) }
        }
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot 'dotnet\TjtTimetable.Windows.slnx'))) { throw 'SourceRoot is not a checkout.' }
$null = New-Item -ItemType Directory -Path $RunRoot
$utf8 = New-Object Text.UTF8Encoding($false)
function WriteJson([string]$path, $value) { [IO.File]::WriteAllText($path, (ConvertTo-Json -InputObject $value -Depth 30), $utf8) }
function SnapshotRealData {
    $snapshot = @{}
    if (Test-Path -LiteralPath $realData) {
        Get-ChildItem -LiteralPath $realData -File -Recurse | ForEach-Object {
            $snapshot[$_.FullName.Substring($realData.Length)] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }
    return $snapshot
}
function Unchanged($before, $after) {
    if ($before.Count -ne $after.Count) { return $false }
    foreach ($key in $before.Keys) { if (-not $after.ContainsKey($key) -or $before[$key] -cne $after[$key]) { return $false } }
    return $true
}
$before = SnapshotRealData
$result = [ordered]@{ Success=$false; Stage='initializing'; RealDataUnchanged=$null; Mode=$Mode; Today=$Today }
WriteJson (Join-Path $RunRoot 'result.json') $result
$oldEnvironment = @{}
foreach ($name in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO','DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER')) {
    $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$server = $null
$failure = $null
try {
    . (Join-Path $PSScriptRoot 'Process.ps1')
    . (Join-Path $PSScriptRoot 'Assertions.ps1')
    $dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_CLI_HOME = Join-Path $RunRoot 'cli-home'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
    if ($NugetPackages) { $env:NUGET_PACKAGES = $NugetPackages }
    else { $env:NUGET_PACKAGES = Join-Path $RunRoot 'packages' }
    function Run([string]$file, [string[]]$arguments, [int]$seconds, [string]$tag) {
        $result.Stage = $tag
        WriteJson (Join-Path $RunRoot 'result.json') $result
        Invoke-OwnedProcess -FilePath $file -ArgumentList $arguments -WorkingDirectory $RunRoot -TimeoutSeconds $seconds -LogPath (Join-Path $RunRoot $tag)
    }
    if (-not $SkipBuild) {
        # Fresh destination only; no /MIR, no process-name discovery, no stale binaries.
        $mirror = Join-Path $RunRoot 'src\dotnet'
        $copyArgs = @((Join-Path $SourceRoot 'dotnet'), $mirror, '/E', '/XJ', '/XD', 'bin', 'obj', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1')
        # Robocopy 0..7 are successful copy states. Treat 8+ as failure.
        $result.Stage = 'copy'
        $copy = Invoke-OwnedProcess -FilePath (Join-Path $env:SystemRoot 'System32\robocopy.exe') -ArgumentList $copyArgs -WorkingDirectory $RunRoot -TimeoutSeconds $BuildTimeoutSeconds -LogPath (Join-Path $RunRoot 'copy') -SuccessExitCodes @(0,1,2,3,4,5,6,7)
        $null = Run $dotnet @('restore', (Join-Path $mirror 'TjtTimetable.Windows.slnx'), '-p:NuGetAudit=false', '--disable-parallel') $BuildTimeoutSeconds 'restore'
        $null = Run $dotnet @('build', (Join-Path $mirror 'TjtTimetable.Windows.slnx'), '-c', 'Release', '--no-restore', '--disable-build-servers', '-p:UseSharedCompilation=false') $BuildTimeoutSeconds 'build'
        $AppPath = Join-Path $mirror 'Tjt.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\Tjt.App.exe'
    } elseif (-not $AppPath) { throw 'SkipBuild requires an explicit AppPath; no implicit stale executable.' }
    $AppPath = (Get-Item -LiteralPath $AppPath -ErrorAction Stop).FullName
    $modes = if ($Mode -eq 'All') { @('Smoke','Board','Update') } else { @($Mode) }
    foreach ($session in $modes) {
        $dir = Join-Path $RunRoot $session.ToLowerInvariant()
        $null = New-Item -ItemType Directory -Path $dir
        $args = @('--diagnostic-dir', (Join-Path $dir 'data'), '--today', $Today, '--no-desktop-layer', '--no-backdrop', '--log', (Join-Path $dir 'app.log'))
        if ($session -eq 'Smoke') {
            $args += '--smoke'
        } else {
            $plan = [IO.File]::ReadAllText((Join-Path $PSScriptRoot ('plans\' + $session.ToLowerInvariant() + '.json'))) | ConvertFrom-Json
            if ($session -eq 'Board') {
                $fixture = Join-Path $SourceRoot 'dotnet\fixtures\board-content-synthetic.json'
                $short = [IO.File]::ReadAllText($fixture) | ConvertFrom-Json
                $short.courses = @($short.courses[1])
                WriteJson (Join-Path $dir 'short.json') $short
                foreach ($step in $plan) {
                    if ($step.PSObject.Properties['Input']) {
                        $step.Input = if ($step.Input -eq '@fixture') { $fixture } else { Join-Path $dir 'short.json' }
                    }
                }
                $args += @('--now', '07:00')
            } else {
                Add-Type -Path (Join-Path $PSScriptRoot 'UpdateServer.cs')
                $responses = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'plans\updates.json')) | ConvertFrom-Json
                $bodies = [string[]]@($responses | ForEach-Object { ConvertTo-Json -InputObject $_.body -Depth 10 -Compress })
                $statuses = [int[]]@($responses | ForEach-Object { $_.status })
                $server = New-Object Tjt.Acceptance.UpdateServer($bodies, $statuses)
                $args += @('--now', '08:22', '--size', '320x260', '--dark', '--update-api', ('http://127.0.0.1:' + $server.Port + '/releases/latest'))
            }
            WriteJson (Join-Path $dir 'plan.json') @($plan)
            $args += @('--session-check', (Join-Path $dir 'plan.json'), '--sjtu-host', 'http://127.0.0.1')
        }
        $null = Run $AppPath $args $TimeoutSeconds $session.ToLowerInvariant()
        if ($session -eq 'Smoke') {
            Require ([IO.File]::ReadAllText((Join-Path $dir 'app.log')).Contains('[smoke] ok')) 'Missing smoke success observation.'
        } else {
            $observations = [IO.File]::ReadAllText((Join-Path $dir 'data\session.json')) | ConvertFrom-Json
            AssertSession $observations $plan
            if ($session -eq 'Board') { AssertBoard $observations }
            else {
                AssertUpdate $observations
                Require ($server.RequestCount -eq 7 -and -not $server.Error) 'Unexpected update API requests or server failure.'
                WriteJson (Join-Path $dir 'api.json') @{ RequestCount=$server.RequestCount }
                $server.Dispose(); $server = $null
            }
        }
    }
    $result.Stage = 'complete'
    $result.Success = $true
} catch {
    $failure = $_
    $result.Success = $false
} finally {
    try { if ($server) { $server.Dispose() } } catch { $failure = $_; $result.Success = $false }
    try {
        $result.RealDataUnchanged = Unchanged $before (SnapshotRealData)
        if (-not $result.RealDataUnchanged) { $result.Success = $false; throw 'Real application data changed; not automatically restored.' }
    } catch { $failure = $_; $result.Success = $false }
    foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
    WriteJson (Join-Path $RunRoot 'result.json') $result
}
Write-Host ('RUN_ROOT=' + $RunRoot)
if ($failure) { throw $failure }
if (-not $result.Success) { throw 'Acceptance did not complete.' }
Write-Host 'PASS acceptance; real application data unchanged.'
