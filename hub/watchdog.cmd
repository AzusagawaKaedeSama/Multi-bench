@echo off
REM Author: WorkBuddy (Intelligent Design Assistant), 2026-10-31
REM Unified supervisor for ReviewHub + resident services.
REM Driven by scheduled task "ReviewHub-Autostart" (Boot + Logon + every 1 minute).
REM Idempotent: each service is probed first, only missing ones are restarted.
REM NOTE: keep this file UTF-8 WITHOUT BOM and ASCII-only. A BOM breaks cmd.exe
REM       on line 1 ("'@echo' is not recognized") and the script aborts.

setlocal
set "D=%~dp0"
set "TEMP=E:\AgentData\Work"
set "TMP=E:\AgentData\Work"
set "TMPDIR=E:\AgentData\Work"
set "NODE_OPTIONS="
set "PYTHONDONTWRITEBYTECODE=1"

REM --- 0. Image gate confirmation page (3958) -----------------------------
REM Author: workbench-ux, 2026-11-01.
REM The confirmation page hard-codes API http://127.0.0.1:3958, so a resident
REM HTTP server must own that port: gate_server.py serves the page itself from /
REM plus /api/pending, /img?p= and /api/decide.
REM Scanning stays on demand (gate_watcher.py --once after a batch): resident
REM scanning cost about 0.9 core and competed with the generator.
REM Launcher is E:\AgentData\ReviewHub\gate\launch_gate.py (ASCII path); it spawns
REM gate_server.py with the Chinese path from Python, where encoding is not an issue.
call :ensure 3958 "gate_server.py" "image-gate"

REM --- 1. ReviewHub workbench (3900) --------------------------------------
call :ensure 3900 "hub.cjs" "review-hub"
REM Mobile access and repaired voice port: Codex (GPT-6), 2026-11-01.
call :ensure 13900 "gateway.cjs" "mobile-access"
call :ensure 3975 "voice_server.py" "character-voice"

REM --- 1b. Review desk data service (3962) -------------------------------
REM Serves the review page and the personal portfolio page; refresh pulls
REM Bilibili comments through the logged-in browser on 9333. Author: dsh 2026-10-31.
call :ensure 3962 "review_desk.py" "review-desk"

REM --- 2. Reference desks (3945) ------------------------------------------
call :ensure 3945 "server.py" "reference-desks"

REM --- 3. Prompt Studio (3967) + ratings (3966) --------------------------
REM prompt-studio launcher starts both 3967 and 3966.
call :ensure 3967 "server.py" "prompt-studio"

REM --- 4. Review service / static page server (3955) ----------------------
REM Normally started by the Startup folder shortcut (parent = explorer.exe).
REM This block is the fallback in case that shortcut did not fire.
netstat -ano | findstr /C:"127.0.0.1:3955 " /C:"0.0.0.0:3955 " | findstr LISTENING >nul
if not errorlevel 1 goto :shell
echo [%date% %time%] port 3955 down, restarting >> "%D%watchdog.log"
if exist "D:\2-视频制作\0-工作流\审阅服务\审阅服务.py" (
  start "" /min "D:\anaconda3\pythonw.exe" "D:\2-视频制作\0-工作流\审阅服务\审阅服务.py" 3955
)

REM --- 5. Workbench native shell (desktop app) ----------------------------
REM Author: WorkBuddy (Intelligent Design Assistant), 2026-10-31
REM The shell is a GUI app that lives in the tray. If the user closed it from the
REM tray menu we must not force it back, so only relaunch when it crashed.
REM shell-state.txt is written when the user exits on purpose; the shell clears
REM it on startup, so a leftover file means "crashed since last clean start".
REM
REM SESSION NOTE (important): this watchdog runs in session 0 (service session).
REM A GUI started from here has no desktop -- the window never appears and the
REM tray icon is missing, which looks exactly like "the app did not start".
REM So session 0 only decides; the actual launch goes through the
REM "WorkbenchShell-Interactive" task (LogonType=InteractiveToken), which
REM Task Scheduler places in the signed-in user's desktop session.
:shell
tasklist /FI "IMAGENAME eq WorkbenchShell.exe" 2>nul | find /I "WorkbenchShell.exe" >nul
if not errorlevel 1 goto :done
if not exist "E:\AgentData\Work\WorkbenchShell\WorkbenchShell.exe" goto :done
if exist "E:\AgentData\Work\WorkbenchShell\shell-state.txt" goto :done
echo [%date% %time%] workbench shell down, restarting via interactive task >> "%D%watchdog.log"
schtasks /run /tn "WorkbenchShell-Interactive" >> "%D%watchdog.log" 2>&1

:done
endlocal
exit /b 0

REM =========================================================================
:ensure
REM %1 = port, %2 = script name, %3 = label for the log
REM Tailnet listeners share the public port; probe the local backend specifically.
netstat -ano | findstr /C:"127.0.0.1:%~1 " /C:"0.0.0.0:%~1 " | findstr LISTENING >nul
if not errorlevel 1 exit /b 0
echo [%date% %time%] port %~1 (%~3) down, restarting >> "%D%watchdog.log"
if /i "%~3"=="review-hub" (
  wscript.exe //nologo "%D%silent.vbs" "%~2" "%~1"
) else if /i "%~3"=="reference-desks" (
  wscript.exe //nologo "%D%reference-desks\start.vbs"
) else if /i "%~3"=="mobile-access" (
  wscript.exe //B //NoLogo "%D%mobile-access\start.vbs"
) else if /i "%~3"=="character-voice" (
  wscript.exe //B //NoLogo "%D%voice-workbench\start.vbs"
) else if /i "%~3"=="prompt-studio" (
  wscript.exe //nologo "%D%prompt-studio\start.vbs"
) else if /i "%~3"=="image-gate" (
  start "" /min "D:\anaconda3\pythonw.exe" "E:\AgentData\ReviewHub\gate\launch_gate.py"
) else if /i "%~3"=="review-desk" (
  start "" /min "D:\anaconda3\pythonw.exe" "E:\AgentData\ReviewHub\reviewdesk\review_service.py" 3962
)
exit /b 0
