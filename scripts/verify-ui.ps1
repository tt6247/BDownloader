param(
    [switch]$Online,
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\app')
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$application = [IO.Path]::GetFullPath($ApplicationDirectory)
$verification = Join-Path $workspace 'analysis\verification'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$sandbox = Join-Path $verification "ui-sandbox\$runId"
$compiler = Join-Path $workspace '.tools\roslyn-4.8.0\tasks\net472\csc.exe'
$references = Join-Path $workspace '.tools\net472-reference-1.0.3\build\.NETFramework\v4.7.2'
$applicationExe = Join-Path $application 'BDownloader.exe'
foreach ($required in @($compiler, $applicationExe, (Join-Path $application 'Newtonsoft.Json.dll'), (Join-Path $application 'QRCoder.dll'))) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing $required; build the application first." }
}
New-Item -ItemType Directory -Path $sandbox | Out-Null
# Only packaged dependencies are copied. No Config/Storage, saved login or user files.
foreach ($name in @('BDownloader.exe', 'BDownloader.exe.config', 'Newtonsoft.Json.dll', 'QRCoder.dll', 'yt-dlp.exe', 'ffmpeg.exe', 'qjs.exe')) {
    $file = Join-Path $application $name
    if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination $sandbox }
}
$program = Join-Path $sandbox 'UISmoke.exe'
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/langversion:latest', '/nostdlib+', "/out:$program", (Join-Path $workspace 'tests\standalone\UISmoke.cs'))
$arguments += Get-ChildItem -LiteralPath $references -Filter '*.dll' -File | Where-Object Name -NotIn @('System.EnterpriseServices.Wrapper.dll', 'System.EnterpriseServices.Thunk.dll') | ForEach-Object { "/reference:$($_.FullName)" }
foreach ($name in @('BDownloader.exe', 'Newtonsoft.Json.dll', 'QRCoder.dll')) { $arguments += "/reference:$(Join-Path $sandbox $name)" }
& $compiler @arguments 2>&1 | Set-Content -LiteralPath (Join-Path $sandbox 'compile.log') -Encoding UTF8
if ($LASTEXITCODE -ne 0) { throw "UI host compilation failed; see $sandbox\compile.log" }
if (Test-Path -LiteralPath "$applicationExe.config") { Copy-Item -LiteralPath "$applicationExe.config" -Destination "$program.config" }
$stdout = Join-Path $sandbox 'ui.log'
$stderr = Join-Path $sandbox 'ui-errors.log'
$start = @{ FilePath = $program; WorkingDirectory = $sandbox; WindowStyle = 'Hidden'; RedirectStandardOutput = $stdout; RedirectStandardError = $stderr; PassThru = $true }
if ($Online) { $start.ArgumentList = '--online' }
$process = Start-Process @start
$timedOut = !$process.WaitForExit(150000)
if ($timedOut) { & (Join-Path $env:WINDIR 'System32\taskkill.exe') /PID $process.Id /T /F 2>&1 | Out-Null }
$process.WaitForExit()
$exitCode = $process.ExitCode; $process.Dispose()
$lines = @(Get-Content -LiteralPath $stdout -Encoding UTF8)
$lines | Where-Object { $_ -match '^(PASS|FAIL|RESULT)' } | Write-Output
$passed = @($lines | Where-Object { $_ -match '^PASS ' }).Count
$failed = @($lines | Where-Object { $_ -match '^FAIL ' }).Count
if ($timedOut -or $exitCode -ne 0 -or $passed -ne 10) { if ($failed -eq 0) { $failed = 1 } }
$summary = [ordered]@{ RunId = $runId; Online = [bool]$Online; FixtureData = !$Online; Expected = 10; Passed = $passed; Failed = $failed;
    ExitCode = $exitCode; TimedOut = $timedOut; ApplicationSha256 = (Get-FileHash -LiteralPath (Join-Path $sandbox 'BDownloader.exe') -Algorithm SHA256).Hash;
    Sandbox = $sandbox; OutputLog = $stdout; ErrorLog = $stderr; Artifacts = (Join-Path $sandbox 'artifacts'); VerifiedUtc = [DateTime]::UtcNow.ToString('o') }
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $sandbox 'summary.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $sandbox 'summary.json') -Destination (Join-Path $verification 'ui-summary.json') -Force
Write-Output "UI verification artifacts: $sandbox\artifacts"
if ($failed -ne 0) { throw "UI verification failed; see $stdout and $stderr" }
