param(
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\app'),
    [switch]$Online,
    [ValidateRange(20, 120)][int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$application = [IO.Path]::GetFullPath($ApplicationDirectory)
$verification = Join-Path $workspace 'analysis\verification'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$sandbox = Join-Path $verification "auth-sandbox\standalone-login-$runId"
$compiler = Join-Path $workspace '.tools\roslyn-4.8.0\tasks\net472\csc.exe'
$references = Join-Path $workspace '.tools\net472-reference-1.0.3\build\.NETFramework\v4.7.2'
$programSource = Join-Path $application 'BDownloader.exe'
$dependencies = @('Newtonsoft.Json.dll', 'QRCoder.dll')
foreach ($required in @($compiler, $programSource, (Join-Path $references 'mscorlib.dll')) + @($dependencies | ForEach-Object { Join-Path $application $_ })) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing $required. Run scripts\build.ps1 first." }
}
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
# Only application assemblies are copied. Saved accounts, browser data and user
# configuration are excluded; the test refuses any preexisting account file.
Copy-Item -LiteralPath $programSource -Destination $sandbox
foreach ($dependency in $dependencies) { Copy-Item -LiteralPath (Join-Path $application $dependency) -Destination $sandbox }
$program = Join-Path $sandbox 'BilibiliLoginTests.exe'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nostdlib+', "/out:$program", (Join-Path $workspace 'tests\standalone\BilibiliLoginTests.cs'))
$arguments += Get-ChildItem -LiteralPath $references -Filter '*.dll' -File |
    Where-Object Name -NotIn @('System.EnterpriseServices.Wrapper.dll', 'System.EnterpriseServices.Thunk.dll') |
    ForEach-Object { "/reference:$($_.FullName)" }
$arguments += "/reference:$(Join-Path $sandbox 'BDownloader.exe')"
$arguments += $dependencies | ForEach-Object { "/reference:$(Join-Path $sandbox $_)" }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Standalone login regression compilation failed.' }
if (Test-Path -LiteralPath "$programSource.config") { Copy-Item -LiteralPath "$programSource.config" -Destination "$program.config" }

$stdout = Join-Path $verification 'standalone-login.log'
$stderr = Join-Path $verification 'standalone-login-errors.log'
$start = @{ FilePath = $program; WorkingDirectory = $sandbox; WindowStyle = 'Hidden';
    RedirectStandardOutput = $stdout; RedirectStandardError = $stderr; PassThru = $true }
if ($Online) { $start.ArgumentList = '--online' }
$started = [DateTime]::UtcNow
$process = Start-Process @start
$timedOut = $false
$exitCode = $null
$passed = 0
$failed = 0
try {
    while (!$process.WaitForExit(1000)) {
        if (([DateTime]::UtcNow - $started).TotalSeconds -gt $TimeoutSeconds) {
            $timedOut = $true
            & (Join-Path $env:WINDIR 'System32\taskkill.exe') /PID $process.Id /T /F 2>&1 | Out-Null
            throw "Standalone login verification timed out. See $stdout"
        }
    }
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $lines = @(Get-Content -LiteralPath $stdout -Encoding UTF8)
    $lines | Write-Output
    $passed = @($lines | Where-Object { $_ -match '^PASS ' }).Count
    $failed = @($lines | Where-Object { $_ -match '^FAIL ' }).Count
    $expected = if ($Online) { 12 } else { 11 }
    if ($exitCode -ne 0 -or $passed -ne $expected -or $failed -ne 0) { throw "Standalone login verification failed. See $stdout and $stderr" }
}
finally {
    $summary = [ordered]@{
        StartedUtc = $started.ToString('o'); CompletedUtc = [DateTime]::UtcNow.ToString('o')
        ApplicationSha256 = (Get-FileHash -LiteralPath (Join-Path $sandbox 'BDownloader.exe') -Algorithm SHA256).Hash
        Passed = $passed; Failed = $failed; ExitCode = $exitCode; TimedOut = $timedOut; Online = [bool]$Online
        Sandbox = $sandbox; OutputLog = $stdout; ErrorLog = $stderr
    }
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $verification 'standalone-login-summary.json') -Encoding UTF8
    $process.Dispose()
}
Write-Output "Standalone login log: $stdout"
