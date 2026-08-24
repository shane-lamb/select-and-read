@echo off
REM Wrapper for drive-highlight.ps1, so the interactive scheduled task is handed one path
REM and no nested quoting. Passing a powershell command line through prlctl exec and then
REM through the task's own argument parsing mangles the quotes, and the task then runs and
REM produces nothing at all - no error, no output, just no work done.
powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\sar-test\drive-highlight.ps1 > C:\sar-test\hl.log 2>&1
