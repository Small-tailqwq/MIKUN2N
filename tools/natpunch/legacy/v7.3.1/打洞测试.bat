@echo off
rem 用法：打洞测试.bat <服务器地址> [房间号]
rem 两端填同样的房间号即可配对；缺省房间号仅用于本机自测。
setlocal
if "%~1"=="" (
    echo 用法：打洞测试.bat ^<服务器地址^> [房间号]
    echo 例如：打洞测试.bat vps.example.com myroom
    exit /b 1
)
set ROOM=%~2
if "%ROOM%"=="" set ROOM=114514
.\natpunch-v7.3.1.exe client %~1 %ROOM%
endlocal
