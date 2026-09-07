@echo off
rem Empties the Windows temporary directories and the student's Downloads folder.
rem timeout: 300
setlocal
for %%d in ("%SystemRoot%\Temp" "C:\Users\student\AppData\Local\Temp" "C:\Users\student\Downloads") do (
    if exist %%d (
        echo clearing %%d
        del /f /s /q "%%~d\*" >nul 2>&1
        for /d %%s in ("%%~d\*") do rd /s /q "%%s" >nul 2>&1
    ) else (
        echo skipping %%d - not found
    )
)
echo done
exit /b 0
