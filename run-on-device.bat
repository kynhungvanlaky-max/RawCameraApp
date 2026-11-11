@echo off
setlocal
title Build va chay RawCameraApp tren dien thoai
echo ==============================================================
echo      DANG BUILD VA CAI APP LEN DIEN THOAI (POCO X3 PRO)
echo ==============================================================
echo.

set "DOTNET_ROOT=%USERPROFILE%\dotnet"
set "JAVA_HOME=%USERPROFILE%\jdk-17"
set "ANDROID_HOME=%USERPROFILE%\Android\Sdk"
set "PATH=%USERPROFILE%\dotnet;%USERPROFILE%\jdk-17\bin;%USERPROFILE%\Android\Sdk\platform-tools;%PATH%"

cd /d "%~dp0\RawCameraApp"

echo [1/3] Kiem tra ket noi thiet bi ADB...
adb devices
echo.

echo [2/3] Dang bien dich RawCameraApp...
dotnet build -f net9.0-android -p:AndroidSdkDirectory="%ANDROID_HOME%" -p:JavaSdkDirectory="%JAVA_HOME%"

if %errorlevel% neq 0 (
    echo.
    echo [Loi] Build that bai!
    pause
    exit /b %errorlevel%
)

echo.
echo [3/3] Dang cai dat va khoi chay len dien thoai...
adb install -r "bin\Debug\net9.0-android\com.companyname.rawcameraapp-Signed.apk"
adb shell am start -n com.companyname.rawcameraapp/crc649c260656a9417674.MainActivity

echo.
echo ==============================================================
echo   [THANH CONG] App da duoc khoi chay tren dien thoai cua ban!
echo ==============================================================
pause
