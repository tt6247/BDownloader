param(
    [string]$BundlePath = (Join-Path $PSScriptRoot '..\releases\BDownloader.exe'),
    [ValidateRange(20, 60)][int]$TimeoutSeconds = 55
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bundle = [IO.Path]::GetFullPath($BundlePath)
if (!(Test-Path -LiteralPath $bundle -PathType Leaf)) { throw "Missing $bundle; run scripts\build-single-file.ps1 first." }
$verification = Join-Path $workspace 'analysis\verification'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$sandbox = [IO.Path]::GetFullPath((Join-Path $verification "bundle-sandbox\$runId"))
$delivery = Join-Path $sandbox 'delivery'
$logs = Join-Path $sandbox 'logs'
$reports = Join-Path $sandbox 'reports'
New-Item -ItemType Directory -Path $delivery, $logs, $reports | Out-Null
$testBundle = Join-Path $delivery 'BDownloader.exe'
Copy-Item -LiteralPath $bundle -Destination $testBundle
$state = @{}
$summary = [ordered]@{ RunId = $runId; StartedUtc = [DateTime]::UtcNow.ToString('o'); Bundle = $bundle;
    BundleSha256 = (Get-FileHash -LiteralPath $testBundle -Algorithm SHA256).Hash; Sandbox = $sandbox;
    Tests = @(); Passed = 0; Failed = 0 }

function Require([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function In-Sandbox([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    Require ($full.StartsWith($sandbox.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'Test operation escaped its synthetic sandbox.'
    return $full
}
function Quote-Argument([string]$Value) { return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"' }
function Check([string]$Name, [scriptblock]$Action) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $summary.Passed += 1
        $summary.Tests += [ordered]@{ Name = $Name; Passed = $true; ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2) }
        Write-Output "PASS $Name"
    }
    catch {
        $summary.Failed += 1
        $summary.Tests += [ordered]@{ Name = $Name; Passed = $false; Error = $_.Exception.Message; ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2) }
        Write-Output "FAIL ${Name}: $($_.Exception.Message)"
    }
    finally { $watch.Stop() }
}
function Start-Bundle([string]$Name, [string]$Cache, [string]$Data, [switch]$NormalLaunch) {
    $Cache = In-Sandbox $Cache; $Data = In-Sandbox $Data
    $report = Join-Path $reports "$Name.json"
    $start = @{ FilePath = $testBundle; WorkingDirectory = $delivery; WindowStyle = 'Hidden'; PassThru = $true;
        RedirectStandardOutput = (Join-Path $logs "$Name.log"); RedirectStandardError = (Join-Path $logs "$Name-errors.log") }
    if (!$NormalLaunch) { $start.ArgumentList = '--bundle-verify ' + (Quote-Argument $report) }
    $previousCache = [Environment]::GetEnvironmentVariable('BDOWNLOADER_CACHE_DIRECTORY')
    $previousData = [Environment]::GetEnvironmentVariable('BDOWNLOADER_DATA_DIRECTORY')
    try {
        [Environment]::SetEnvironmentVariable('BDOWNLOADER_CACHE_DIRECTORY', $Cache)
        [Environment]::SetEnvironmentVariable('BDOWNLOADER_DATA_DIRECTORY', $Data)
        $process = Start-Process @start
    }
    finally {
        [Environment]::SetEnvironmentVariable('BDOWNLOADER_CACHE_DIRECTORY', $previousCache)
        [Environment]::SetEnvironmentVariable('BDOWNLOADER_DATA_DIRECTORY', $previousData)
    }
    return @{ Process = $process; Report = $report; NormalLaunch = [bool]$NormalLaunch; Name = $Name }
}
function Finish-Bundle($Invocation, [bool]$ExpectedSuccess = $true) {
    $process = $Invocation.Process
    try {
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            & (Join-Path $env:WINDIR 'System32\taskkill.exe') /PID $process.Id /T /F 2>&1 | Out-Null
            throw "Bundle invocation timed out: $($Invocation.Name)"
        }
        $process.WaitForExit(); $exitCode = $process.ExitCode
    }
    finally { $process.Dispose() }
    if ($Invocation.NormalLaunch) { Require ($exitCode -eq 0) 'Normal bundle launcher failed.'; return }
    Require (Test-Path -LiteralPath $Invocation.Report -PathType Leaf) "Missing launcher report: $($Invocation.Name)"
    $report = Get-Content -LiteralPath $Invocation.Report -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($ExpectedSuccess) { Require ($exitCode -eq 0 -and $report.Success) "Bundle verification failed: $($report.Error)" }
    else { Require ($exitCode -ne 0 -and !$report.Success) 'Unsafe reparse cache was unexpectedly accepted.' }
    return $report
}
function Verify-Bundle([string]$Name, [string]$Cache, [string]$Data, [bool]$ExpectedSuccess = $true) {
    return Finish-Bundle (Start-Bundle $Name $Cache $Data) $ExpectedSuccess
}
function Was-Extracted($Report) {
    if ($null -ne $Report.Extracted) { return [bool]$Report.Extracted }
    return [bool]$Report.CacheWasExtracted
}
function Verified-Count($Report) {
    if ($null -ne $Report.VerifiedFileCount) { return [int]$Report.VerifiedFileCount }
    return [int]$Report.FilesVerified
}
function File-Snapshot([string]$Root) {
    $snapshot = @{}
    foreach ($file in Get-ChildItem -LiteralPath (In-Sandbox $Root) -Recurse -File) {
        Require (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Snapshot encountered a reparse point.'
        $snapshot[$file.FullName] = @{ Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash; Ticks = $file.LastWriteTimeUtc.Ticks }
    }
    return $snapshot
}

try {
    $state.Cache = Join-Path $sandbox 'main-cache'; $state.Data = Join-Path $sandbox 'main-data'
    Check 'fresh single executable extracts and verifies every manifest file' {
        $first = Verify-Bundle 'fresh' $state.Cache $state.Data
        Require (Was-Extracted $first) 'Fresh bundle did not extract its runtime.'
        Require ($first.AllHashesVerified -and !$first.LaunchedApplication) 'Verify-only mode did not complete full checks without GUI.'
        Require ($first.PayloadSha256 -match '^[0-9a-f]{64}$') 'Payload hash is invalid.'
        $runtime = In-Sandbox $first.RuntimeDirectory
        $version = In-Sandbox ([IO.Path]::GetDirectoryName($runtime))
        Require ($version -eq (Join-Path $state.Cache $first.PayloadSha256)) 'Runtime is not in the expected versioned cache.'
        Require ([IO.Path]::GetFullPath($first.DataDirectory) -eq [IO.Path]::GetFullPath($state.Data)) 'Persistent data override was ignored.'
        $manifest = Get-Content -LiteralPath (Join-Path $version 'bundle-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        Require ($manifest.Files.Count -eq (Verified-Count $first)) 'Verified file count differs from embedded manifest.'
        foreach ($file in $manifest.Files) {
            $path = In-Sandbox (Join-Path $version $file.Path.Replace('/', '\'))
            Require ((Get-Item -LiteralPath $path).Length -eq $file.Size) "Manifest size mismatch: $($file.Path)"
            Require ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $file.Sha256) "Manifest hash mismatch: $($file.Path)"
        }
        foreach ($name in @('BDownloader.exe', 'BDownloader.exe.config', 'Newtonsoft.Json.dll', 'QRCoder.dll', 'ffmpeg.exe', 'yt-dlp.exe', 'qjs.exe')) {
            Require (Test-Path -LiteralPath (Join-Path $runtime $name) -PathType Leaf) "Missing extracted dependency: $name"
        }
        Require (!(Test-Path -LiteralPath (Join-Path $runtime 'deno.exe'))) 'Compact QuickJS runtime unexpectedly includes Deno.'
        Require (@($manifest.Files | Where-Object { $_.Path -match '\.pdb$' }).Count -eq 0) 'Release runtime includes development symbols.'
        $state.First = $first; $state.Manifest = $manifest; $state.Runtime = $runtime; $state.Version = $version
        $summary.PayloadSha256 = $first.PayloadSha256; $summary.ApplicationSha256 = $first.ApplicationSha256
    }
    Require ($null -ne $state.First) 'Fresh extraction must succeed before dependent integration checks.'

    Check 'verified second launch reuses cache without rewriting files' {
        $before = File-Snapshot $state.Version
        $again = Verify-Bundle 'reuse' $state.Cache $state.Data
        Require (!(Was-Extracted $again)) 'Verified cache was needlessly re-extracted.'
        Require ($again.RuntimeDirectory -eq $state.Runtime) 'Second launch changed runtime location.'
        $after = File-Snapshot $state.Version
        Require ($before.Count -eq $after.Count) 'Cache reuse changed file count.'
        foreach ($path in $before.Keys) {
            Require ($after.ContainsKey($path) -and $before[$path].Hash -eq $after[$path].Hash -and $before[$path].Ticks -eq $after[$path].Ticks) 'Cache reuse rewrote a verified runtime file.'
        }
    }
    Check 'corrupted QuickJS cache is repaired while persistent data survives' {
        $sentinel = In-Sandbox (Join-Path $state.Data 'synthetic-state.txt')
        [IO.File]::WriteAllText($sentinel, 'synthetic data must survive runtime repair')
        $sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
        $qjs = In-Sandbox (Join-Path $state.Runtime 'qjs.exe')
        [IO.File]::WriteAllBytes($qjs, [byte[]](0x42, 0x41, 0x44))
        $repaired = Verify-Bundle 'repair' $state.Cache $state.Data
        Require (Was-Extracted $repaired) 'Corrupt cache was not replaced.'
        $expected = @($state.Manifest.Files | Where-Object { $_.Path -eq 'payload/qjs.exe' })[0].Sha256
        Require ((Get-FileHash -LiteralPath $qjs -Algorithm SHA256).Hash -eq $expected) 'QuickJS bytes were not restored.'
        Require ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $sentinelHash) 'Runtime repair changed persistent data.'
        Require (@(Get-ChildItem -LiteralPath $state.Cache -Directory | Where-Object { $_.Name.StartsWith('.invalid-') }).Count -eq 1) 'Corrupted runtime was not preserved in quarantine.'
    }
    Check 'concurrent first launches serialize extraction and share verified runtime' {
        $cache = Join-Path $sandbox 'concurrent-cache'; $data = Join-Path $sandbox 'concurrent-data'
        $one = Start-Bundle 'concurrent-one' $cache $data
        $two = Start-Bundle 'concurrent-two' $cache $data
        $a = Finish-Bundle $one; $b = Finish-Bundle $two
        Require ($a.RuntimeDirectory -eq $b.RuntimeDirectory) 'Concurrent launches published different runtime paths.'
        Require ((([int](Was-Extracted $a)) + ([int](Was-Extracted $b))) -eq 1) 'Concurrent first launches did not extract exactly once.'
        Require (@(Get-ChildItem -LiteralPath $cache -Directory | Where-Object { $_.Name.StartsWith('.extract-') }).Count -eq 0) 'Concurrent launch left an extraction staging directory.'
    }
    Check 'junction as versioned cache is rejected without modifying its target' {
        $cache = In-Sandbox (Join-Path $sandbox 'junction-cache'); $data = Join-Path $sandbox 'junction-data'
        $target = In-Sandbox (Join-Path $sandbox 'junction-target')
        New-Item -ItemType Directory -Path $cache, $target | Out-Null
        $sentinel = Join-Path $target 'untouched.txt'; [IO.File]::WriteAllText($sentinel, 'synthetic junction target')
        $link = In-Sandbox (Join-Path $cache $state.First.PayloadSha256)
        New-Item -ItemType Junction -Path $link -Target $target | Out-Null
        $rejected = Verify-Bundle 'junction-version' $cache $data $false
        Require (!$rejected.Success) 'Version junction was accepted.'
        Require ([IO.File]::ReadAllText($sentinel) -eq 'synthetic junction target') 'Junction target was changed.'
        Require (@(Get-ChildItem -LiteralPath $target -File).Count -eq 1) 'Launcher wrote into a junction target.'
    }
    Check 'junction in executable directory is rejected without following it' {
        $cache = In-Sandbox (Join-Path $sandbox 'nested-cache'); $data = Join-Path $sandbox 'nested-data'
        $version = In-Sandbox (Join-Path $cache $state.First.PayloadSha256)
        $target = In-Sandbox (Join-Path $sandbox 'nested-target')
        New-Item -ItemType Directory -Path $version, $target | Out-Null
        $sentinel = Join-Path $target 'BDownloader.exe'; [IO.File]::WriteAllText($sentinel, 'synthetic executable sentinel')
        Copy-Item -LiteralPath (Join-Path $state.Version 'bundle-manifest.json') -Destination $version
        New-Item -ItemType Junction -Path (Join-Path $version 'payload') -Target $target | Out-Null
        [void](Verify-Bundle 'junction-payload' $cache $data $false)
        Require ([IO.File]::ReadAllText($sentinel) -eq 'synthetic executable sentinel') 'Executable junction target changed.'
        Require (@(Get-ChildItem -LiteralPath $target -File).Count -eq 1) 'Launcher followed executable-directory junction.'
    }
    Check 'normal single-file launch opens real GUI and writes only to stable data' {
        $expectedExecutable = In-Sandbox (Join-Path $state.Runtime 'BDownloader.exe')
        $invocation = Start-Bundle 'normal-gui' $state.Cache $state.Data -NormalLaunch
        Finish-Bundle $invocation
        $child = $null
        try {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            while ($watch.Elapsed.TotalSeconds -lt 20) {
                $child = Get-Process -Name BDownloader -ErrorAction SilentlyContinue | Where-Object {
                    try { $_.Path -eq $expectedExecutable } catch { $false }
                } | Select-Object -First 1
                if ($null -ne $child) { $child.Refresh(); if ($child.MainWindowHandle -ne [IntPtr]::Zero -and $child.MainWindowTitle -eq 'BDownloader') { break } }
                Start-Sleep -Milliseconds 150
            }
            Require ($null -ne $child -and $child.MainWindowHandle -ne [IntPtr]::Zero -and $child.MainWindowTitle -eq 'BDownloader') 'Extracted application did not display its real main window.'
            $summary.GuiExecutable = $child.Path; $summary.GuiProcessId = $child.Id
            Require ($child.CloseMainWindow()) 'Could not close the test application normally.'
            Require ($child.WaitForExit(10000)) 'Test application did not exit after CloseMainWindow.'
        }
        finally {
            if ($null -ne $child) {
                if (!$child.HasExited) { $child.Kill(); $child.WaitForExit(5000) }
                $child.Dispose()
            }
        }
        Require (Test-Path -LiteralPath (Join-Path $state.Data 'Storage\media-queue.json') -PathType Leaf) 'Application did not persist its queue under stable data.'
        foreach ($root in @($delivery, $state.Runtime)) {
            Require (!(Test-Path -LiteralPath (Join-Path $root 'Config'))) 'Application wrote account data beside its executable.'
            Require (!(Test-Path -LiteralPath (Join-Path $root 'Storage'))) 'Application wrote queue data into the runtime cache.'
        }
        Require (@(Get-ChildItem -LiteralPath $delivery -File).Count -eq 1) 'Single executable created required sidecar files in its delivery folder.'
    }
}
catch { $summary.Failed += 1; $summary.FatalError = $_.Exception.Message; throw }
finally {
    $summary.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $sandbox 'summary.json') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $sandbox 'summary.json') -Destination (Join-Path $verification 'single-file-summary.json') -Force
    Write-Output "Single-file integration: passed=$($summary.Passed) failed=$($summary.Failed)"
    Write-Output "Summary: $sandbox\summary.json"
}
if ($summary.Failed -ne 0) { throw 'Single-file integration verification failed; see its summary.' }
