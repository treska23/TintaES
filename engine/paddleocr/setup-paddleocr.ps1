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

# -----------------------------------------------------------------------------
# llama.cpp CUDA para OCR concurrente dentro de UNA sola página
# -----------------------------------------------------------------------------
# No usamos ya el paquete winget ggml.llamacpp como ruta principal: en Windows
# ese paquete puede resolver a la compilación Vulkan. En una NVIDIA queremos la
# compilación CUDA oficial para que los slots simultáneos de cada bocadillo usen
# el backend adecuado.
$llamaBuild = "b10809"
$llamaCudaRoot = Join-Path $InstallRoot "llama-cuda\$llamaBuild"
$llamaServer = Join-Path $llamaCudaRoot "llama-server.exe"
$llamaMarker = Join-Path $llamaCudaRoot ".tintaes-ready"

$llamaBaseUrl = "https://github.com/ggml-org/llama.cpp/releases/download/$llamaBuild"
$llamaBinUrl = "$llamaBaseUrl/llama-$llamaBuild-bin-win-cuda-12.4-x64.zip"
$llamaRuntimeUrl = "$llamaBaseUrl/cudart-llama-bin-win-cuda-12.4-x64.zip"

function Install-LlamaCuda {
    param(
        [string]$Destination,
        [string]$BinaryUrl,
        [string]$RuntimeUrl
    )

    $downloadRoot = Join-Path $env:TEMP ("tintaes-llama-cuda-" + [Guid]::NewGuid().ToString("N"))
    $binZip = Join-Path $downloadRoot "llama-cuda.zip"
    $runtimeZip = Join-Path $downloadRoot "llama-cudart.zip"
    New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null

    try {
        Write-Host "Descargando llama.cpp CUDA oficial para NVIDIA..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $BinaryUrl -OutFile $binZip -UseBasicParsing
        Invoke-WebRequest -Uri $RuntimeUrl -OutFile $runtimeZip -UseBasicParsing

        if (Test-Path -LiteralPath $Destination) {
            Remove-Item -LiteralPath $Destination -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        Expand-Archive -LiteralPath $binZip -DestinationPath $Destination -Force
        Expand-Archive -LiteralPath $runtimeZip -DestinationPath $Destination -Force

        $server = Get-ChildItem -LiteralPath $Destination -Filter "llama-server.exe" -File -Recurse |
            Select-Object -First 1
        if ($null -eq $server) {
            throw "El paquete CUDA oficial no contiene llama-server.exe."
        }

        # Normalizamos la ruta esperada aunque el ZIP cambie su carpeta interna.
        if ($server.FullName -ne (Join-Path $Destination "llama-server.exe")) {
            $sourceDir = $server.Directory.FullName
            Get-ChildItem -LiteralPath $sourceDir -File | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Destination $_.Name) -Force
            }
        }
        return (Join-Path $Destination "llama-server.exe")
    }
    finally {
        Remove-Item -LiteralPath $downloadRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $llamaServer) -or -not (Test-Path -LiteralPath $llamaMarker)) {
    try {
        $llamaServer = Install-LlamaCuda `
            -Destination $llamaCudaRoot `
            -BinaryUrl $llamaBinUrl `
            -RuntimeUrl $llamaRuntimeUrl
        [System.IO.File]::WriteAllText(
            $llamaMarker,
            $llamaBuild,
            (New-Object System.Text.UTF8Encoding($false)))
    }
    catch {
        Write-Warning "No se pudo instalar llama.cpp CUDA: $($_.Exception.Message)"
        $llamaServer = $null
    }
}

# Si la instalación CUDA no pudo hacerse, aceptamos un llama-server ya existente
# como último recurso. Es preferible seguir teniendo OCR a fallar completamente.
if (-not $llamaServer -or -not (Test-Path -LiteralPath $llamaServer)) {
    $existing = Get-Command llama-server -ErrorAction SilentlyContinue
    if ($null -ne $existing -and (Test-Path -LiteralPath $existing.Source)) {
        $llamaServer = $existing.Source
        Write-Warning "Usando llama-server existente como fallback. Para máximo rendimiento en NVIDIA vuelve a ejecutar este instalador cuando puedas descargar CUDA."
    }
}

if ($llamaServer -and (Test-Path -LiteralPath $llamaServer)) {
    $pathFile = Join-Path $InstallRoot "llama-server.path"
    [System.IO.File]::WriteAllText(
        $pathFile,
        [IO.Path]::GetFullPath($llamaServer),
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "llama-server CUDA: $llamaServer" -ForegroundColor Green
} else {
    Write-Warning "No se encontró llama-server. PaddleOCR seguirá funcionando con Transformers, pero sin concurrencia VLM acelerada."
}

# Usamos el GGUF OFICIAL de PaddlePaddle. No cambiamos el modelo OCR ni aplicamos
# una cuantización comunitaria: sólo cambiamos el motor y el número de peticiones
# simultáneas dentro de la página.
$llamaModelDir = Join-Path $InstallRoot "models\llama"
New-Item -ItemType Directory -Force -Path $llamaModelDir | Out-Null
$modelFile = Join-Path $llamaModelDir "PaddleOCR-VL-1.6-GGUF.gguf"
$mmprojFile = Join-Path $llamaModelDir "PaddleOCR-VL-1.6-GGUF-mmproj.gguf"
if (-not (Test-Path -LiteralPath $modelFile) -or -not (Test-Path -LiteralPath $mmprojFile)) {
    Write-Host "Preparando PaddleOCR-VL 1.6 GGUF oficial (~1,8 GB)..." -ForegroundColor Cyan
    & $venvPython -c "from huggingface_hub import hf_hub_download; import sys; repo='PaddlePaddle/PaddleOCR-VL-1.6-GGUF'; names=('PaddleOCR-VL-1.6-GGUF.gguf','PaddleOCR-VL-1.6-GGUF-mmproj.gguf'); [hf_hub_download(repo_id=repo, filename=n, local_dir=sys.argv[1]) for n in names]" $llamaModelDir
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudieron descargar los modelos GGUF oficiales de PaddleOCR-VL 1.6."
    }
}

Write-Host "Entorno instalado en $InstallRoot" -ForegroundColor Green
Write-Host "TintaES elegirá automáticamente entre 4 y 16 lecturas de bocadillos simultáneas según la VRAM NVIDIA libre." -ForegroundColor Green
Write-Host "Puedes forzar TINTAES_PADDLE_PAGE_PARALLEL=1..16 si quieres fijar manualmente la concurrencia." -ForegroundColor DarkGray
Write-Host "El OCR mantiene los mismos crops y PaddleOCR-VL 1.6; el cambio es de backend y paralelismo, no de calidad." -ForegroundColor DarkGray
