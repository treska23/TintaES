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
& $venvPython -m pip install --upgrade transformers accelerate huggingface_hub
& $venvPython -c "import torch; from paddleocr import PaddleOCRVL; print('CUDA=' + str(torch.cuda.is_available())); print('PaddleOCR-VL 1.6 preparado')"

# Backend de baja latencia para una sola página. PaddleOCR lo soporta oficialmente
# como servicio VLM y puede enviar varios bocadillos de la MISMA página a la vez.
function Find-LlamaServer {
    $command = Get-Command llama-server -ErrorAction SilentlyContinue
    if ($null -ne $command -and (Test-Path -LiteralPath $command.Source)) {
        return $command.Source
    }

    if ($env:LOCALAPPDATA) {
        $wingetLink = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\llama-server.exe"
        if (Test-Path -LiteralPath $wingetLink) {
            return $wingetLink
        }

        $packageRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
        if (Test-Path -LiteralPath $packageRoot) {
            $found = Get-ChildItem -LiteralPath $packageRoot -Filter "llama-server.exe" -File -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -like "*ggml.llamacpp*" } |
                Select-Object -First 1
            if ($null -ne $found) {
                return $found.FullName
            }
        }
    }
    return $null
}

$llamaServer = Find-LlamaServer
if (-not $llamaServer) {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -ne $winget) {
        Write-Host "Instalando llama.cpp para acelerar los bocadillos de cada página..." -ForegroundColor Cyan
        & winget install --id ggml.llamacpp -e --accept-source-agreements --accept-package-agreements --silent
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "winget no pudo instalar llama.cpp (código $LASTEXITCODE). PaddleOCR seguirá funcionando con Transformers."
        }
        $llamaServer = Find-LlamaServer
    }
}

if ($llamaServer) {
    Set-Content -LiteralPath (Join-Path $InstallRoot "llama-server.path") -Value $llamaServer -Encoding UTF8
    Write-Host "llama-server: $llamaServer" -ForegroundColor DarkGray
} else {
    Write-Warning "No se encontró llama-server. El OCR seguirá funcionando, pero sin la aceleración concurrente intra-página."
}

# Usamos el GGUF OFICIAL de PaddlePaddle, no una cuantización comunitaria ni un
# modelo distinto. Así cambiamos el motor de inferencia, no el OCR que reconoce.
$llamaModelDir = Join-Path $InstallRoot "models\llama"
New-Item -ItemType Directory -Force -Path $llamaModelDir | Out-Null
Write-Host "Preparando PaddleOCR-VL 1.6 GGUF oficial (~1,8 GB)..." -ForegroundColor Cyan
& $venvPython -c "from huggingface_hub import hf_hub_download; import sys; repo='PaddlePaddle/PaddleOCR-VL-1.6-GGUF'; names=('PaddleOCR-VL-1.6-GGUF.gguf','PaddleOCR-VL-1.6-GGUF-mmproj.gguf'); [hf_hub_download(repo_id=repo, filename=n, local_dir=sys.argv[1]) for n in names]" $llamaModelDir
if ($LASTEXITCODE -ne 0) {
    throw "No se pudieron descargar los modelos GGUF oficiales de PaddleOCR-VL 1.6."
}

Write-Host "Entorno instalado en $InstallRoot" -ForegroundColor Green
Write-Host "PaddleOCR usará hasta 4 solicitudes concurrentes dentro de cada página cuando llama.cpp esté disponible." -ForegroundColor Green
Write-Host "Puedes ajustar TINTAES_PADDLE_PAGE_PARALLEL (1-8) y TINTAES_PADDLE_MAX_NEW_TOKENS (128-2048) si necesitas afinar el equipo." -ForegroundColor DarkGray
