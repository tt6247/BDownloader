param(
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\app'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\releases\BDownloader.exe'),
    [switch]$SkipAppBuild
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$application = [IO.Path]::GetFullPath($ApplicationDirectory)
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (!$SkipAppBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $application }
$compiler = Join-Path $workspace '.tools\roslyn-4.8.0\tasks\net472\csc.exe'
$references = Join-Path $workspace '.tools\net472-reference-1.0.3\build\.NETFramework\v4.7.2'
$launcherSource = Join-Path $workspace 'source\BundleLauncher\Program.cs'
foreach ($required in @($compiler, (Join-Path $references 'mscorlib.dll'), $launcherSource)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing build input: $required" }
}
$runtimeNames = @('BDownloader.exe', 'BDownloader.exe.config', 'Newtonsoft.Json.dll', 'QRCoder.dll', 'ffmpeg.exe',
    'FFmpeg_LICENSE.txt', 'yt-dlp.exe', 'qjs.exe', 'quickjs-runtime.json', 'media-tools.json', 'LICENSE.txt', 'aria2c.exe')
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$build = Join-Path $workspace "build\single-file\$runId"
$stage = Join-Path $build 'staging'
$payload = Join-Path $stage 'payload'
$payloadZip = Join-Path $build 'payload.zip'
$bundleInfo = Join-Path $build 'BundleInfo.cs'
$applicationManifest = Join-Path $build 'launcher.manifest'
$temporaryExe = Join-Path $build 'BDownloader.exe'
$utf8 = [Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Path $payload -Force | Out-Null
foreach ($name in $runtimeNames) {
    $inputFile = Join-Path $application $name
    if (!(Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw "Missing application payload: $name" }
    Copy-Item -LiteralPath $inputFile -Destination $payload
}
$notices = Join-Path $payload 'ThirdPartyLicenses'
New-Item -ItemType Directory -Path $notices | Out-Null
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $application 'ThirdPartyLicenses') -File) {
    if ($file.Name -eq 'quickjs-release-proof.json') { Copy-Item -LiteralPath $file.FullName -Destination $notices; continue }
    if ($file.Name -notmatch '^(yt-dlp|FFmpeg|Newtonsoft\.Json|QRCoder|quickjs|qjs|aria2)[A-Za-z0-9_.-]*$' -or
        $file.Name -notmatch '(?i)(LICENSE|COPYING|NOTICE|SHA(?:2-)?256|sha256sum)' -or
        !($file.Name -match '(?i)\.(txt|md|sha256sum)$' -or $file.Name.EndsWith('LICENSE') -or $file.Name -eq 'yt-dlp-SHA2-256SUMS')) { continue }
    Copy-Item -LiteralPath $file.FullName -Destination $notices
}
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/'); Size = $_.Length;
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ SchemaVersion = 1; Files = $files }
[IO.File]::WriteAllText((Join-Path $stage 'bundle-manifest.json'), ($manifest | ConvertTo-Json -Depth 5), $utf8)
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$fixedTimestamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$zipFile = [IO.File]::Open($payloadZip, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$archive = [IO.Compression.ZipArchive]::new($zipFile, [IO.Compression.ZipArchiveMode]::Create, $true)
try {
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
        $file.LastWriteTimeUtc = $fixedTimestamp.UtcDateTime
        $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $fixedTimestamp
        $inputStream = [IO.File]::OpenRead($file.FullName)
        $entryStream = $entry.Open()
        try { $inputStream.CopyTo($entryStream) }
        finally { $entryStream.Dispose(); $inputStream.Dispose() }
    }
}
finally { $archive.Dispose(); $zipFile.Dispose() }
$payloadHash = (Get-FileHash -LiteralPath $payloadZip -Algorithm SHA256).Hash.ToLowerInvariant()
$payloadBytes = (Get-Item -LiteralPath $payloadZip).Length
$infoSource = @"
using System.Reflection;
[assembly: AssemblyTitle("BDownloader")]
[assembly: AssemblyProduct("BDownloader")]
[assembly: AssemblyDescription("BDownloader single-file launcher")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.7.2")]
namespace BDownloader.BundleLauncher {
    internal static class BundleInfo {
        internal const string PayloadSha256 = "$payloadHash";
        internal const long PayloadZipLength = ${payloadBytes}L;
        internal const int ExpectedFileCount = $($files.Count);
    }
}
"@
[IO.File]::WriteAllText($bundleInfo, $infoSource, $utf8)
$manifestXml = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="BDownloader.BundleLauncher" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges>
    <requestedExecutionLevel level="asInvoker" uiAccess="false" />
  </requestedPrivileges></security></trustInfo>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
    <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
  </windowsSettings></application>
</assembly>
'@
[IO.File]::WriteAllText($applicationManifest, $manifestXml, $utf8)
$arguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/deterministic+', '/langversion:latest', '/nostdlib+',
    "/out:$temporaryExe", "/win32icon:$(Join-Path $workspace 'source\BDownloader\Resources\AppIcon.ico')", "/win32manifest:$applicationManifest", "/resource:$payloadZip,BDownloader.Payload.zip", $launcherSource, $bundleInfo)
foreach ($name in @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Security.dll', 'System.Runtime.Serialization.dll',
    'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Xml.dll')) {
    $arguments += "/reference:$(Join-Path $references $name)"
}
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Single-file launcher compilation failed.' }
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($OutputPath)) -Force | Out-Null
Copy-Item -LiteralPath $temporaryExe -Destination $OutputPath -Force
$launcherHash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()
$audit = [ordered]@{
    SchemaVersion = 1; CreatedUtc = [DateTime]::UtcNow.ToString('o'); FileName = [IO.Path]::GetFileName($OutputPath)
    Bytes = (Get-Item -LiteralPath $OutputPath).Length; Sha256 = $launcherHash
    PayloadSha256 = $payloadHash; PayloadZipBytes = $payloadBytes; RuntimeFileCount = $files.Count
    JavaScriptRuntime = 'quickjs'; ApplicationSha256 = ($files | Where-Object { $_.Path -eq 'payload/BDownloader.exe' }).Sha256
    Files = $files
}
[IO.File]::WriteAllText("$OutputPath.manifest.json", ($audit | ConvertTo-Json -Depth 6), $utf8)
[IO.File]::WriteAllText("$OutputPath.sha256", $launcherHash + '  ' + [IO.Path]::GetFileName($OutputPath) + "`n", $utf8)
Write-Output "Single executable: $OutputPath"
Write-Output "Bytes: $($audit.Bytes); compressed runtime: $payloadBytes; runtime files: $($files.Count); JavaScript: QuickJS-NG"
Write-Output "SHA256: $launcherHash"
