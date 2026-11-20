#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Source')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Source')]
    [string] $FrameworkRoot,
    [Parameter(Mandatory, ParameterSetName = 'Archives')]
    [string] $PackageManifest
)

& (Join-Path $PSScriptRoot '../tools/Update-TrueMoonPackages.ps1') @PSBoundParameters
