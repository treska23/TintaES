param(
    [switch]$Full,
    [ValidateSet("auto", "cuda", "cpu")]
    [string]$Backend = "auto"
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
if ($LASTEXITCODE -ne 0) {
    throw "No se pudieron restaurar NumPy/Protobuf."
}

Write-Host "Sincronizando las dependencias declaradas por manga-image-translator..." -ForegroundColor Yellow
if ($Full) {
    & $Python -m pip install --disable-pip-version-check --upgrade --force-reinstall -r $Requirements
} else {
    & $Python -m pip install --disable-pip-version-check -r $Requirements
}
if ($LASTEXITCODE -ne 0) {
    throw "pip no pudo sincronizar requirements.txt."
}

# requirements.txt deja torch/torchvision sin versión. Si el entorno fue modificado,
# pip puede conservar wheels incompatibles entre sí y torchvision pierde operadores
# nativos como torchvision::nms. Reinstalamos siempre un trío oficial emparejado.
$UseCuda = $false
if ($Backend -eq "cuda") {
    $UseCuda = $true
} elseif ($Backend -eq "auto") {
    $UseCuda = $null -ne (Get-Command "nvidia-smi" -ErrorAction SilentlyContinue)
}

Write-Host "Reinstalando PyTorch/TorchVision como conjunto compatible..." -ForegroundColor Yellow
& $Python -m pip uninstall -y torch torchvision torchaudio | Out-Host

if ($UseCuda) {
    # Pareja oficial PyTorch 2.6 / TorchVision 0.21. CUDA 12.4 incluye su propio runtime;
    # no depende de que el toolkit CUDA local tenga exactamente esa versión.
    & $Python -m pip install --disable-pip-version-check --no-cache-dir `
        "torch==2.6.0" "torchvision==0.21.0" "torchaudio==2.6.0" `
        --index-url "https://download.pytorch.org/whl/cu124"
} else {
    & $Python -m pip install --disable-pip-version-check --no-cache-dir `
        "torch==2.6.0" "torchvision==0.21.0" "torchaudio==2.6.0" `
        --index-url "https://download.pytorch.org/whl/cpu"
}
if ($LASTEXITCODE -ne 0) {
    throw "No se pudo instalar un conjunto compatible de PyTorch/TorchVision."
}

Write-Host "Comprobando imports y operador NMS del worker..." -ForegroundColor Yellow
$env:TINTAES_ENGINE_ROOT = $EngineRoot
@'
import os
import sys
from pathlib import Path
engine = Path(os.environ["TINTAES_ENGINE_ROOT"])
sys.path.insert(0, str(engine))
import cv2
import numpy
import torch
import torchvision
from torchvision.ops import nms

boxes = torch.tensor([[0.0, 0.0, 10.0, 10.0], [1.0, 1.0, 9.0, 9.0]])
scores = torch.tensor([0.9, 0.8])
kept = nms(boxes, scores, 0.5)
assert kept.numel() >= 1

import tinta_worker
print("OK")
print("numpy", numpy.__version__)
print("cv2", cv2.__version__)
print("torch", torch.__version__)
print("torchvision", torchvision.__version__)
print("cuda", torch.cuda.is_available())
print("nms", kept.tolist())
'@ | & $Python -

if ($LASTEXITCODE -ne 0) {
    throw "El motor sigue sin poder importarse después de reparar PyTorch/TorchVision."
}

Write-Host ""
Write-Host "Motor principal reparado." -ForegroundColor Green
Write-Host "Cierra y vuelve a abrir TintaES antes de probar Detectar y traducir."