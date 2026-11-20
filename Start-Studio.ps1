param([string]$Config,[int]$Port,[switch]$NoBrowser)
& (Join-Path $PSScriptRoot 'tools/Start-Studio.ps1') @PSBoundParameters
exit $LASTEXITCODE
