@echo off
setlocal
title Thiet lap moi truong .NET MAUI cho Camera App
echo ==============================================================
echo        THIET LAP MOI TRUONG .NET MAUI CAMERA CHO ANDROID
echo ==============================================================
echo.

:: Kiem tra quyen Administrator
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [INFO] Yeu cau quyen Administrator de cai dat Workload va Java.
    echo Dang mo cua so xac nhan Admin (UAC)...
    powershell -Command "Start-Process cmd.exe -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b
)

echo [1/3] Dang cai dat Microsoft OpenJDK 17 qua Winget...
winget install --id Microsoft.OpenJDK.17 --silent --accept-package-agreements --accept-source-agreements

echo.
echo [2/3] Dang cai dat .NET MAUI Android Workload...
dotnet workload install maui-android

echo.
echo [3/3] Dang khoi phuc thu vien (restore) cho RawCameraApp...
cd /d "%~dp0\RawCameraApp"
dotnet restore

echo.
echo ==============================================================
echo   HOAN TAT! MOI TRUONG DA SAN SANG DE BUILD APP LEN DIEN THOAI.
echo ==============================================================
pause
