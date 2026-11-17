#Requires -Version 7.0
<#
.SYNOPSIS
Builds Studio's TrueMoon packages without writing build output to the framework checkout.
.EXAMPLE
./Update-TrueMoonPackages.ps1 -FrameworkRoot ../../TrueMoon
.EXAMPLE
./Update-TrueMoonPackages.ps1 -PackageManifest <framework-delivery-manifest.json>
#>
[CmdletBinding(DefaultParameterSetName = 'Source')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Source')]
    [string] $FrameworkRoot,
    [Parameter(Mandatory, ParameterSetName = 'Archives')]
    [string] $PackageManifest
)

$ErrorActionPreference = 'Stop'
$studioSource = $PSScriptRoot
[xml] $versions = Get-Content -LiteralPath (Join-Path $studioSource 'Directory.Packages.props') -Raw
$version = $versions.SelectSingleNode('/Project/PropertyGroup/StudioTrueMoonVersion').InnerText
$packages = @(
    'TrueMoon.Contracts', 'TrueMoon.Core', 'TrueMoon.Argentis', 'TrueMoon.Cobalt',
    'TrueMoon', 'TrueMoon.Alloy',
    'TrueMoon.Alloy.Platform.Silk', 'TrueMoon.Alloy.Rendering.Skia', 'TrueMoon.Alloy.Hosting',
    'TrueMoon.Enerit', 'TrueMoon.Enerit.Generator', 'TrueMoon.Mithril'
)

if ($PSCmdlet.ParameterSetName -eq 'Archives') {
    $manifestPath = (Resolve-Path -LiteralPath $PackageManifest).Path
    $delivery = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($delivery.Completed -ne $true -or $delivery.Version -ne $version) {
        throw "Expected a completed framework delivery for $version."
    }
    $feed = Join-Path $studioSource '.local/truemoon-feed'
    $staging = Join-Path $studioSource ('.local/truemoon-build/import-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $verified = foreach ($package in $packages) {
        $rows = @($delivery.Packages | Where-Object { $_.Id -eq $package })
        if ($rows.Count -ne 1 -or $rows[0].Sha256 -notmatch '\A[0-9a-fA-F]{64}\z') {
            throw "Missing, ambiguous or invalid archive record: $package"
        }
        $row = $rows[0]
        $source = [string] $row.Path
        if (![IO.Path]::IsPathRooted($source)) { $source = Join-Path (Split-Path $manifestPath) $source }
        $fileName = "$package.$version.nupkg"
        $staged = Join-Path $staging $fileName
        Copy-Item -LiteralPath $source -Destination $staged -ErrorAction Stop
        $hash = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash
        if ($hash -ne $row.Sha256) { throw "Archive SHA-256 mismatch: $package" }
        $archive = [IO.Compression.ZipFile]::OpenRead($staged)
        try {
            $specs = @($archive.Entries | Where-Object { $_.FullName -match '\A[^/]+\.nuspec\z' })
            if ($specs.Count -ne 1) { throw "Expected one root nuspec: $package" }
            $reader = [IO.StreamReader]::new($specs[0].Open())
            try { [xml] $spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($spec.package.metadata.id -ne $package -or $spec.package.metadata.version -ne $version) {
                throw "Archive identity mismatch: $package"
            }
        } finally { $archive.Dispose() }
        $target = Join-Path $feed $fileName
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -ne $hash) {
            throw "Immutable feed archive differs: $target"
        }
        [ordered]@{ id = $package; version = $version; sha256 = $hash; staged = $staged; target = $target }
    }
    # Validate the entire staged set before publishing; never overwrite an archive or manifest.
    New-Item -ItemType Directory -Path $feed -Force | Out-Null
    foreach ($row in $verified) {
        if (!(Test-Path -LiteralPath $row.target)) { [IO.File]::Copy($row.staged, $row.target, $false) }
    }
    $destinationManifest = Join-Path $feed "TrueMoon.$version.studio-backend.manifest.json"
    if (!(Test-Path -LiteralPath $destinationManifest)) {
        $summary = [ordered]@{
            sourceManifest = $manifestPath
            sourceManifestSha256 = (Get-FileHash -LiteralPath $manifestPath).Hash
            importedAtUtc = [DateTime]::UtcNow.ToString('o')
            buildProfile = $delivery.BuildProfile
            packages = @($verified | ForEach-Object { [ordered]@{ id = $_.id; version = $_.version; sha256 = $_.sha256 } })
        }
        $json = $summary | ConvertTo-Json -Depth 8
        $stream = [IO.File]::Open($destinationManifest, [IO.FileMode]::CreateNew)
        try {
            $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
            $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
    }
    Write-Host "Verified/imported $($verified.Count) immutable packages for $version in $feed."
    return
}

$framework = (Resolve-Path -LiteralPath $FrameworkRoot).Path
$frameworkSource = Join-Path $framework 'src'
[xml] $frameworkProperties = Get-Content -LiteralPath (Join-Path $frameworkSource 'Directory.Build.props') -Raw
$frameworkVersion = $frameworkProperties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -ne $frameworkVersion) {
    throw "Studio expects $version; framework declares $frameworkVersion. Align Directory.Packages.props before packing."
}
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
    foreach ($package in @('TrueMoon', 'TrueMoon.Alloy.Hosting', 'TrueMoon.Enerit', 'TrueMoon.Mithril')) {
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
