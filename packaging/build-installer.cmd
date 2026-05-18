@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Installer.ps1" %*
