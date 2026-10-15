param(
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\app'),
    [switch]$InspectOnly,
    [ValidateRange(2, 15)][int]$TimeoutMinutes = 8
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$application = [IO.Path]::GetFullPath($ApplicationDirectory)
$verification = Join-Path $workspace 'analysis\verification'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$hostDirectory = Join-Path $verification "standalone-live-host\$runId"
$artifacts = Join-Path $verification "standalone-live\$runId"
$references = Join-Path $workspace '.tools\net472-reference-1.0.3\build\.NETFramework\v4.7.2'
$compiler = Join-Path $workspace '.tools\roslyn-4.8.0\tasks\net472\csc.exe'
$programSource = Join-Path $application 'BDownloader.exe'
$dependencies = @('Newtonsoft.Json.dll', 'QRCoder.dll')
foreach ($required in @($compiler, $programSource, (Join-Path $application 'yt-dlp.exe'),
    (Join-Path $application 'ffmpeg.exe'), (Join-Path $application 'qjs.exe')) + @($dependencies | ForEach-Object { Join-Path $application $_ })) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing $required. Run scripts\build.ps1 first." }
}
New-Item -ItemType Directory -Path $hostDirectory, $artifacts -Force | Out-Null
# Copy only application assemblies into the host. No saved account, browser,
# queue or application configuration directory is read or copied.
Copy-Item -LiteralPath $programSource -Destination $hostDirectory
foreach ($dependency in $dependencies) { Copy-Item -LiteralPath (Join-Path $application $dependency) -Destination $hostDirectory }
$testExe = Join-Path $hostDirectory 'LiveDownloadTests.exe'
$arguments = @('/nologo', '/target:exe', '/langversion:latest', '/nostdlib+', "/out:$testExe", (Join-Path $workspace 'tests\standalone\LiveDownloadTests.cs'))
$arguments += Get-ChildItem -LiteralPath $references -Filter '*.dll' -File |
    Where-Object Name -NotIn @('System.EnterpriseServices.Wrapper.dll', 'System.EnterpriseServices.Thunk.dll') |
    ForEach-Object { "/reference:$($_.FullName)" }
$arguments += "/reference:$(Join-Path $hostDirectory 'BDownloader.exe')"
$arguments += $dependencies | ForEach-Object { "/reference:$(Join-Path $hostDirectory $_)" }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Standalone live harness compilation failed.' }
if (Test-Path -LiteralPath "$programSource.config") { Copy-Item -LiteralPath "$programSource.config" -Destination "$testExe.config" }

function ConvertTo-NativeArgument([string]$Value) {
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
$stdout = Join-Path $verification 'standalone-live.log'
$stderr = Join-Path $verification 'standalone-live-errors.log'
$programArguments = (ConvertTo-NativeArgument $application) + ' ' + (ConvertTo-NativeArgument $artifacts)
if ($InspectOnly) { $programArguments += ' --inspect-only' }
$started = [DateTime]::UtcNow
$printed = 0
$process = Start-Process -FilePath $testExe -ArgumentList $programArguments -WorkingDirectory $hostDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
try {
    while (!$process.WaitForExit(1000)) {
        if (([DateTime]::UtcNow - $started).TotalMinutes -gt $TimeoutMinutes) {
            & (Join-Path $env:WINDIR 'System32\taskkill.exe') /PID $process.Id /T /F 2>&1 | Out-Null
            throw "Standalone live verification timed out. See $stdout"
        }
        $lines = @(Get-Content -LiteralPath $stdout -Encoding UTF8)
        if ($lines.Count -gt $printed) { $lines | Select-Object -Skip $printed; $printed = $lines.Count }
    }
    $process.WaitForExit()
    $lines = @(Get-Content -LiteralPath $stdout -Encoding UTF8)
    if ($lines.Count -gt $printed) { $lines | Select-Object -Skip $printed }
    $passed = @($lines | Where-Object { $_ -match '^PASS ' }).Count
    $expected = if ($InspectOnly) { 3 } else { 6 }
    if ($process.ExitCode -ne 0 -or $passed -ne $expected -or @($lines | Where-Object { $_ -match '^FAIL ' }).Count -gt 0) {
        throw "Standalone live verification failed. See $stdout and $stderr"
    }
}
finally {
    $summary = [ordered]@{
        StartedUtc = $started.ToString('o')
        CompletedUtc = [DateTime]::UtcNow.ToString('o')
        ApplicationSha256 = (Get-FileHash -LiteralPath (Join-Path $hostDirectory 'BDownloader.exe') -Algorithm SHA256).Hash
        ApplicationDirectory = $application
        ArtifactsDirectory = $artifacts
        Log = $stdout
        InspectOnly = [bool]$InspectOnly
    }
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $verification 'standalone-live-summary.json') -Encoding UTF8
    $process.Dispose()
}
Write-Output "Standalone live artifacts: $artifacts"
Write-Output "Standalone live log: $stdout"
