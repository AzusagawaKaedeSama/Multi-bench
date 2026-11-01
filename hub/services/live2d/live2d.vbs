' Author: Codex (GPT-6), 2026-10-21
' Unified Live2D and legacy redirect; process-local temporary directories.
Option Explicit
Dim fso, sh, here, python, script, port, tempDir, processEnv
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
If WScript.Arguments.Count <> 1 Then WScript.Quit 2
port = WScript.Arguments(0)
If port <> "8998" And port <> "8999" Then WScript.Quit 2
here = fso.GetParentFolderName(WScript.ScriptFullName)
script = fso.BuildPath(here, "live2d.py")
python = "D:\anaconda3\python.exe"
tempDir = "E:\AgentData\Work"
If Not fso.FileExists(python) Then WScript.Quit 3
If Not fso.FileExists(script) Then WScript.Quit 4
If Not fso.FolderExists(tempDir) Then WScript.Quit 5
Set processEnv = sh.Environment("PROCESS")
processEnv("TEMP") = tempDir
processEnv("TMP") = tempDir
processEnv("TMPDIR") = tempDir
processEnv("PYTHONDONTWRITEBYTECODE") = "1"
sh.CurrentDirectory = here
sh.Run """" & python & """ """ & script & """ " & port, 0, False
