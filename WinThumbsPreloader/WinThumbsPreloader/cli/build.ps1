<#
.SYNOPSIS
    Builds WinThumbsPreloader-cli.

.DESCRIPTION
    The .NET Framework targeting packs are not installed on every machine, so this script
    points MSBuild at the runtime directory as a substitute reference assembly set.
    If MSBuild is unavailable it falls back to invoking csc.exe directly.

.PARAMETER Configuration
    Debug or Release (default).

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$projectFile = Join-Path $projectDir 'WinThumbsPreloader.Cli.csproj'
$outputDir = Join-Path $projectDir "bin\$Configuration"
$outputExe = Join-Path $outputDir 'WinThumbsPreloader-cli.exe'

# When the .NET Framework targeting pack is installed, MSBuild resolves reference
# assemblies from it. When only the runtime is installed, the runtime's own directory
# holds usable framework assemblies, so point MSBuild and csc there explicitly.
$frameworkDir = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath $frameworkDir)) {
    throw "Cannot find the .NET Framework runtime directory at '$frameworkDir'."
}

$msbuild = Join-Path $frameworkDir 'MSBuild.exe'
$csc = Join-Path $frameworkDir 'csc.exe'

$sources = @(
    (Join-Path $projectDir 'CliOptions.cs'),
    (Join-Path $projectDir 'CliProgram.cs'),
    (Join-Path $projectDir 'CliRunner.cs'),
    (Join-Path $projectDir '..\ThumbnailPreloader.cs'),
    (Join-Path $projectDir '..\DirectoryScanner.cs')
) | ForEach-Object { (Resolve-Path -LiteralPath $_).Path }

if (Test-Path -LiteralPath $msbuild) {
    $env:FrameworkPathOverride = $frameworkDir
    Write-Host "Building $projectFile ($Configuration) with $msbuild"
    $log = & $msbuild $projectFile "/p:Configuration=$Configuration" "/p:FrameworkPathOverride=$frameworkDir" '/v:minimal' '/nologo' 2>&1 | Out-String
    $log = $log.Trim()
    if ($log.Length -gt 0) { Write-Host $log }
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE."
    }
}
elseif (Test-Path -LiteralPath $csc) {
    if (-not (Test-Path -LiteralPath $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }
    $arguments = @(
        '/nologo', '/target:exe', "/out:$outputExe", '/optimize+', '/platform:anycpu',
        "/r:$frameworkDir\System.dll", "/r:$frameworkDir\System.Core.dll"
    ) + $sources
    Write-Host "Building with $csc (MSBuild not found)"
    & $csc $arguments
    if ($LASTEXITCODE -ne 0) {
        throw "csc.exe failed with exit code $LASTEXITCODE."
    }
}
else {
    throw "Neither MSBuild.exe nor csc.exe was found under '$frameworkDir'."
}

if (-not (Test-Path -LiteralPath $outputExe)) {
    throw "Build reported success but '$outputExe' does not exist."
}

Write-Host ''
Write-Host "Built $outputExe"
Write-Host "Try: `"$outputExe`" --help"
