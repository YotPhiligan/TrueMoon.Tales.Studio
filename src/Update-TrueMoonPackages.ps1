#Requires -Version 7.0
<#
.SYNOPSIS
Builds Studio's TrueMoon packages without writing build output to the framework checkout.
.EXAMPLE
./Update-TrueMoonPackages.ps1 -FrameworkRoot ../../TrueMoon
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $FrameworkRoot
)

$ErrorActionPreference = 'Stop'
$framework = (Resolve-Path -LiteralPath $FrameworkRoot).Path
$frameworkSource = Join-Path $framework 'src'
$studioSource = $PSScriptRoot
[xml] $versions = Get-Content -LiteralPath (Join-Path $studioSource 'Directory.Packages.props') -Raw
$version = $versions.SelectSingleNode('/Project/PropertyGroup/StudioTrueMoonVersion').InnerText
[xml] $frameworkProperties = Get-Content -LiteralPath (Join-Path $frameworkSource 'Directory.Build.props') -Raw
$frameworkVersion = $frameworkProperties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -ne $frameworkVersion) {
    throw "Studio expects $version; framework declares $frameworkVersion. Align Directory.Packages.props before packing."
}

$packages = @(
    'TrueMoon.Contracts', 'TrueMoon.Core', 'TrueMoon.Argentis', 'TrueMoon.Cobalt',
    'TrueMoon', 'TrueMoon.Alloy',
    'TrueMoon.Alloy.Platform.Silk', 'TrueMoon.Alloy.Rendering.Skia', 'TrueMoon.Alloy.Hosting'
)
foreach ($package in $packages) {
    if (!(Test-Path -LiteralPath (Join-Path $frameworkSource "$package/$package.csproj"))) {
        throw "Framework package project is missing: $package"
    }
}

$run = Join-Path $studioSource ('.local/truemoon-build/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$feed = Join-Path $studioSource '.local/truemoon-feed'
$artifacts = Join-Path $run 'artifacts'
$packageCache = Join-Path $run 'packages'
New-Item -ItemType Directory -Path $run, $feed -Force | Out-Null
foreach ($package in $packages) {
    if (Test-Path -LiteralPath (Join-Path $feed "$package.$version.nupkg")) {
        throw "Package already exists in the local feed: $package.$version. Use a new framework version; existing packages are immutable."
    }
}

# Source builds only need third-party packages; they must not resolve TrueMoon from a feed.
$restoreConfig = Join-Path $run 'NuGet.Config'
$cacheSource = [System.Security.SecurityElement]::Escape((Join-Path $env:USERPROFILE '.nuget/packages'))
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="cached-dependencies" value="$cacheSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $restoreConfig -Encoding utf8

function Invoke-PackageBuild([string] $operation, [string] $package, [string[]] $additionalArguments) {
    $project = Join-Path $frameworkSource "$package/$package.csproj"
    $log = Join-Path $run "$operation-$package.log"
    $binlog = Join-Path $run "$operation-$package-{}.binlog"
    Write-Host "$operation $package $version"
    & dotnet $operation $project --configuration Release --artifacts-path $artifacts `
        "-p:Version=$version" "-p:RestoreConfigFile=$restoreConfig" `
        "-p:RestorePackagesPath=$packageCache" '-p:NuGetAudit=false' "-bl:$binlog" `
        @additionalArguments *> $log
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $log -Tail 35 | Write-Host
        throw "$operation failed for $package; see $log"
    }
}

Push-Location $studioSource
try {
    # Build each root once; referenced projects and the Cobalt generator share these artifacts.
    foreach ($package in @('TrueMoon', 'TrueMoon.Alloy.Hosting')) {
        Invoke-PackageBuild 'build' $package @()
    }
    $stagingFeed = Join-Path $run 'feed'
    foreach ($package in $packages) {
        Invoke-PackageBuild 'pack' $package @('--no-build', '--no-restore', '--output', $stagingFeed)
    }
    $manifest = foreach ($package in $packages) {
        $archive = Join-Path $stagingFeed "$package.$version.nupkg"
        [ordered]@{ id = $package; version = $version; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash }
    }
    # Publish the complete set only after all packs succeed.
    foreach ($package in $packages) {
        Copy-Item -LiteralPath (Join-Path $stagingFeed "$package.$version.nupkg") -Destination $feed -ErrorAction Stop
    }
    [ordered]@{ frameworkRoot = $framework; builtAt = (Get-Date).ToString('o'); packages = @($manifest) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $feed "TrueMoon.$version.manifest.json") -Encoding utf8
    Write-Host "Created $($packages.Count) packages in $feed. Logs: $run"
}
finally {
    Pop-Location
}
