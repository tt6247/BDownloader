param(
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\app'),
    [string]$OutputArchive = (Join-Path $PSScriptRoot '..\releases\BDownloader-Windows-x64.zip'),
    [string]$ExpectedApplicationSha256
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$application = [IO.Path]::GetFullPath($ApplicationDirectory)
$OutputArchive = [IO.Path]::GetFullPath($OutputArchive)
$source = Join-Path $workspace 'source\BDownloader'
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$stage = Join-Path $workspace "build\packages\$runId\staging"
$extracted = Join-Path $workspace "build\packages\$runId\extracted"
$temporaryArchive = Join-Path ([IO.Path]::GetDirectoryName($OutputArchive)) ('.package-' + $runId + '.zip')
$utf8 = [Text.UTF8Encoding]::new($false)
$runtimeFiles = @(
    'BDownloader.exe', 'BDownloader.exe.config', 'Newtonsoft.Json.dll', 'QRCoder.dll',
    'ffmpeg.exe', 'FFmpeg_LICENSE.txt', 'yt-dlp.exe', 'qjs.exe', 'media-tools.json', 'quickjs-runtime.json', 'LICENSE.txt'
)
$scriptNames = @('build.ps1', 'build-single-file.ps1', 'prepare-build-tools.ps1', 'prepare-app-dependencies.ps1', 'prepare-media-tools.ps1', 'prepare-quickjs.ps1', 'verify.ps1',
    'verify-ui.ps1', 'verify-online.ps1', 'verify-login.ps1', 'verify-single-file.ps1', 'package.ps1')
$sourceExtensions = @('.cs', '.xaml', '.csproj', '.config', '.md', '.txt', '.png', '.jpg', '.jpeg',
    '.ico', '.svg', '.resx', '.settings', '.manifest', '.sln')

function Is-Notice([string]$Name) {
    if ($Name -eq 'quickjs-release-proof.json') { return $true }
    return $Name -match '(?i)(license|copying|notice|sha(?:2-)?256|sha256sum)' -and
        ($Name -match '(?i)\.(txt|md|sha256sum)$' -or $Name -eq 'yt-dlp-SHA2-256SUMS')
}

function Is-SourcePath([string]$Relative) {
    if ($Relative -match '(?i)(^|/)(bin|obj|\.git|\.vs|Config|Storage|logs|Media|downloads|sessions?)(/|$)') { return $false }
    if ($Relative -match '(?i)(\.Restore.*\.csproj$|_wpftmp\.|\.g(?:\.i)?\.cs$|\.user$|\.suo$|\.tmp$)') { return $false }
    if ($Relative -match '(?i)(^|/)(cookies?|sessions?)(\.[^/]*)?$') { return $false }
    if ($Relative.StartsWith('lib/', [StringComparison]::OrdinalIgnoreCase)) {
        $name = $Relative.Substring(4)
        return $name -notmatch '/' -and ($name -in @('Newtonsoft.Json.dll', 'QRCoder.dll', 'ffmpeg.exe') -or (Is-Notice $name))
    }
    return $Relative -eq 'LICENSE' -or [IO.Path]::GetExtension($Relative).ToLowerInvariant() -in $sourceExtensions
}

function Is-PackagePath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.StartsWith('/') -or $Path.Contains('\') -or
        $Path.Contains(':') -or $Path -match '(^|/)\.\.?(/|$)') { return $false }
    if ($Path -in @('README.md', 'launch.cmd', 'SHA256SUMS')) { return $true }
    if ($Path -eq 'source/BundleLauncher/Program.cs') { return $true }
    if ($Path.StartsWith('app/', [StringComparison]::Ordinal)) {
        $relative = $Path.Substring(4)
        if ($relative -in $runtimeFiles) { return $true }
        if ($relative.StartsWith('ThirdPartyLicenses/', [StringComparison]::Ordinal)) {
            $name = $relative.Substring('ThirdPartyLicenses/'.Length)
            return $name -notmatch '/' -and (Is-Notice $name)
        }
        return $false
    }
    if ($Path.StartsWith('source/BDownloader/', [StringComparison]::Ordinal)) {
        return Is-SourcePath $Path.Substring('source/BDownloader/'.Length)
    }
    if ($Path.StartsWith('scripts/', [StringComparison]::Ordinal)) { return $Path.Substring(8) -in $scriptNames }
    if ($Path.StartsWith('tests/standalone/', [StringComparison]::Ordinal)) {
        return $Path -match '(?i)\.(cs|ps1)$' -and $Path -notmatch '(?i)(^|/)(bin|obj|\.git|\.vs)(/|$)'
    }
    return $false
}

function Copy-PackageFile([string]$From, [string]$Relative) {
    $Relative = $Relative.Replace('\', '/')
    if (!(Is-PackagePath $Relative)) { throw "File is outside the package allowlist: $Relative" }
    if (!(Test-Path -LiteralPath $From -PathType Leaf)) { throw "Missing package input: $From" }
    $target = Join-Path $stage $Relative.Replace('/', '\')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::Copy($From, $target, $false)
}

foreach ($name in $runtimeFiles) {
    if (!(Test-Path -LiteralPath (Join-Path $application $name) -PathType Leaf)) { throw "Missing runtime $name. Run scripts\build.ps1 first." }
}
foreach ($required in @((Join-Path $source 'BDownloader.csproj'), (Join-Path $source 'LICENSE'),
    (Join-Path $workspace 'README.md'), (Join-Path $workspace 'launch.cmd'))) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing package input: $required" }
}
$applicationSha256 = (Get-FileHash -LiteralPath (Join-Path $application 'BDownloader.exe') -Algorithm SHA256).Hash
if ($ExpectedApplicationSha256 -and $applicationSha256 -ne $ExpectedApplicationSha256) { throw 'Application differs from the expected verified build.' }
[IO.Directory]::CreateDirectory($stage) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputArchive)) | Out-Null

# Explicit inputs only: saved application data is never enumerated or copied.
foreach ($name in $runtimeFiles) { Copy-PackageFile (Join-Path $application $name) "app/$name" }
foreach ($notice in Get-ChildItem -LiteralPath (Join-Path $application 'ThirdPartyLicenses') -File) {
    if (Is-Notice $notice.Name) { Copy-PackageFile $notice.FullName ('app/ThirdPartyLicenses/' + $notice.Name) }
}
$sourcePrefix = $source.TrimEnd('\') + '\'
foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File -Force) {
    $relative = $file.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
    if (Is-SourcePath $relative) { Copy-PackageFile $file.FullName ('source/BDownloader/' + $relative) }
}
foreach ($name in $scriptNames) {
    $inputScript = Join-Path $PSScriptRoot $name
    if (Test-Path -LiteralPath $inputScript -PathType Leaf) { Copy-PackageFile $inputScript "scripts/$name" }
}
$bundleSource = Join-Path $workspace 'source\BundleLauncher\Program.cs'
if (Test-Path -LiteralPath $bundleSource -PathType Leaf) { Copy-PackageFile $bundleSource 'source/BundleLauncher/Program.cs' }
$tests = Join-Path $workspace 'tests\standalone'
foreach ($file in Get-ChildItem -LiteralPath $tests -Recurse -File) {
    $relative = $file.FullName.Substring($tests.TrimEnd('\').Length + 1).Replace('\', '/')
    if ($relative -match '(?i)\.(cs|ps1)$' -and $relative -notmatch '(?i)(^|/)(bin|obj|\.git|\.vs)(/|$)') {
        Copy-PackageFile $file.FullName ('tests/standalone/' + $relative)
    }
}
Copy-PackageFile (Join-Path $workspace 'launch.cmd') 'launch.cmd'
$launcher = [IO.File]::ReadAllText((Join-Path $stage 'launch.cmd'))
if ($launcher.ToCharArray() | Where-Object { [int]$_ -gt 127 }) { throw 'The portable launcher must contain ASCII characters only.' }
if ($launcher -notmatch '(?i)%~dp0app' -or $launcher -notmatch 'BDownloader\.exe') { throw 'The launcher does not point at the packaged application.' }

# Make documentation links portable while keeping the workspace README intact.
$readme = [IO.File]::ReadAllText((Join-Path $workspace 'README.md'))
$readme = [regex]::Replace($readme, '\[启动BDownloader\.cmd\]\([^)]*\)', '[launch.cmd](launch.cmd)')
$readme = $readme.Replace($workspace.Replace('\', '/') + '/', '').Replace($workspace + '\', '')
$readme = [regex]::Replace($readme, '(?m)^\|\s*`archive/`[^\r\n]*(?:\r?\n)?', '')
$readme = [regex]::Replace($readme, '(?m)^最新测试记录位于[^\r\n]*', '运行验证脚本后，测试记录写入 `analysis/verification/`。')
[IO.File]::WriteAllText((Join-Path $stage 'README.md'), $readme, $utf8)

$stagedExe = Join-Path $stage 'app\BDownloader.exe'
$assembly = [Reflection.Assembly]::LoadFile($stagedExe)
if ($assembly.GetName().Name -ne 'BDownloader') { throw 'Incorrect application assembly identity.' }
$references = @($assembly.GetReferencedAssemblies() | Select-Object -ExpandProperty Name | Sort-Object)
foreach ($reference in $references) {
    if ($reference -notmatch '^(mscorlib|System(?:\..+)?|Microsoft\.CSharp|PresentationFramework|PresentationCore|WindowsBase|Newtonsoft\.Json|QRCoder)$') {
        throw "Unapproved application assembly reference: $reference"
    }
}

$manifest = [Collections.Generic.List[object]]::new()
$stagePrefix = $stage.TrimEnd('\') + '\'
foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName) {
    $relative = $file.FullName.Substring($stagePrefix.Length).Replace('\', '/')
    if (!(Is-PackagePath $relative)) { throw "Unexpected staged file: $relative" }
    $manifest.Add([pscustomobject]@{ Path = $relative; Bytes = $file.Length; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() })
}
$manifestText = (@($manifest | ForEach-Object { $_.Sha256 + '  ' + $_.Path }) -join "`n") + "`n"
[IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS'), $manifestText, $utf8)
if ((Get-FileHash -LiteralPath (Join-Path $application 'BDownloader.exe') -Algorithm SHA256).Hash -ne $applicationSha256) {
    throw 'Application changed while packaging. Run again after the build finishes.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporaryArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
$archive = [IO.Compression.ZipFile]::OpenRead($temporaryArchive)
try {
    $entries = @($archive.Entries | ForEach-Object { $_ })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        if (!(Is-PackagePath $entry.FullName) -or !$seen.Add($entry.FullName)) { throw "Unexpected or duplicate ZIP entry: $($entry.FullName)" }
    }
    if ($entries.Count -ne $manifest.Count + 1) { throw 'ZIP file entry count differs from the staged manifest.' }
}
finally { $archive.Dispose() }

# Perform a real extraction into another fresh directory, retaining both stages
# for review. Verify every extracted file, not merely the ZIP directory listing.
[IO.Compression.ZipFile]::ExtractToDirectory($temporaryArchive, $extracted)
$extractedFiles = @(Get-ChildItem -LiteralPath $extracted -Recurse -File)
if ($extractedFiles.Count -ne $manifest.Count + 1) { throw 'Extracted file count differs from the staged manifest.' }
foreach ($record in $manifest) {
    $path = Join-Path $extracted $record.Path.Replace('/', '\')
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $record.Sha256) {
        throw "Extracted file checksum mismatch: $($record.Path)"
    }
}
if ([IO.File]::ReadAllText((Join-Path $extracted 'SHA256SUMS')) -ne $manifestText) { throw 'Extracted checksum manifest differs.' }
Move-Item -LiteralPath $temporaryArchive -Destination $OutputArchive -Force
$archiveSha256 = (Get-FileHash -LiteralPath $OutputArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$OutputArchive.sha256", $archiveSha256 + '  ' + [IO.Path]::GetFileName($OutputArchive) + "`n", $utf8)
$audit = [ordered]@{
    SchemaVersion = 1
    CreatedUtc = [DateTime]::UtcNow.ToString('o')
    Archive = [IO.Path]::GetFileName($OutputArchive)
    ArchiveBytes = (Get-Item -LiteralPath $OutputArchive).Length
    ArchiveSha256 = $archiveSha256
    ApplicationSha256 = $applicationSha256.ToLowerInvariant()
    AssemblyName = $assembly.GetName().Name
    AssemblyReferences = $references
    FileEntries = $manifest.Count + 1
    ExtractedFiles = $extractedFiles.Count
    EveryExtractedHashVerified = $true
    StagingDirectory = $stage
    ExtractionDirectory = $extracted
    Files = $manifest.ToArray()
}
[IO.File]::WriteAllText("$OutputArchive.manifest.json", ($audit | ConvertTo-Json -Depth 6), $utf8)
Write-Output "Packaged: $OutputArchive"
Write-Output "Bytes: $($audit.ArchiveBytes); files: $($audit.FileEntries); verified extraction: $($audit.ExtractedFiles)"
Write-Output "SHA256: $archiveSha256"
