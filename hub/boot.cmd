@REM Author: Codex (GPT-6), 2026-10-21
@echo off
chcp 65001 >nul
REM Original boot launcher: dsh (DeepSeek-V4.1-Flash), 2026-10-21
REM GBP startup integration candidate; install in physical E ReviewHub.
setlocal
set "TEMP=E:\AgentData\Work"
set "TMP=E:\AgentData\Work"
set "TMPDIR=E:\AgentData\Work"
set "PYTHONDONTWRITEBYTECODE=1"
set "NODE_OPTIONS="
if not exist "%TEMP%\." mkdir "%TEMP%" >nul 2>nul
if not exist "%TEMP%\." exit /b 3
set "D=%~dp0"
echo [%date% %time%] boot >> "%D%start.log"

REM Author: Codex (GPT-6), 2026-10-24. MOSS and simple ComfyUI, hidden.
wscript.exe //B //NoLogo "%D%generation\start.vbs"
call :ensure 3960 "migrate.cjs" "migrate-filter"
call :ensure 3965 "portfolio.py" "portfolio-preview"
call :ensure 8999 "live2d.py"  "live2d-all"
call :ensure 8998 "live2d.py"  "live2d-uika-redirect"
REM Prompt Studio permanent service: Codex (GPT-6), 2026-10-25
setlocal
set "TEMP=D:\AgentData\Work\HermesTemp"
set "TMP=D:\AgentData\Work\HermesTemp"
set "TMPDIR=D:\AgentData\Work\HermesTemp"
wscript.exe //B //NoLogo "E:\AgentData\ReviewHub\prompt-studio\start.vbs"
endlocal
REM Author: Codex (GPT-6), 2026-10-29. Lightweight character voice page; models load only on Generate.
wscript.exe //B //NoLogo "%D%voice-workbench\start.vbs"
call :ensure 3900 "hub.cjs"     "review-hub"
REM Mobile workbench gateway: Codex (GPT-6), 2026-11-01
wscript.exe //B //NoLogo "%D%mobile-access\start.vbs"
REM Author: Codex (GPT-6), 2026-10-23. Permanent reference desks; hidden child process.
wscript.exe //B //NoLogo "%D%reference-desks\start.vbs"

echo [%date% %time%] done >> "%D%start.log"
endlocal
exit /b

:ensure
netstat -ano | findstr /C:"127.0.0.1:%~1 " /C:"0.0.0.0:%~1 " | findstr LISTENING >nul
if not errorlevel 1 (
  echo   %~1 %~2 already-running >> "%D%start.log"
  exit /b
)
if /i "%~2"=="live2d.py" (
  wscript.exe //nologo "%D%live2d.vbs" "%~1"
) else if /i "%~2"=="portfolio.py" (
  wscript.exe //nologo "%D%silentpy.vbs" "%~2" "%~1"
) else (
  wscript.exe //nologo "%D%silent.vbs" "%~2" "%~1"
)
echo   %~1 %~2 started >> "%D%start.log"
exit /b
