param(
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\app'),
    [ValidateRange(20, 600)][int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$application = [IO.Path]::GetFullPath($ApplicationDirectory)
$verification = Join-Path $workspace 'analysis\verification'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runDirectory = Join-Path $verification "standalone\$runId"
$authDirectory = Join-Path $verification "auth-sandbox\standalone-$runId"
$compiler = Join-Path $workspace '.tools\roslyn-4.8.0\tasks\net472\csc.exe'
$references = Join-Path $workspace '.tools\net472-reference-1.0.3\build\.NETFramework\v4.7.2'
$programSource = Join-Path $application 'BDownloader.exe'
$dependencies = @('Newtonsoft.Json.dll', 'QRCoder.dll')

foreach ($required in @($compiler, (Join-Path $references 'mscorlib.dll'), $programSource,
    (Join-Path $application 'ffmpeg.exe')) + @($dependencies | ForEach-Object { Join-Path $application $_ })) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing $required. Build the application first with scripts\build.ps1." }
}
New-Item -ItemType Directory -Path $runDirectory, $authDirectory | Out-Null
$frameworkReferences = @(Get-ChildItem -LiteralPath $references -Filter '*.dll' -File |
    Where-Object Name -NotIn @('System.EnterpriseServices.Wrapper.dll', 'System.EnterpriseServices.Thunk.dll') |
    ForEach-Object { "/reference:$($_.FullName)" })
$summary = [ordered]@{
    RunId = $runId
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    ApplicationDirectory = $application
    ApplicationSha256 = (Get-FileHash -LiteralPath $programSource -Algorithm SHA256).Hash
    Suites = @()
    Passed = 0
    Failed = 0
}

function ConvertTo-NativeArgument([string]$Value) {
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Invoke-StandaloneSuite([string]$Name, [string]$Sandbox, [int]$ExpectedChecks, [switch]$NeedsMedia) {
    New-Item -ItemType Directory -Path $Sandbox -Force | Out-Null
    # Each host loads the exact packaged application from a fresh private directory.
    # Saved credentials, configuration, queues and user media are never copied.
    Copy-Item -LiteralPath $programSource -Destination $Sandbox
    foreach ($dependency in $dependencies) { Copy-Item -LiteralPath (Join-Path $application $dependency) -Destination $Sandbox }
    if ($NeedsMedia) {
        Copy-Item -LiteralPath (Join-Path $application 'ffmpeg.exe') -Destination $Sandbox
        $probe = Join-Path $application 'ffprobe.exe'
        if (Test-Path -LiteralPath $probe) { Copy-Item -LiteralPath $probe -Destination $Sandbox }
    }
    $program = Join-Path $Sandbox "$Name.exe"
    $compileLog = Join-Path $runDirectory "$Name-compile.log"
    $stdout = Join-Path $runDirectory "$Name.log"
    $stderr = Join-Path $runDirectory "$Name-errors.log"
    $source = Join-Path $workspace "tests\standalone\$Name.cs"
    $arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nostdlib+', "/out:$program", $source) + $frameworkReferences
    $arguments += "/reference:$(Join-Path $Sandbox 'BDownloader.exe')"
    $arguments += $dependencies | ForEach-Object { "/reference:$(Join-Path $Sandbox $_)" }
    & $compiler @arguments 2>&1 | Set-Content -LiteralPath $compileLog -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw "$Name compilation failed; see $compileLog" }
    $configuration = "$programSource.config"
    if (Test-Path -LiteralPath $configuration) { Copy-Item -LiteralPath $configuration -Destination "$program.config" }

    $start = @{ FilePath = $program; WorkingDirectory = $Sandbox; WindowStyle = 'Hidden';
        RedirectStandardOutput = $stdout; RedirectStandardError = $stderr; PassThru = $true }
    if ($NeedsMedia) { $start.ArgumentList = (ConvertTo-NativeArgument $Sandbox) + ' ' + (ConvertTo-NativeArgument (Join-Path $Sandbox 'artifacts')) }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $previousDataDirectory = [Environment]::GetEnvironmentVariable('BDOWNLOADER_DATA_DIRECTORY')
    try {
        [Environment]::SetEnvironmentVariable('BDOWNLOADER_DATA_DIRECTORY', $Sandbox)
        $process = Start-Process @start
    }
    finally { [Environment]::SetEnvironmentVariable('BDOWNLOADER_DATA_DIRECTORY', $previousDataDirectory) }
    $timedOut = !$process.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        & (Join-Path $env:WINDIR 'System32\taskkill.exe') /PID $process.Id /T /F 2>&1 | Out-Null
        if (!$process.HasExited) { $process.Kill() }
    }
    $process.WaitForExit()
    $watch.Stop()
    $exitCode = $process.ExitCode
    $process.Dispose()
    $lines = @(Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue)
    $passed = @($lines | Where-Object { $_ -match '^PASS ' }).Count
    $failed = @($lines | Where-Object { $_ -match '^FAIL ' }).Count
    if ($passed + $failed -ne $ExpectedChecks) {
        "Expected $ExpectedChecks regression checks; found passed=$passed failed=$failed." | Add-Content -LiteralPath $stderr -Encoding UTF8
        if ($failed -eq 0) { $failed = 1 }
    }
    if ($timedOut -or $exitCode -ne 0) { if ($failed -eq 0) { $failed = 1 } }
    $lines | Where-Object { $_ -match '^(PASS|FAIL|RESULT|Media engine failures:)' } | Write-Output
    $summary.Suites += [ordered]@{ Name = $Name; Expected = $ExpectedChecks; Passed = $passed; Failed = $failed;
        ExitCode = $exitCode; TimedOut = $timedOut; ElapsedSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2);
        Sandbox = $Sandbox; OutputLog = $stdout; ErrorLog = $stderr }
    $summary.Passed += $passed; $summary.Failed += $failed
    Write-Output "$Name status: passed=$passed failed=$failed exit=$exitCode timedOut=$timedOut"
}

try {
    # The playlist host's optional live-network case is excluded from this offline run.
    Invoke-StandaloneSuite 'PlaylistTests' (Join-Path $runDirectory 'playlist') 18
    Invoke-StandaloneSuite 'MediaEngineTests' (Join-Path $runDirectory 'engine') 17 -NeedsMedia
    Invoke-StandaloneSuite 'MediaAccountsTests' $authDirectory 10
    Invoke-StandaloneSuite 'AppPathsTests' (Join-Path $runDirectory 'paths') 6
}
catch { $summary.Failed += 1; $summary.FatalError = $_.Exception.Message; throw }
finally {
    $summary.CompletedUtc = [DateTime]::UtcNow.ToString('o')
    $summaryPath = Join-Path $runDirectory 'summary.json'
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    Copy-Item -LiteralPath $summaryPath -Destination (Join-Path $verification 'standalone-summary.json') -Force
    Write-Output "Offline verification: passed=$($summary.Passed) failed=$($summary.Failed)"
    Write-Output "Verification summary: $summaryPath"
}
if ($summary.Failed -ne 0) { throw "Offline verification failed; see $summaryPath" }
