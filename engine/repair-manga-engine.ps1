param(
    [switch]$Full
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$EngineRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$TranslatorDir = Join-Path $EngineRoot "manga-image-translator"
$Python = Join-Path $TranslatorDir ".venv\Scripts\python.exe"
$Requirements = Join-Path $TranslatorDir "requirements.txt"

if (-not (Test-Path $TranslatorDir)) {
    throw "No se encuentra engine\manga-image-translator. Ejecuta 'git submodule update --init --recursive'."
}
if (-not (Test-Path $Python)) {
    throw "No existe el .venv de manga-image-translator: $Python"
}
if (-not (Test-Path $Requirements)) {
    throw "No se encuentra requirements.txt en manga-image-translator."
}

Write-Host "TintaES · reparación del motor principal" -ForegroundColor Cyan
Write-Host "Este script NO toca HunyuanOCR ni borra modelos." -ForegroundColor DarkGray

# El primer instalador de HunyuanOCR reutilizaba por error este entorno y podía
# subir NumPy/Protobuf a versiones incompatibles con manga-image-translator.
Write-Host "Restaurando las versiones críticas del motor..." -ForegroundColor Yellow
& $Python -m pip install --disable-pip-version-check --upgrade --force-reinstall `
    "numpy==1.26.4" `
    "protobuf>=3.20.2,<6.0.0"

Write-Host "Sincronizando las dependencias declaradas por manga-image-translator..." -ForegroundColor Yellow
if ($Full) {
    & $Python -m pip install --disable-pip-version-check --upgrade --force-reinstall -r $Requirements
} else {
    & $Python -m pip install --disable-pip-version-check -r $Requirements
}

Write-Host "Comprobando imports del worker..." -ForegroundColor Yellow
$env:TINTAES_ENGINE_ROOT = $EngineRoot
@'
import os
import sys
from pathlib import Path
engine = Path(os.environ["TINTAES_ENGINE_ROOT"])
sys.path.insert(0, str(engine))
import cv2
import numpy
import tinta_worker
print("OK")
print("numpy", numpy.__version__)
print("cv2", cv2.__version__)
'@ | & $Python -

if ($LASTEXITCODE -ne 0) {
    throw "El motor sigue sin poder importarse. Repite con: .\repair-manga-engine.ps1 -Full"
}

Write-Host ""
Write-Host "Motor principal reparado." -ForegroundColor Green
Write-Host "Cierra y vuelve a abrir TintaES antes de probar Detectar y traducir."