# CPU-only reproduction: does Start-Process expose ExitCode after timed wait?
$ErrorActionPreference='Stop'
$outFile = [IO.Path]::GetTempFileName()
$errFile = [IO.Path]::GetTempFileName()
$probe = Start-Process -FilePath 'E:/ComfyUI_windows_portable/python_embeded/python.exe' -ArgumentList '-s','-B','-c','pass' -WindowStyle Hidden -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile
$finished = $probe.WaitForExit(10000)
if (-not $finished) { $probe.Kill(); throw 'Probe timed out' }
[ordered]@{ powershell=$PSVersionTable.PSVersion.ToString(); finished=$finished; exit_code=$probe.ExitCode; code_is_null=($null -eq $probe.ExitCode); stdout=$outFile; stderr=$errFile } | ConvertTo-Json -Compress
