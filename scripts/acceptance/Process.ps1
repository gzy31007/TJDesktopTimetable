#requires -Version 5
<#
Owned process runner.

Dot-source this file to get Invoke-OwnedProcess; run it with -File to get the
standalone CLI. Dot-sourcing never starts anything.

    . .\scripts\acceptance\Process.ps1
    Invoke-OwnedProcess -FilePath <exe> -ArgumentList <string[]> `
        -WorkingDirectory <dir> -TimeoutSeconds 120 -LogPath <prefix>

CLI:

    powershell -NoProfile -ExecutionPolicy Bypass -File Process.ps1 `
        -FilePath <exe> -ArgsJsonPath <args.json> [-WorkingDirectory <dir>] `
        [-TimeoutSeconds 120] [-LogPath <prefix>] [-Hidden]

Exit codes: 0 success, 1 child nonzero, 2 start failure, 3 timeout,
130 Ctrl+C, 4 usage error.
#>

if (-not ('OwnedProcess' -as [type])) {
    Add-Type -TypeDefinition ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'OwnedProcess.cs')))
}

function ConvertTo-QuotedArgument {
    param([string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($ch in $Value.ToCharArray()) {
        if ($ch -eq [char]'\') {
            $backslashes++
            continue
        }
        if ($ch -eq [char]'"') {
            if ($backslashes -gt 0) { [void]$builder.Append((New-Object System.String([char]'\', ($backslashes * 2 + 1)))) }
            else { [void]$builder.Append('\') }
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append((New-Object System.String([char]'\', $backslashes)))
            $backslashes = 0
        }
        [void]$builder.Append($ch)
    }
    if ($backslashes -gt 0) { [void]$builder.Append((New-Object System.String([char]'\', ($backslashes * 2)))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function ConvertTo-SignedExitCode {
    param([uint32]$Code)
    # Exit codes above 0x7FFFFFFF (0xC000013A, 0xC0000409, ...) must not be cast
    # with [int]: that throws in PowerShell 5 and would lose the whole result.
    return [BitConverter]::ToInt32([BitConverter]::GetBytes($Code), 0)
}

function ConvertTo-WindowsCommandLine {
    param([string]$FilePath, [string[]]$ArgumentList)
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append((ConvertTo-QuotedArgument $FilePath))
    foreach ($argument in $ArgumentList) {
        [void]$builder.Append(' ')
        [void]$builder.Append((ConvertTo-QuotedArgument $argument))
    }
    return $builder.ToString()
}

function New-OwnedFailure {
    param([string]$Message, $Result)
    $exception = New-Object System.InvalidOperationException $Message
    $exception.Data['OwnedProcessResult'] = $Result
    return $exception
}

function New-OwnedResult {
    param([hashtable]$Fields)
    return [pscustomobject]$Fields
}

function Write-OwnedLogLine {
    param([string]$Path, [string]$Line)
    $stamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss.fff')
    $text = "[$stamp] $Line" + [Environment]::NewLine
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($text)
    $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $stream = New-Object System.IO.FileStream($Path, [IO.FileMode]::Append, [IO.FileAccess]::Write, $share, 4096, [IO.FileOptions]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    } finally {
        $stream.Dispose()
    }
}

function Write-OwnedResultFile {
    param([string]$Prefix, $Result)
    $json = ConvertTo-Json -InputObject $Result -Depth 5
    [IO.File]::WriteAllText("$Prefix.json", $json, (New-Object System.Text.UTF8Encoding($false)))
}

function ConvertTo-ArgumentList {
    param($Value)
    if ($null -eq $Value) { return @() }
    $list = @()
    foreach ($item in @($Value)) {
        if ($item -isnot [string]) {
            throw (New-Object System.ArgumentException ("argument list must contain only strings, got " + $item.GetType().Name))
        }
        $list += $item
    }
    return $list
}

function Invoke-OwnedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = '',
        [int]$TimeoutSeconds = 120,
        [string]$LogPath = '',
        [int[]]$SuccessExitCodes = @(0),
        [switch]$Hidden
    )

    $ArgumentList = @(ConvertTo-ArgumentList $ArgumentList)
    if ($TimeoutSeconds -lt 1) {
        throw (New-Object System.ArgumentException 'TimeoutSeconds must be at least 1')
    }
    if (-not $FilePath) {
        throw (New-Object System.ArgumentException 'FilePath is required')
    }
    if (-not $WorkingDirectory) {
        # Default to the executable's directory: the runner itself may have been
        # started from a UNC path (WSL interop), which some children cannot use as
        # their current directory. A bare command name (resolved through PATH) keeps
        # the ambient directory.
        $executableDirectory = $null
        if (Test-Path -LiteralPath $FilePath -PathType Leaf) {
            $executableDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($FilePath))
        } elseif ([IO.Path]::IsPathRooted($FilePath)) {
            $executableDirectory = [IO.Path]::GetDirectoryName($FilePath)
        }
        if ($executableDirectory) { $WorkingDirectory = $executableDirectory }
    }
    if (-not $LogPath) {
        $LogPath = Join-Path ([IO.Path]::GetTempPath()) (([IO.Path]::GetFileNameWithoutExtension($FilePath)) + '-' + [Guid]::NewGuid().ToString('N'))
    }
    $LogPath = [IO.Path]::GetFullPath($LogPath)
    $logDirectory = [IO.Path]::GetDirectoryName($LogPath)
    if ($logDirectory -and -not (Test-Path -LiteralPath $logDirectory)) {
        New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    }

    $result = New-OwnedResult @{
        exitCode      = $null
        timedOut      = $false
        ctrlC         = $false
        startFailed   = $false
        error         = $null
        durationSeconds = 0
        processId     = $null
        logPath       = "$LogPath.log"
        commandLine   = $null
    }

    $logFile = "$LogPath.log"
        if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        $result.startFailed = $true
        $result.error = "CreateProcess failed for '$FilePath': file not found"
        Write-OwnedLogLine $logFile ("[runner] CreateProcess failed for '" + $FilePath + "': file not found (Win32 error 2)")
        Write-OwnedResultFile $LogPath $result
        throw (New-OwnedFailure $result.error $result)
    }

    $commandLine = ConvertTo-WindowsCommandLine -FilePath $FilePath -ArgumentList $ArgumentList

    $startError = ''
    $owned = [OwnedProcess]::Start($commandLine, $WorkingDirectory, $logFile, [bool]$Hidden, [ref]$startError)
    if ($null -eq $owned) {
        $result.startFailed = $true
        $result.error = "CreateProcess failed for '$FilePath': $startError"
        Write-OwnedLogLine $logFile $result.error
        Write-OwnedResultFile $LogPath $result
        throw (New-OwnedFailure $result.error $result)
    }

    $result.commandLine = $commandLine
    $result.processId = $owned.ProcessId
    Write-OwnedLogLine $logFile ("[runner] launched pid " + $owned.ProcessId + ": " + $commandLine)
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    [uint32]$exitCode = 0
    $timedOut = $false
    $ctrlC = $false
    try {
        $exited = $owned.WaitForExit([uint32]($TimeoutSeconds * 1000), [ref]$exitCode)
        $ctrlC = [bool]$owned.CtrlC
        if (-not $exited) {
            $timedOut = $true
            Write-OwnedLogLine $logFile ("[runner] timed out after " + $TimeoutSeconds + "s; terminating the whole job")
            $owned.Terminate()
            $null = $owned.WaitForExit([uint32]5000, [ref]$exitCode)
        }
    } finally {
        $owned.Dispose()
        $stopwatch.Stop()
    }
    $result.exitCode = ConvertTo-SignedExitCode $exitCode
    $result.timedOut = $timedOut
    if ($ctrlC) { $result.exitCode = 130 }
    $result.ctrlC = $ctrlC
    $result.durationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
    if ($ctrlC) {
        Write-OwnedLogLine $logFile ("[runner] ctrl+c received; job terminated after " + $result.durationSeconds + "s")
    } elseif ($timedOut) {
        Write-OwnedLogLine $logFile ("[runner] timed out after " + $result.durationSeconds + "s (exit code " + $result.exitCode + " after termination)")
    } else {
        Write-OwnedLogLine $logFile ("[runner] exit code " + $result.exitCode + " after " + $result.durationSeconds + "s")
    }
    Write-OwnedResultFile $LogPath $result
    if ($ctrlC) {
        $message = "ctrl+c received; the owned job was terminated; log: " + $logFile
        Write-OwnedLogLine $logFile ("[runner] FAILED: " + $message)
        throw (New-OwnedFailure $message $result)
    }
    if ($timedOut) {
        $message = "child timed out after " + $TimeoutSeconds + "s and its job was terminated; log: " + $logFile
        Write-OwnedLogLine $logFile ("[runner] FAILED: " + $message)
        throw (New-OwnedFailure $message $result)
    }
    if ($SuccessExitCodes -notcontains $result.exitCode) {
        $message = "child exited with code " + $result.exitCode + " (expected one of: " + ($SuccessExitCodes -join ', ') + "); log: " + $logFile
        Write-OwnedLogLine $logFile ("[runner] FAILED: " + $message)
        throw (New-OwnedFailure $message $result)
    }
    return $result
}

# ---------------------------------------------------------------------------
# CLI seam. Parsed by hand so dot-sourcing leaves no parameters or variables in
# the caller's scope. $MyInvocation.InvocationName is '.' when dot-sourced.
# ---------------------------------------------------------------------------
function ConvertFrom-ArgsJsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "args json not found: $Path"
    }
    $text = [IO.File]::ReadAllText($Path)
    $parsed = ConvertFrom-Json -InputObject $text
    if ($null -eq $parsed) { return @() }
    if ($parsed -is [string]) { return @([string]$parsed) }
    $list = @()
    foreach ($item in @($parsed)) {
        if ($item -isnot [string]) { throw "args json must be an array of strings" }
        $list += [string]$item
    }
    return $list
}

function Invoke-OwnedProcessCli {
    param([string[]]$Arguments)

    $options = @{
        FilePath = $null
        ArgsJsonPath = $null
        WorkingDirectory = ''
        TimeoutSeconds = 120
        LogPath = ''
        Hidden = $false
    }
    $i = 0
    while ($i -lt $Arguments.Count) {
        $token = $Arguments[$i]
        $next = if ($i + 1 -lt $Arguments.Count) { $Arguments[$i + 1] } else { $null }
        switch ($token) {
            '-FilePath' { $options.FilePath = $next; $i += 2; continue }
            '-ArgsJsonPath' { $options.ArgsJsonPath = $next; $i += 2; continue }
            '-WorkingDirectory' { $options.WorkingDirectory = $next; $i += 2; continue }
            '-TimeoutSeconds' { $options.TimeoutSeconds = [int]$next; $i += 2; continue }
            '-LogPath' { $options.LogPath = $next; $i += 2; continue }
            '-Hidden' { $options.Hidden = $true; $i += 1; continue }
            default { throw "unknown argument: $token" }
        }
    }
    if ($null -eq $options.TimeoutSeconds -or $options.TimeoutSeconds -lt 1) { throw 'usage: -TimeoutSeconds must be at least 1' }
    if (-not $options.FilePath) { throw 'usage: Process.ps1 -FilePath <exe> -ArgsJsonPath <json> [-WorkingDirectory <dir>] [-TimeoutSeconds <n>] [-LogPath <prefix>] [-Hidden]' }

    $arguments = @()
    if ($options.ArgsJsonPath) { $arguments = @(ConvertFrom-ArgsJsonFile $options.ArgsJsonPath) }

    $invoke = @{
        FilePath = $options.FilePath
        ArgumentList = $arguments
        TimeoutSeconds = $options.TimeoutSeconds
    }
    if ($options.WorkingDirectory) { $invoke['WorkingDirectory'] = $options.WorkingDirectory }
    if ($options.LogPath) { $invoke['LogPath'] = $options.LogPath }
    if ($options.Hidden) { $invoke['Hidden'] = [switch]$true }

    try {
        Invoke-OwnedProcess @invoke | Out-Null
        return 0
    } catch {
        $record = $_.Exception.Data['OwnedProcessResult']
        if ($null -ne $record) {
            if ($record.startFailed) { return 2 }
            if ($record.timedOut) { return 3 }
            if ($record.ctrlC) { return 130 }
            return 1
        }
        Write-Host ("runner error: " + $_.Exception.Message)
        return 2
    }
}

$__ownedInvocation = $MyInvocation.InvocationName
if ($__ownedInvocation -ne '.') {
    try {
        $__ownedExit = Invoke-OwnedProcessCli -Arguments @($args)
    } catch {
        Write-Host ("usage error: " + $_.Exception.Message)
        $__ownedExit = 4
    }
    exit $__ownedExit
}
