param([Parameter(Mandatory=$true)][string]$JobDir,[ValidateSet('api','ui')][string]$Mode='api')
$Job='Run01'
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $JobDir).Path
$env:TMT_R08_JOB=$root
$env:TMT_R08_MODE=$Mode
$helpers=$PSScriptRoot
$runtime=(Get-Content -LiteralPath (Join-Path $root 'Job.json') -Raw | ConvertFrom-Json).runtime
$python=if ($runtime.python_executable) { $runtime.python_executable } elseif ($env:TMT_STUDIO_PYTHON) { $env:TMT_STUDIO_PYTHON } else { 'E:\ComfyUI_windows_portable\python_embeded\python.exe' }
$comfy=if ($runtime.comfy_root) { $runtime.comfy_root } elseif ($env:TMT_COMFY_ROOT) { $env:TMT_COMFY_ROOT } else { 'E:\ComfyUI_windows_portable\ComfyUI' }
$env:TMT_STUDIO_PYTHON=$python
$env:TMT_COMFY_ROOT=$comfy
$resultDir=$root
if (Test-Path -LiteralPath (Join-Path $resultDir 'Submission.json')) { throw 'This job was already submitted; no duplicate generation' }
$portCheck=Start-Process -FilePath $python -ArgumentList '-s','-B',(Join-Path $helpers 'port_guard.py') -WorkingDirectory $helpers -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $resultDir 'Port.stdout.log') -RedirectStandardError (Join-Path $resultDir 'Port.stderr.log')
if (-not $portCheck.WaitForExit(60000)) { $portCheck.Kill(); throw 'Port preflight exceeded 60 seconds; no server started' }
if ($portCheck.ExitCode -ne 0) { throw 'Port preflight refused start; preserve existing listener' }
$other=Get-NetTCPConnection -State Listen -LocalPort 8188 -ErrorAction SilentlyContinue
if ($other) { $q=Invoke-RestMethod -Uri 'http://127.0.0.1:8188/queue' -TimeoutSec 3; if ($q.queue_running.Count -or $q.queue_pending.Count) { throw 'User Comfy queue busy' } }
$workflowDir=Join-Path $root 'user/default/workflows'
New-Item -ItemType Directory -Path $workflowDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Workflow.ui.json') -Destination (Join-Path $workflowDir 'R08.Current.json')
$launchClock=[Diagnostics.Stopwatch]::StartNew()
$proc=$null
$validation=$null
$driver=$null
$exitCode=1
try {
    $validation=Start-Process -FilePath $python -ArgumentList '-s','-B',(Join-Path $helpers 'validate.py'),$Job -WorkingDirectory $comfy -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $resultDir 'Validation.stdout.log') -RedirectStandardError (Join-Path $resultDir 'Validation.stderr.log')
    if (-not $validation.WaitForExit(90000)) { $validation.Kill(); throw 'CPU validation exceeded 90 seconds' }
    if ($validation.ExitCode -ne 0) { throw 'Graph validation failed; see Validation.stderr.log' }
    $env:TRITON_CACHE_DIR=Join-Path $env:TEMP 'tmt-r00-sage-triton'
    $env:TORCHINDUCTOR_CACHE_DIR=Join-Path $env:TEMP 'tmt-r00-torchinductor'
    $proc=Start-Process -FilePath $python -ArgumentList '-s','-B',(Join-Path $helpers 'server.py') -WorkingDirectory $comfy -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $resultDir 'Server.stdout.log') -RedirectStandardError (Join-Path $resultDir 'Server.stderr.log')
    $proc.Id | Set-Content -LiteralPath (Join-Path $resultDir 'server.pid') -Encoding ascii
    $remaining=[Math]::Max(1,720-$launchClock.Elapsed.TotalSeconds).ToString([Globalization.CultureInfo]::InvariantCulture)
    # Separate watchdog survives a disconnected chat or an interrupted launcher.
    $watchdog=Start-Process -FilePath $python -ArgumentList '-s','-B',(Join-Path $helpers 'watchdog.py'),$Job,$proc.Id,$remaining -WorkingDirectory $helpers -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $resultDir 'Watchdog.stdout.log') -RedirectStandardError (Join-Path $resultDir 'Watchdog.stderr.log')
    $watchdog.Id | Set-Content -LiteralPath (Join-Path $resultDir 'watchdog.pid') -Encoding ascii
    $ready=$false
    $startup=[Diagnostics.Stopwatch]::StartNew()
    while ($startup.Elapsed.TotalSeconds -lt 90) {
        if ($proc.HasExited) { throw 'Test server exited during startup' }
        if ($watchdog.HasExited) { throw 'Watchdog failed during startup' }
        try { $null=Invoke-RestMethod -Uri 'http://127.0.0.1:8199/system_stats' -TimeoutSec 2; $ready=$true; break } catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ready) { throw 'Server startup exceeded 90 seconds' }
    if (-not (Test-Path -LiteralPath (Join-Path $resultDir 'Watchdog.json'))) { throw 'Watchdog not armed' }
    Write-Output "Ready Omni.v1 $Job server PID=$($proc.Id); deadlines: generation 480s, no progress 180s, total launch 720s"
    $driver=Start-Process -FilePath $python -ArgumentList '-s','-B',(Join-Path $helpers 'driver.py'),$Job -WorkingDirectory $helpers -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $resultDir 'Driver.stdout.log') -RedirectStandardError (Join-Path $resultDir 'Driver.stderr.log')
    $driverLimit=if($Mode -eq 'ui'){660000}else{510000}
    if (-not $driver.WaitForExit($driverLimit)) { $driver.Kill(); throw 'Driver exceeded its bounded wait' }
    $exitCode=$driver.ExitCode
    Get-Content -LiteralPath (Join-Path $resultDir 'Driver.stdout.log') -Tail 4
    if ($exitCode -ne 0) { throw 'One job failed; preserved logs, no automatic retry' }
} catch {
    [ordered]@{job=$Job;error=$_.Exception.Message;at=(Get-Date).ToString('o');automatic_retry=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultDir 'Launcher.failure.json') -Encoding utf8
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    $exitCode=1
} finally {
    if ($driver -and -not $driver.HasExited) { $driver.Kill() }
    if ($validation -and -not $validation.HasExited) { $validation.Kill() }
    if ($proc) {
        # This process object belongs to this launch. Other Comfy servers are untouched.
        if (-not $proc.HasExited) {
            try { $null=Invoke-RestMethod -Uri 'http://127.0.0.1:8199/interrupt' -Method Post -Body '{}' -ContentType 'application/json' -TimeoutSec 3 } catch {}
            $proc.Kill()
            $null=$proc.WaitForExit(5000)
        }
        [ordered]@{job=$Job;pid=$proc.Id;stopped=$proc.HasExited;exit_code=$exitCode;stopped_at=(Get-Date).ToString('o');scope='only the server process handle created by this launcher'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultDir 'Cleanup.json') -Encoding utf8
    }
}
exit $exitCode
