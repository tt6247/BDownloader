param(
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [string]$SourceRoot = (Join-Path $PSScriptRoot '..\source\BDownloader')
)

$ErrorActionPreference = 'Stop'
$ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
$libraryDirectory = Join-Path ([IO.Path]::GetFullPath($SourceRoot)) 'lib'
$archiveSha256 = '3a67cf0c5142a78a3520a1044fa2ed4445197fc1b9f91c4fb99fb5957f975857'
$files = @{
    'ffmpeg.exe' = '6001ad699ca9894c01273ee22474ea7f60de642764c374d1234a9a75c41763b1'
    'FFmpeg_LICENSE.txt' = '0b383d5a63da644f628d99c33976ea6487ed89aaa59f0b3257992deac1171e6b'
    'Newtonsoft.Json.dll' = 'c5c83bbc1741be6ff4c490c0aee34c162945423ec577c646538b2d21ce13199e'
    'Newtonsoft.Json-LICENSE.txt' = 'd7f83bbea3264a8e23d7739e44d9db1a4994b357274df6f00876eb2e623827ad'
    'QRCoder.dll' = '561acfe4b1a14c837b189fb9fc5c6d3e82440184bbde61912de723d62d6368b3'
    'QRCoder-LICENSE.txt' = '044bd5eeee412a603a6e2b6201c7256da386788aaa4392be9870a5e899adea05'
}
if (!(Test-Path -LiteralPath $ArchivePath -PathType Leaf)) { throw 'Download BDownloader-build-dependencies.zip from the repository Releases page first.' }
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $archiveSha256) { throw 'The dependency archive failed its pinned SHA-256 check.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    if ($archive.Entries.Count -ne $files.Count) { throw 'Unexpected dependency archive file count.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        if (!$files.ContainsKey($entry.FullName) -or !$seen.Add($entry.FullName)) { throw 'Unexpected or duplicate dependency archive entry.' }
        $inputStream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = ([BitConverter]::ToString($sha.ComputeHash($inputStream))).Replace('-', '').ToLowerInvariant() }
        finally { $inputStream.Dispose(); $sha.Dispose() }
        if ($actual -ne $files[$entry.FullName]) { throw "Dependency checksum mismatch: $($entry.FullName)" }
    }
    New-Item -ItemType Directory -Path $libraryDirectory -Force | Out-Null
    foreach ($entry in $archive.Entries) {
        $target = Join-Path $libraryDirectory $entry.FullName
        if ((Test-Path -LiteralPath $target -PathType Leaf) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $files[$entry.FullName]) { continue }
        $temporary = $target + '.tmp-' + [Guid]::NewGuid().ToString('N')
        try {
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
            if (Test-Path -LiteralPath $target -PathType Leaf) { [IO.File]::Replace($temporary, $target, $null) }
            else { [IO.File]::Move($temporary, $target) }
        }
        finally { if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary } }
    }
}
finally { $archive.Dispose() }
Write-Output "Verified build dependencies ready: $libraryDirectory"
