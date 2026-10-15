param(
    [string]$ToolsDirectory = (Join-Path $PSScriptRoot '..\.tools\media'),
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\app')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ToolsDirectory = [IO.Path]::GetFullPath($ToolsDirectory)
$ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory)
New-Item -ItemType Directory -Path $ToolsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $ApplicationDirectory -Force | Out-Null

# Stable official Windows x64 releases, checked against the upstream release
# manifests on 2026-10-15. Update the versions AND their pinned hashes together.
$YtDlpVersion = '2026.08.19'
$YtDlpSha256 = '66674953FE251B89F4D08C5F0E35E0728679BD67AB3D7D05C0562AF101DD3E7A'
$YtDlpBaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/$YtDlpVersion"

function Assert-Sha256([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Expected) {
        throw "SHA-256 mismatch for '$Path'. Expected $Expected, received $actual. No executable has been installed."
    }
}

function Get-VerifiedFile([string]$Url, [string]$FileName, [string]$Sha256) {
    $path = Join-Path $ToolsDirectory $FileName
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        $partialPath = "$path.download"
        Invoke-WebRequest -Uri $Url -OutFile $partialPath -UseBasicParsing -TimeoutSec 180
        Assert-Sha256 $partialPath $Sha256
        Move-Item -LiteralPath $partialPath -Destination $path -Force
    }
    Assert-Sha256 $path $Sha256
    return $path
}

function Copy-VerifiedFile([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash) { return }
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

$ytManifest = Get-VerifiedFile "$YtDlpBaseUrl/SHA2-256SUMS" 'yt-dlp-SHA2-256SUMS' 'A63701F30755CB4D9317950D69703E9D751D490CB1B7059E1BB501A353FE7DCB'

$ytManifestText = Get-Content -LiteralPath $ytManifest -Raw
if ($ytManifestText -notmatch "(?im)^$YtDlpSha256\s+\*?yt-dlp\.exe\s*$") {
    throw 'The pinned yt-dlp executable hash is absent from its official checksum manifest.'
}
$ytExe = Get-VerifiedFile "$YtDlpBaseUrl/yt-dlp.exe" 'yt-dlp.exe' $YtDlpSha256
$licenses = @(
    @{ Name = 'yt-dlp-LICENSE.txt'; Url = "https://raw.githubusercontent.com/yt-dlp/yt-dlp/$YtDlpVersion/LICENSE"; Sha256 = '7E12E5DF4BAE12CB21581BA157CED20E1986A0508DD10D0E8A4AB9A4CF94E85C' },
    @{ Name = 'yt-dlp-THIRD_PARTY_LICENSES.txt'; Url = "https://raw.githubusercontent.com/yt-dlp/yt-dlp/$YtDlpVersion/THIRD_PARTY_LICENSES.txt"; Sha256 = '472AEFE951C7DB35E1657C1D13FD337140511ED6F2B329205105AD441C5A02B7' }
)
$licensePaths = @()
foreach ($license in $licenses) {
    $licensePaths += Get-VerifiedFile $license.Url $license.Name $license.Sha256
}

# Install only after all downloads, licenses, manifests and extracted files pass.
Copy-VerifiedFile $ytExe (Join-Path $ApplicationDirectory 'yt-dlp.exe')
$licenseDirectory = Join-Path $ApplicationDirectory 'ThirdPartyLicenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
foreach ($licensePath in $licensePaths) {
    Copy-VerifiedFile $licensePath (Join-Path $licenseDirectory ([IO.Path]::GetFileName($licensePath)))
}
Copy-VerifiedFile $ytManifest (Join-Path $licenseDirectory ([IO.Path]::GetFileName($ytManifest)))

& (Join-Path $PSScriptRoot 'prepare-quickjs.ps1') -ToolsDirectory (Join-Path $ToolsDirectory 'quickjs') -ApplicationDirectory $ApplicationDirectory
$quickJsMetadata = Get-Content -LiteralPath (Join-Path $ApplicationDirectory 'quickjs-runtime.json') -Raw | ConvertFrom-Json

$metadata = [ordered]@{
    checkedDate = '2026-10-15'
    platform = 'windows-x64'
    quickJs = $quickJsMetadata
    ytDlp = [ordered]@{
        version = $YtDlpVersion
        sha256 = $YtDlpSha256.ToLowerInvariant()
        source = "$YtDlpBaseUrl/yt-dlp.exe"
        sourceCode = "https://github.com/yt-dlp/yt-dlp/tree/$YtDlpVersion"
        manifest = "$YtDlpBaseUrl/SHA2-256SUMS"
    }

}
$metadataJson = $metadata | ConvertTo-Json -Depth 5
$metadataJson | Set-Content -LiteralPath (Join-Path $ToolsDirectory 'media-tools.json') -Encoding UTF8
$metadataJson | Set-Content -LiteralPath (Join-Path $ApplicationDirectory 'media-tools.json') -Encoding UTF8
Write-Output "Ready: yt-dlp $YtDlpVersion and QuickJS-NG $($quickJsMetadata.version) in $ApplicationDirectory"
