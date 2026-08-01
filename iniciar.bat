@echo off
title Tinta ES - Lector y traductor local de comics
cd /d "%~dp0"
dotnet run --project src\TintaES.Wpf\TintaES.Wpf.csproj --configuration Release
if errorlevel 1 (
  echo.
  echo No se pudo iniciar Tinta ES. Comprueba que .NET, Python 3.11 y Ollama estan instalados.
  pause
)
