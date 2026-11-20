@echo off
setlocal
cd /d "%~dp0"

echo ComfyUI video profile: SageAttention + FP16 accumulation
".\python_embeded\python.exe" -s ".\ComfyUI\main.py" --windows-standalone-build --fast fp16_accumulation --use-sage-attention %*
set "COMFY_EXIT_CODE=%ERRORLEVEL%"

echo.
echo ComfyUI exited with code %COMFY_EXIT_CODE%.
pause
exit /b %COMFY_EXIT_CODE%
