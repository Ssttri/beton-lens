@echo off
rem Builds BetonVeil.exe (grade + grain) and BetonLens.exe (constructivist lens) with the csc.exe that ships with Windows.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /out:BetonVeil.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll BetonVeil.cs
"%CSC%" /nologo /target:winexe /optimize+ /unsafe /out:BetonLens.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll BetonLens.cs
