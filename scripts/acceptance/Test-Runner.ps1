param(
    [string]$RunRoot = '',
    [string]$Filter = '*'
)

# Acceptance runner test suite. No Pester: every test starts real processes and
# checks the public CLI seam (Process.ps1) plus the module seam (dot-source).
# Keep this file pure ASCII: Windows PowerShell 5 reads it as ANSI.

$ErrorActionPreference = 'Stop'
$script:AcceptanceDir = $PSScriptRoot
$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:ProcessScript = Join-Path $PSScriptRoot 'Process.ps1'
$script:PowerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:Failures = 0
$script:Results = @()

if (-not $RunRoot) {
    $RunRoot = Join-Path $env:TEMP ('tjt-acceptance-' + [Guid]::NewGuid().ToString('N'))
}
# Never reuse a run root: stale pids/guard files from an earlier run could make a
# test clean up processes it did not create. An existing dir is only tolerated
# when it is empty (CI jobs sometimes pre-create the directory).
if (Test-Path -LiteralPath $RunRoot) {
    $existing = @(Get-ChildItem -LiteralPath $RunRoot -Force -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) {
        throw "RunRoot is not fresh: '$RunRoot'. Pass a new path so old artifacts and pid files cannot leak into this run."
    }
} else {
    New-Item -ItemType Directory -Path $RunRoot | Out-Null
}
$script:RunRoot = (Resolve-Path -LiteralPath $RunRoot).Path
$script:BinDir = Join-Path $script:RunRoot 'bin'
New-Item -ItemType Directory -Force -Path $script:BinDir | Out-Null
$script:ProbeExe = Join-Path $script:BinDir ('AcceptanceProbe-' + $PID + '.exe')

if (-not ('ConsoleGuard' -as [type])) {
    Add-Type -TypeDefinition (Get-Content -Raw (Join-Path $PSScriptRoot 'fixtures\ConsoleGuard.cs'))
}
if (-not ('Acceptance.Native' -as [type])) {
    Add-Type -Namespace Acceptance -Name Native -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr GetConsoleWindow();
[DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
[DllImport("kernel32.dll", SetLastError=true)] public static extern bool AllocConsole();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
'@
}

# Control events can only reach a real console. Under a pseudoconsole (WSL
# interop, some CI hosts) console inheritance breaks, so take a real one when
# there is no console window yet, and hide it.
if ([Acceptance.Native]::GetConsoleWindow() -eq [IntPtr]::Zero) {
    [Acceptance.Native]::FreeConsole() | Out-Null
    if ([Acceptance.Native]::AllocConsole()) {
        [Acceptance.Native]::ShowWindow([Acceptance.Native]::GetConsoleWindow(), 0) | Out-Null
    }
}

Add-Type -TypeDefinition (Get-Content -Raw (Join-Path $PSScriptRoot 'fixtures\AcceptanceProbe.cs')) -OutputAssembly $script:ProbeExe -OutputType ConsoleApplication

function Quote-Argument([string]$value) {
    if ($value.Length -gt 0 -and $value -notmatch '[\s"]') { return $value }
    return '"' + ($value -replace '"', '\"') + '"'
}

function Write-ArgsJson([string]$path, [string[]]$values) {
    $parts = foreach ($value in $values) { ConvertTo-Json -InputObject ([string]$value) }
    $text = '[' + ($parts -join ',') + ']'
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Invoke-RunnerCli {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = '',
        [int]$TimeoutSeconds = 60,
        [string]$LogPrefix = '',
        [switch]$Hidden,
        [switch]$NoWait
    )
    $dir = Join-Path $script:RunRoot (Join-Path 'runs' $Name)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $argsJson = Join-Path $dir 'args.json'
    Write-ArgsJson $argsJson $ArgumentList
    if (-not $LogPrefix) { $LogPrefix = Join-Path $dir 'run' }
    $cli = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', (Quote-Argument $script:ProcessScript),
        '-FilePath', (Quote-Argument $FilePath),
        '-ArgsJsonPath', (Quote-Argument $argsJson),
        '-TimeoutSeconds', "$TimeoutSeconds",
        '-LogPath', (Quote-Argument $LogPrefix)
    )
    if ($WorkingDirectory) { $cli += @('-WorkingDirectory', (Quote-Argument $WorkingDirectory)) }
    if ($Hidden) { $cli += '-Hidden' }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:PowerShellExe
    $psi.Arguments = $cli -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    if ($Hidden) { $psi.CreateNoWindow = $true }
    $process = [System.Diagnostics.Process]::Start($psi)
    $result = @{
        Name = $Name
        Process = $process
        Dir = $dir
        LogPath = "$LogPrefix.log"
        ResultPath = "$LogPrefix.json"
    }
    if ($NoWait) { return $result }

    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $exited = $process.WaitForExit($TimeoutSeconds * 1000 + 30000)
    if (-not $exited) {
        try { $process.Kill() } catch { }
        throw "runner CLI did not exit within its own budget ($Name)"
    }
    $result.ExitCode = $process.ExitCode
    $result.StdOut = $stdout.Result
    $result.StdErr = $stderr.Result
    return $result
}

function Read-ResultJson([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

function Test-PidAlive([int]$id) {
    return $null -ne (Get-Process -Id $id -ErrorAction SilentlyContinue)
}

function Wait-PidGone([int[]]$ids, [int]$timeoutMs) {
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $timeoutMs) {
        $alive = @($ids | Where-Object { Test-PidAlive $_ })
        if ($alive.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return (@($ids | Where-Object { Test-PidAlive $_ }).Count -eq 0)
}

function Wait-File([string]$path, [int]$timeoutMs) {
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-Path -LiteralPath $path) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) {
        throw "$Message (expected=[$Expected] actual=[$Actual])"
    }
}

function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-FileContains([string]$Path, [string]$Text, [string]$Message) {
    Assert-True (Test-Path -LiteralPath $Path) "$Message (file missing: $Path)"
    $content = Get-Content -Raw -LiteralPath $Path
    Assert-True ($content -like "*$Text*") "$Message (missing [$Text] in $Path)"
}

# ---------------------------------------------------------------------------
# Tests. Each one is a tracer bullet: written before the behaviour it checks.
# ---------------------------------------------------------------------------

$tests = @(
    @{
        Name = 'start-failure'
        Body = {
            $run = Invoke-RunnerCli -Name 'start-failure' -FilePath (Join-Path $script:RunRoot 'does-not-exist.exe') -TimeoutSeconds 30 -Hidden
            Assert-Equal 2 $run.ExitCode 'start failure must exit 2'
            $result = Read-ResultJson $run.ResultPath
            Assert-True ($null -ne $result) 'start failure must write a result json'
            Assert-Equal $true $result.startFailed 'result must mark startFailed'
            Assert-True ([string]$result.error).Length -gt 0 'result must carry an error message'
            Assert-FileContains $run.LogPath 'CreateProcess' 'log must explain the failed start'
        }
    }
    @{
        Name = 'normal-run'
        Body = {
            $run = Invoke-RunnerCli -Name 'normal-run' -FilePath $script:ProbeExe -ArgumentList @('exit', '--exit-code', '0') -TimeoutSeconds 30 -Hidden
            Assert-Equal 0 $run.ExitCode 'normal run must exit 0'
            $result = Read-ResultJson $run.ResultPath
            Assert-True ($null -ne $result) 'normal run must write a result json'
            Assert-Equal 0 $result.exitCode 'child exit code must be recorded'
            Assert-Equal $false $result.timedOut 'normal run must not time out'
            Assert-Equal $false $result.ctrlC 'normal run must not report ctrl+c'
            Assert-True ($result.processId -gt 0) 'result must record the child pid'
            Assert-FileContains $run.LogPath 'exit' 'log must record the launch and exit'
        }
    }
    @{
        Name = 'nonzero-exit'
        Body = {
            $run = Invoke-RunnerCli -Name 'nonzero-exit' -FilePath $script:ProbeExe -ArgumentList @('exit', '--exit-code', '7') -TimeoutSeconds 30 -Hidden
            Assert-Equal 1 $run.ExitCode 'child failure must make the CLI exit 1'
            $result = Read-ResultJson $run.ResultPath
            Assert-True ($null -ne $result) 'failed child must still write a result json'
            Assert-Equal 7 $result.exitCode 'child exit code must be recorded'
            Assert-Equal $false $result.startFailed 'a started child is not a start failure'
            Assert-FileContains $run.LogPath 'exit code 7' 'log must record the failing exit code'
        }
    }
    @{
        Name = 'arg-quoting'
        Body = {
            $echo = Join-Path $script:AcceptanceDir 'fixtures\ArgsEcho.ps1'
            $expected = @('plain', 'a b c', 'quote"inside', 'back\slash\', '', 'semi;colon', 'tab	inside')
            $echoArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $echo) + $expected
            $run = Invoke-RunnerCli -Name 'arg-quoting' -FilePath $script:PowerShellExe -ArgumentList $echoArgs -TimeoutSeconds 60 -Hidden
            Assert-Equal 0 $run.ExitCode 'arg-quoting run must succeed'
            $line = @(Get-Content -LiteralPath $run.LogPath | Where-Object { $_ -match '^\[("|\])' })[-1]
            Assert-True ([string]$line).Length -gt 0 'child must print its argv as JSON'
            $parsed = $line | ConvertFrom-Json
            $actual = @($parsed)
            Assert-Equal $expected.Count $actual.Count 'argv count must survive quoting'
            for ($i = 0; $i -lt $expected.Count; $i++) {
                Assert-Equal $expected[$i] $actual[$i] ("argv[" + $i + "] must survive quoting")
            }
        }
    }
    @{
        Name = 'timeout-kills-tree'
        Body = {
            $dir = Join-Path $script:RunRoot 'runs\timeout-kills-tree'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $pidsFile = Join-Path $dir 'pids.json'
            $pids = @()
            try {
                $run = Invoke-RunnerCli -Name 'timeout-kills-tree' -FilePath $script:ProbeExe -ArgumentList @('driver', '--pids', $pidsFile, '--sleep-ms', '120000') -TimeoutSeconds 2 -Hidden
                Assert-True (Test-Path -LiteralPath $pidsFile) 'probe must report the pids it created'
                $report = Get-Content -Raw -LiteralPath $pidsFile | ConvertFrom-Json
                $pids = @([int]$report.pid, [int]$report.grandchild)
                Assert-Equal 3 $run.ExitCode 'a timeout must exit 3'
                $result = Read-ResultJson $run.ResultPath
                Assert-Equal $true $result.timedOut 'result must mark the timeout'
                Assert-FileContains $run.LogPath 'timed out' 'log must record the timeout'
                Assert-True (Wait-PidGone $pids 10000) 'a timeout must kill the child and its grandchildren'
            } finally {
                $pids = @()
                if (Test-Path -LiteralPath $pidsFile) {
                    $report = Get-Content -Raw -LiteralPath $pidsFile | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if ($null -ne $report) { $pids = @([int]$report.pid, [int]$report.grandchild) }
                }
                foreach ($processId in $pids) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
            }
        }
    }
    @{
        Name = 'normal-exit-cleans-orphans'
        Body = {
            $dir = Join-Path $script:RunRoot 'runs\normal-exit-cleans-orphans'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $pidsFile = Join-Path $dir 'pids.json'
            $pids = @()
            try {
                # driver writes its pids, then exits after 700 ms, leaving its
                # grandchild behind. The runner closing the job must clean it up.
                $run = Invoke-RunnerCli -Name 'normal-exit-cleans-orphans' -FilePath $script:ProbeExe -ArgumentList @('driver', '--pids', $pidsFile, '--sleep-ms', '700') -TimeoutSeconds 30 -Hidden
                Assert-Equal 0 $run.ExitCode 'driver run must exit 0'
                Assert-True (Test-Path -LiteralPath $pidsFile) 'probe must report the pids it created'
                $report = Get-Content -Raw -LiteralPath $pidsFile | ConvertFrom-Json
                $pids = @([int]$report.pid, [int]$report.grandchild)
                Assert-True (Wait-PidGone $pids 10000) 'a normal exit must not leave grandchildren behind'
                Assert-FileContains $run.LogPath 'driver stderr marker' 'the log must capture child stderr too'
            } finally {
                $pids = @()
                if (Test-Path -LiteralPath $pidsFile) {
                    $report = Get-Content -Raw -LiteralPath $pidsFile | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if ($null -ne $report) { $pids = @([int]$report.pid, [int]$report.grandchild) }
                }
                foreach ($processId in $pids) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
            }
        }
    }
    @{
        Name = 'ctrl-c-cleanup'
        Body = {
            $dir = Join-Path $script:RunRoot 'runs\ctrl-c-cleanup'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $pidsFile = Join-Path $dir 'pids.json'
            $guardFile = Join-Path $dir 'guard.txt'
            $pids = @()
            Remove-Item -LiteralPath $pidsFile, $guardFile -Force -ErrorAction SilentlyContinue
            [void][ConsoleGuard]::Register($guardFile)
            $run = $null
            try {
                # No -Hidden: the runner needs a real console for the event.
                $run = Invoke-RunnerCli -Name 'ctrl-c-cleanup' -FilePath $script:ProbeExe -ArgumentList @('driver', '--pids', $pidsFile, '--sleep-ms', '120000') -TimeoutSeconds 60 -NoWait
                Assert-True (Wait-File $pidsFile 20000) 'driver must report pids'
                $report = Get-Content -Raw -LiteralPath $pidsFile | ConvertFrom-Json
                $pids = @([int]$report.pid, [int]$report.grandchild)
                $injector = [System.Diagnostics.Process]::Start((New-Object System.Diagnostics.ProcessStartInfo($script:ProbeExe, ('inject --pid ' + $run.Process.Id + ' --delay-ms 300'))))
                Assert-True ($injector.WaitForExit(20000)) 'injector must finish'
                Assert-True ($run.Process.WaitForExit(30000)) 'runner must stop after ctrl+c'
                Assert-Equal 130 $run.Process.ExitCode 'ctrl+c must make the CLI exit 130'
                $result = Read-ResultJson $run.ResultPath
                Assert-True ($null -ne $result) 'ctrl+c must write a result json'
                Assert-Equal $true $result.ctrlC 'result must mark the ctrl+c'
                Assert-FileContains $run.LogPath 'ctrl' 'log must record the ctrl+c'
                Assert-True (Wait-PidGone $pids 15000) 'ctrl+c must clean up the child and its grandchildren'
            } finally {
                [void][ConsoleGuard]::Unregister()
                if ($null -ne $run -and -not $run.Process.HasExited) {
                    try { $run.Process.Kill() } catch { }
                    $null = $run.Process.WaitForExit(10000)
                }
                $pids = @()
                if (Test-Path -LiteralPath $pidsFile) {
                    $report = Get-Content -Raw -LiteralPath $pidsFile | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if ($null -ne $report) { $pids = @([int]$report.pid, [int]$report.grandchild) }
                }
                foreach ($processId in $pids) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
            }
        }
    }
    @{
        Name = 'hard-kill-cleans-tree'
        Body = {
            $dir = Join-Path $script:RunRoot 'runs\hard-kill-cleans-tree'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $pidsFile = Join-Path $dir 'pids.json'
            Remove-Item -LiteralPath $pidsFile -Force -ErrorAction SilentlyContinue
            $pids = @()
            $run = $null
            try {
                $run = Invoke-RunnerCli -Name 'hard-kill-cleans-tree' -FilePath $script:ProbeExe -ArgumentList @('driver', '--pids', $pidsFile, '--sleep-ms', '120000') -TimeoutSeconds 60 -Hidden -NoWait
                Assert-True (Wait-File $pidsFile 20000) 'driver must report pids'
                $report = Get-Content -Raw -LiteralPath $pidsFile | ConvertFrom-Json
                $pids = @([int]$report.pid, [int]$report.grandchild)
                Assert-True (Test-PidAlive $pids[0]) 'driver must still be running before the kill'
                try { $run.Process.Kill() } catch { }
                Assert-True ($run.Process.WaitForExit(15000)) 'a hard-killed runner must exit'
                Assert-True ($run.Process.ExitCode -ne 0) 'a hard-killed runner must not report success'
                Assert-True (Wait-PidGone $pids 15000) 'hard-killing the runner must kill the whole tree'
            } finally {
                if ($null -ne $run -and -not $run.Process.HasExited) {
                    try { $run.Process.Kill() } catch { }
                    $null = $run.Process.WaitForExit(10000)
                }
                foreach ($processId in $pids) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
            }
        }
    }
    @{
        Name = 'existing-process-untouched'
        Body = {
            $dir = Join-Path $script:RunRoot 'runs\existing-process-untouched'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $foreignFile = Join-Path $dir 'foreign.json'
            $ourFile = Join-Path $dir 'ours.json'
            Remove-Item -LiteralPath $foreignFile, $ourFile -Force -ErrorAction SilentlyContinue
            $foreign = $null
            $foreignPids = @()
            try {
                # A process with the same image name, started outside the runner.
                $foreign = [System.Diagnostics.Process]::Start((New-Object System.Diagnostics.ProcessStartInfo($script:ProbeExe, ('driver --pids ' + $foreignFile + ' --sleep-ms 120000'))))
                Assert-True (Wait-File $foreignFile 20000) 'foreign driver must report pids'
                $foreignReport = Get-Content -Raw -LiteralPath $foreignFile | ConvertFrom-Json
                $foreignPids = @([int]$foreignReport.pid, [int]$foreignReport.grandchild)

                $run = Invoke-RunnerCli -Name 'existing-process-untouched' -FilePath $script:ProbeExe -ArgumentList @('driver', '--pids', $ourFile, '--sleep-ms', '700') -TimeoutSeconds 30 -Hidden
                Assert-Equal 0 $run.ExitCode 'owned run must succeed'
                $ownReport = Get-Content -Raw -LiteralPath $ourFile | ConvertFrom-Json
                $ownPids = @([int]$ownReport.pid, [int]$ownReport.grandchild)
                Assert-True (Wait-PidGone $ownPids 10000) 'runner must clean up its own tree'

                Assert-True (Test-PidAlive $foreignPids[0]) 'pre-existing same-name process must survive'
                Assert-True (Test-PidAlive $foreignPids[1]) 'pre-existing grandchild must survive'
            } finally {
                foreach ($processId in $foreignPids) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
                if ($null -ne $foreign -and -not $foreign.HasExited) {
                    try { $foreign.Kill() } catch { }
                    $null = $foreign.WaitForExit(10000)
                }
            }
        }
    }
    @{
        Name = 'synthetic-config-unchanged'
        Body = {
            $dir = Join-Path $script:RunRoot 'runs\synthetic-config-unchanged'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $sentinel = Join-Path $dir 'synthetic-settings.json'
            $text = '{"fixture":"synthetic-42","name":"' + [char]0x8bfe + [char]0x8868 + '","note":"runner must not read or write this"}'
            [IO.File]::WriteAllText($sentinel, $text, (New-Object System.Text.UTF8Encoding($false)))
            $before = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash

            $run = Invoke-RunnerCli -Name 'synthetic-config-unchanged' -FilePath $script:ProbeExe -ArgumentList @('exit', '--exit-code', '0') -TimeoutSeconds 30 -Hidden
            Assert-Equal 0 $run.ExitCode 'probe run must succeed'

            $after = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
            Assert-Equal $before $after 'synthetic config bytes must not change'
            Assert-Equal $text ([IO.File]::ReadAllText($sentinel)) 'synthetic config text must stay identical'
        }
    }
    @{
        Name = 'module-seam'
        Body = {
            $dir = Join-Path $script:RunRoot 'runs\module-seam'
            New-Item -ItemType Directory -Force -Path $dir | Out-Null

            # Dot-sourcing must only define the function; it must not run the CLI.
            . $script:ProcessScript
            Assert-True ($null -ne (Get-Command Invoke-OwnedProcess -ErrorAction SilentlyContinue)) 'dot-sourcing must define Invoke-OwnedProcess'

            $ok = Invoke-OwnedProcess -FilePath $script:ProbeExe -ArgumentList @('exit', '--exit-code', '0') -TimeoutSeconds 30 -LogPath (Join-Path $dir 'ok') -Hidden
            Assert-Equal 0 $ok.ExitCode 'module success result must carry the exit code'
            Assert-Equal $false $ok.TimedOut 'module success result must not be timed out'
            Assert-True ($ok.LogPath -like '*.log') 'module result must expose the log path'

            $threw = $false
            $record = $null
            try {
                $null = Invoke-OwnedProcess -FilePath $script:ProbeExe -ArgumentList @('exit', '--exit-code', '9') -TimeoutSeconds 30 -LogPath (Join-Path $dir 'fail') -Hidden
            } catch {
                $threw = $true
                $record = $_.Exception.Data['OwnedProcessResult']
            }
            Assert-True $threw 'module must throw when the child fails'
            Assert-True ($null -ne $record) 'thrown exception must carry the OwnedProcessResult record'
            Assert-Equal 9 $record.exitCode 'thrown record must carry the child exit code'

            $expected = Invoke-OwnedProcess -FilePath $script:ProbeExe -ArgumentList @('exit', '--exit-code', '7') -TimeoutSeconds 30 -LogPath (Join-Path $dir 'expected7') -Hidden -SuccessExitCodes @(7)
            Assert-Equal 7 $expected.ExitCode 'SuccessExitCodes must be honoured by the module'
        }
    }
)

foreach ($test in $tests) {
    if ($Filter -and $test.Name -notlike $Filter) { continue }
    $dir = Join-Path $script:RunRoot (Join-Path 'runs' $test.Name)
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $passed = $false
    $message = ''
    try {
        & $test.Body
        $passed = $true
    } catch {
        $message = ($_ | Out-String).Trim()
    }
    $stopwatch.Stop()
    $line = if ($passed) { '[PASS] ' } else { '[FAIL] ' }
    Write-Host ("{0}{1} ({2} ms)" -f $line, $test.Name, $stopwatch.ElapsedMilliseconds)
    if (-not $passed) {
        Write-Host $message
        $script:Failures++
    }
    $script:Results += [pscustomobject]@{
        name = $test.Name
        passed = $passed
        durationMs = $stopwatch.ElapsedMilliseconds
        message = $message
    }
}

$summary = [pscustomobject]@{
    runRoot = $script:RunRoot
    total = $script:Results.Count
    failed = $script:Failures
    results = $script:Results
}
[IO.File]::WriteAllText((Join-Path $script:RunRoot 'results.json'), (ConvertTo-Json -InputObject $summary -Depth 6), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("{0} test(s), {1} failed; run root: {2}" -f $script:Results.Count, $script:Failures, $script:RunRoot)
exit $(if ($script:Failures -gt 0) { 1 } else { 0 })
