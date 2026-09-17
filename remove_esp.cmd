@echo off
setlocal
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

echo ================================================================
echo   Win2Linux - Removing ESP System Partition from Disk 1
echo ================================================================
echo.

(
echo select disk 1
echo select partition 3
echo delete partition override
echo select partition 2
echo extend
echo exit
) > "%TEMP%\remove_esp_diskpart.txt"

diskpart /s "%TEMP%\remove_esp_diskpart.txt"
del "%TEMP%\remove_esp_diskpart.txt" 2>nul

echo.
echo ================================================================
echo   Partition 3 deleted and Drive D: extended successfully!
echo ================================================================
echo.
pause
