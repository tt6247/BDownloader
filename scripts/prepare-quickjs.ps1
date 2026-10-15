param(
    [string]$ToolsDirectory = (Join-Path $PSScriptRoot '..\.tools\media\quickjs'),
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\app')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ToolsDirectory = [IO.Path]::GetFullPath($ToolsDirectory)
$ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory)
New-Item -ItemType Directory -Path $ToolsDirectory, $ApplicationDirectory -Force | Out-Null

# Official QuickJS-NG release, checked against GitHub's upstream asset digest.
# Update version, size and both hashes together when changing this pinned release.
$Version = '0.17.0'
$AssetName = 'qjs-windows-x86_64.exe'
$ExpectedSize = 2151363
$ExecutableSha256 = '2AEABF0092C3262D6B2609824418F7DD7ED1F1DF939F73B2B15645230CAC0D77'
$LicenseSha256 = '96F73F9D2A16C21A36B418F06073BE26E7D6D5E7C1BC99756B21A4F2C74EF171'
$ReleaseApi = "https://api.github.com/repos/quickjs-ng/quickjs/releases/tags/v$Version"
$ExecutableUrl = "https://github.com/quickjs-ng/quickjs/releases/download/v$Version/$AssetName"
$LicenseUrl = "https://raw.githubusercontent.com/quickjs-ng/quickjs/v$Version/LICENSE"
$ProofFile = Join-Path $ToolsDirectory 'quickjs-release-proof.json'

function Assert-Sha256([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Expected) { throw "SHA-256 mismatch for '$Path'; no runtime has been installed." }
}

function Get-VerifiedFile([string]$Url, [string]$Name, [string]$Hash) {
    $path = Join-Path $ToolsDirectory $Name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        $partial = "$path.download"
        try {
            Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing -TimeoutSec 90
            Assert-Sha256 $partial $Hash
            Move-Item -LiteralPath $partial -Destination $path -Force
        }
        finally { if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial } }
    }
    Assert-Sha256 $path $Hash
    return $path
}

if (!(Test-Path -LiteralPath $ProofFile -PathType Leaf)) {
    $release = Invoke-RestMethod -Uri $ReleaseApi -Headers @{ 'User-Agent' = 'BDownloader-runtime-preparation'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 30
    $asset = @($release.assets | Where-Object { $_.name -eq $AssetName })
    if ($release.tag_name -ne "v$Version" -or $asset.Count -ne 1 -or $asset[0].browser_download_url -ne $ExecutableUrl -or
        $asset[0].size -ne $ExpectedSize -or $asset[0].digest -ne ('sha256:' + $ExecutableSha256.ToLowerInvariant())) {
        throw 'The official release asset metadata does not match the pinned version, size or SHA-256.'
    }
    [ordered]@{
        release = $release.tag_name; asset = $AssetName; size = $ExpectedSize
        digest = $asset[0].digest; source = $ExecutableUrl; manifest = $ReleaseApi
        checkedDate = '2026-10-15'
    } | ConvertTo-Json | Set-Content -LiteralPath $ProofFile -Encoding UTF8
}
$proof = Get-Content -LiteralPath $ProofFile -Raw | ConvertFrom-Json
if ($proof.release -ne "v$Version" -or $proof.asset -ne $AssetName -or $proof.size -ne $ExpectedSize -or
    $proof.digest -ne ('sha256:' + $ExecutableSha256.ToLowerInvariant()) -or $proof.source -ne $ExecutableUrl) {
    throw 'Cached QuickJS release evidence does not match the pinned release.'
}
$executable = Get-VerifiedFile $ExecutableUrl 'qjs.exe' $ExecutableSha256
$license = Get-VerifiedFile $LicenseUrl 'QuickJS-NG-LICENSE.txt' $LicenseSha256
if ((Get-Item -LiteralPath $executable).Length -ne $ExpectedSize) { throw 'QuickJS runtime size differs from its official release metadata.' }

# Install only after the executable, license and upstream checksum all pass.
$installedRuntime = Join-Path $ApplicationDirectory 'qjs.exe'
if (!(Test-Path -LiteralPath $installedRuntime -PathType Leaf) -or (Get-FileHash -LiteralPath $installedRuntime -Algorithm SHA256).Hash -ne $ExecutableSha256) {
    Copy-Item -LiteralPath $executable -Destination $installedRuntime -Force
}
$licenses = Join-Path $ApplicationDirectory 'ThirdPartyLicenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath $license, $ProofFile -Destination $licenses -Force
[ordered]@{
    name = 'QuickJS-NG'; version = $Version; platform = 'windows-x64'; file = 'qjs.exe'
    bytes = $ExpectedSize; sha256 = $ExecutableSha256.ToLowerInvariant(); source = $ExecutableUrl
    sourceCode = "https://github.com/quickjs-ng/quickjs/tree/v$Version"; upstreamDigestApi = $ReleaseApi
    license = 'MIT'; licenseSha256 = $LicenseSha256.ToLowerInvariant()
    runtimeArgument = 'quickjs:<path-to-qjs.exe>'
    documentation = 'https://github.com/yt-dlp/yt-dlp/wiki/EJS'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ApplicationDirectory 'quickjs-runtime.json') -Encoding UTF8
Write-Output "Ready: QuickJS-NG $Version, $ExpectedSize bytes, SHA-256 $($ExecutableSha256.ToLowerInvariant())"
Write-Output "Runtime: --no-js-runtimes --js-runtimes quickjs:$([IO.Path]::Combine($ApplicationDirectory, 'qjs.exe'))"
