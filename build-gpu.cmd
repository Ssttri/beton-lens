@echo off
setlocal
cd /d "%~dp0"
where cl.exe >nul 2>nul
if not errorlevel 1 goto build
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto missing
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
if not defined VSINSTALL goto missing
call "%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b 1
:build
cl /nologo /O2 /EHsc /utf-8 /W3 BetonLensGPU.cpp /Fe:BetonLensGPU.exe /link /SUBSYSTEM:WINDOWS
if errorlevel 1 exit /b 1
del BetonLensGPU.obj 2>nul
echo Built BetonLensGPU.exe
exit /b 0
:missing
echo Install Visual Studio C++ Build Tools and the Windows SDK, or use an x64 Native Tools prompt.
exit /b 1
