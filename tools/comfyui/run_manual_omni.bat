@echo off
setlocal
set "TMT_COMFY_ROOT=E:\ComfyUI_windows_portable"
cd /d "%TMT_COMFY_ROOT%"
echo Omni production UI: Sage + FP16 accumulation + Motion Context pack only
".\python_embeded\python.exe" -s ".\ComfyUI\main.py" --windows-standalone-build --fast fp16_accumulation --use-sage-attention --disable-all-custom-nodes --whitelist-custom-nodes ComfyUI-H3-Motion-Context %*
set "TMT_COMFY_EXIT=%ERRORLEVEL%"
echo ComfyUI exited with code %TMT_COMFY_EXIT%.
pause
exit /b %TMT_COMFY_EXIT%
