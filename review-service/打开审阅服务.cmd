@echo off
rem 作者：Claude Code（Opus 5.5）· 2026-10-20 · 双击启动审阅服务并打开首页
start "" /min python "%~dp0审阅服务.py" 3955
timeout /t 2 >nul
start "" http://127.0.0.1:3955/
