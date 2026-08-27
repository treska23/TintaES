param(
    [string]$BasePython = "D:\TintaESData\manga-image-translator\.venv\Scripts\python.exe",
    [string]$InstallRoot = "D:\TintaESData\paddleocr"
)

$ErrorActionPreference = "Stop"
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$venvPython = Join-Path $InstallRoot ".venv\Scripts\python.exe"

if (-not (Test-Path -LiteralPath $BasePython)) {
    throw "No se encuentra Python 3.11 en $BasePython"
}

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
if (-not (Test-Path -LiteralPath $venvPython)) {
    & $BasePython -m venv (Join-Path $InstallRoot ".venv")
}

& $venvPython -m pip install --upgrade pip setuptools wheel
& $venvPython -m pip install --upgrade torch torchvision --index-url https://download.pytorch.org/whl/cu128
& $venvPython -m pip install --upgrade "paddleocr[doc-parser]"
& $venvPython -m pip install --upgrade transformers accelerate
& $venvPython -c "import torch; from paddleocr import PaddleOCRVL; print('CUDA=' + str(torch.cuda.is_available())); print('PaddleOCR-VL 1.6 preparado')"

Write-Host "Entorno instalado en $InstallRoot" -ForegroundColor Green
Write-Host "Los modelos oficiales se descargarán en la primera prueba y quedarán en $InstallRoot\models." -ForegroundColor DarkGray
