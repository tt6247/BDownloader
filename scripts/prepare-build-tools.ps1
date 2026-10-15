param([string]$ToolsDirectory = (Join-Path $PSScriptRoot '..\.tools'))
$ErrorActionPreference = 'Stop'
$ToolsDirectory = [IO.Path]::GetFullPath($ToolsDirectory)
New-Item -ItemType Directory -Path $ToolsDirectory -Force | Out-Null
$packages = @(
    @{ Id = 'microsoft.net.compilers.toolset'; Version = '4.8.0'; Folder = 'roslyn-4.8.0'; Sha256 = '37333F4F1E2CE55E621355D6DA651DC23D4CB5F94A8F76B9478816E87F110AD9' },
    @{ Id = 'microsoft.netframework.referenceassemblies.net472'; Version = '1.0.3'; Folder = 'net472-reference-1.0.3'; Sha256 = 'FFA0A5570A39F911399164D0581FFDDEF99B5E3DFBAA5F220E5CE22969BCF57C' }
)
foreach ($package in $packages) {
    $archive = Join-Path $ToolsDirectory "$($package.Folder).zip"
    $url = "https://api.nuget.org/v3-flatcontainer/$($package.Id)/$($package.Version)/$($package.Id).$($package.Version).nupkg"
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $url -OutFile $archive }
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actualHash -ne $package.Sha256) { throw "Unexpected SHA-256 for $archive : $actualHash" }
    $folder = Join-Path $ToolsDirectory $package.Folder
    if (!(Test-Path -LiteralPath $folder)) { Expand-Archive -LiteralPath $archive -DestinationPath $folder }
    Write-Output "Ready: $folder"
}
