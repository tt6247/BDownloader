param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot '..\source\BDownloader'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\app'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$SourceRoot = [IO.Path]::GetFullPath($SourceRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $SourceRoot 'BDownloader.csproj'
$libraryDirectory = Join-Path $SourceRoot 'lib'
$compilerDirectory = Join-Path $workspaceRoot '.tools\roslyn-4.8.0\tasks\net472'
$referenceRoot = Join-Path $workspaceRoot '.tools\net472-reference-1.0.3\build'
$referenceDirectory = Join-Path $referenceRoot '.NETFramework\v4.7.2'
$msbuild = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\MSBuild.exe'
$buildDirectory = Join-Path $workspaceRoot 'build\standalone'
$intermediateDirectory = Join-Path $buildDirectory "obj\$Configuration"
$buildTemp = Join-Path $buildDirectory 'temp'
$buildLog = Join-Path $buildDirectory 'build.log'
$configurationFile = Join-Path $buildDirectory 'BDownloader.exe.config'

function Copy-ChangedFile([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination -PathType Container) {
        $Destination = Join-Path $Destination ([IO.Path]::GetFileName($Source))
    }
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
        if ($sourceHash -eq $destinationHash) { return }
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

foreach ($required in @($project, (Join-Path $SourceRoot 'App.xaml'), (Join-Path $compilerDirectory 'csc.exe'), (Join-Path $referenceDirectory 'mscorlib.dll'), $msbuild)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing required build file: $required. Prepare the source and run scripts\prepare-build-tools.ps1 first." }
}
foreach ($name in @('Newtonsoft.Json.dll', 'QRCoder.dll', 'ffmpeg.exe', 'FFmpeg_LICENSE.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $libraryDirectory $name) -PathType Leaf)) { throw "Missing $name. Download BDownloader-build-dependencies.zip from Releases and run scripts\prepare-app-dependencies.ps1 -ArchivePath <zip-path>." }
}
$outputExe = Join-Path $OutputDirectory 'BDownloader.exe'
if (Test-Path -LiteralPath $outputExe -PathType Leaf) {
    try {
        $probe = [IO.File]::Open($outputExe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $probe.Dispose()
    }
    catch [IO.IOException] { throw "BDownloader is running. Close it or choose a separate OutputDirectory: $OutputDirectory" }
}
New-Item -ItemType Directory -Path $OutputDirectory,$buildDirectory,$intermediateDirectory,$buildTemp -Force | Out-Null

$configurationXml = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.7.2" />
  </startup>
  <runtime>
    <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
      <dependentAssembly>
        <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-13.0.0.0" newVersion="13.0.0.0" />
      </dependentAssembly>
    </assemblyBinding>
  </runtime>
</configuration>
'@
[IO.File]::WriteAllText($configurationFile, $configurationXml, [Text.UTF8Encoding]::new($false))
$arguments = @(
    $project, '/t:Build', '/nologo', '/v:minimal', "/p:Configuration=$Configuration", '/p:Platform=AnyCPU',
    "/p:OutDir=$OutputDirectory\", "/p:BaseIntermediateOutputPath=$buildDirectory\obj\",
    "/p:IntermediateOutputPath=$intermediateDirectory\", "/p:CscToolPath=$compilerDirectory\", '/p:CscToolExe=csc.exe',
    '/p:UseHostCompilerIfAvailable=false', '/p:LangVersion=latest',
    "/p:TargetFrameworkRootPath=$referenceRoot\", "/p:FrameworkPathOverride=$referenceDirectory\",
    "/p:AppConfig=$configurationFile", '/nodeReuse:false', '/fileLogger',
    "/fileLoggerParameters:LogFile=$buildLog;Verbosity=normal;Encoding=UTF-8"
)

# Framework MSBuild cannot format TEMP paths containing braces. Use a local
# build-only temporary directory and always restore the caller's environment.
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    $env:TEMP = $buildTemp
    $env:TMP = $buildTemp
    & $msbuild @arguments
    $buildExitCode = $LASTEXITCODE
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}
if ($buildExitCode -ne 0) { throw "Build failed with exit code $buildExitCode. See $buildLog" }
if (!(Test-Path -LiteralPath $outputExe -PathType Leaf)) { throw "Build did not produce $outputExe" }
if ([Reflection.AssemblyName]::GetAssemblyName($outputExe).Name -ne 'BDownloader') { throw 'Unexpected application assembly identity.' }
if ($Configuration -eq 'Release') {
    $debugSymbols = Join-Path $OutputDirectory 'BDownloader.pdb'
    if (Test-Path -LiteralPath $debugSymbols -PathType Leaf) { Remove-Item -LiteralPath $debugSymbols }
}

Copy-ChangedFile $configurationFile "$outputExe.config"
foreach ($name in @('Newtonsoft.Json.dll', 'QRCoder.dll', 'ffmpeg.exe', 'FFmpeg_LICENSE.txt')) {
    Copy-ChangedFile (Join-Path $libraryDirectory $name) $OutputDirectory
}
$notices = Join-Path $OutputDirectory 'ThirdPartyLicenses'
New-Item -ItemType Directory -Path $notices -Force | Out-Null
Get-ChildItem -LiteralPath $libraryDirectory -File | Where-Object { $_.Name -match '(?i)(license|copying|notice)' } | ForEach-Object { Copy-ChangedFile $_.FullName $notices }
if (Test-Path -LiteralPath (Join-Path $SourceRoot 'LICENSE') -PathType Leaf) {
    Copy-ChangedFile (Join-Path $SourceRoot 'LICENSE') (Join-Path $OutputDirectory 'LICENSE.txt')
}
& (Join-Path $PSScriptRoot 'prepare-media-tools.ps1') -ApplicationDirectory $OutputDirectory

foreach ($name in @('BDownloader.exe', 'BDownloader.exe.config', 'Newtonsoft.Json.dll', 'QRCoder.dll', 'ffmpeg.exe', 'yt-dlp.exe', 'qjs.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $OutputDirectory $name) -PathType Leaf)) { throw "Missing completed application dependency: $name" }
}
Write-Output "Built: $outputExe"
Write-Output "Build log: $buildLog"
