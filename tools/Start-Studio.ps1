param([string]$Config=$env:TRUE_MOON_STUDIO_CONFIG,[int]$Port=8787,[switch]$NoBrowser)
$ErrorActionPreference='Stop'
if ([string]::IsNullOrWhiteSpace($Config)) {
    $personalData=[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not $personalData) { throw 'Local application data directory is unavailable.' }
    $Config=Join-Path $personalData 'TrueMoon/Tales Studio/Studio.config.local.json'
    if (-not (Test-Path -LiteralPath $Config -PathType Leaf)) {
        throw "Studio configuration not found: $Config. Launch the desktop Studio once or copy Studio.config.example.json to that location."
    }
}
$Config=[Environment]::ExpandEnvironmentVariables($Config)
$Config=(Resolve-Path -LiteralPath $Config).Path
$settings=Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$python=$env:STUDIO_PYTHON
if (-not $python) { $python=[Environment]::ExpandEnvironmentVariables($settings.python_executable) }
if (-not $python) { throw 'Set python_executable in Studio config, or STUDIO_PYTHON.' }
if (-not [IO.Path]::IsPathRooted($python)) { $python=Join-Path (Split-Path $Config) $python }
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { throw "Python not found: $python" }
$arguments=@('-s','-B',(Join-Path $PSScriptRoot '../legacy/web/app.py'),'--config',$Config,'--port',"$Port")
if (-not $NoBrowser) { $arguments+='--open-browser' }
& $python @arguments
exit $LASTEXITCODE
