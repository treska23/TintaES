param(
    [ValidateSet("auto", "cuda", "cpu")]
    [string]$Backend = "auto"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$EngineRoot = Split-Path -Parent $Here
$RepoRoot = Split-Path -Parent $EngineRoot
$LlamaDir = Join-Path $Here "llama.cpp"
$ModelDir = Join-Path $Here "model"
$HfDir = Join-Path $ModelDir "hf"
$ModelFile = Join-Path $ModelDir "hyocr-f16.gguf"
$MmprojFile = Join-Path $ModelDir "mmproj-hyocr-f16.gguf"

function Require-Command([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Falta '$Name' en PATH. Instálalo antes de continuar."
    }
    return $command.Source
}

Write-Host "TintaES · preparación local de HunyuanOCR-1.5" -ForegroundColor Cyan
Write-Host "Carpeta: $Here"

$Git = Require-Command "git"
$CMake = Require-Command "cmake"

$ProjectPython = Join-Path $RepoRoot "engine\manga-image-translator\.venv\Scripts\python.exe"
if (Test-Path $ProjectPython) {
    $Python = $ProjectPython
} else {
    $Python = Require-Command "python"
}

if (-not (Test-Path $LlamaDir)) {
    Write-Host "Clonando llama.cpp..." -ForegroundColor Yellow
    & $Git clone --depth 1 https://github.com/ggml-org/llama.cpp.git $LlamaDir
} else {
    Write-Host "Actualizando llama.cpp..." -ForegroundColor Yellow
    & $Git -C $LlamaDir pull --ff-only
}

$UseCuda = $false
if ($Backend -eq "cuda") {
    $UseCuda = $true
} elseif ($Backend -eq "auto") {
    $UseCuda = $null -ne (Get-Command "nvcc" -ErrorAction SilentlyContinue)
}

$BuildDir = Join-Path $LlamaDir "build"
$CmakeArgs = @("-S", $LlamaDir, "-B", $BuildDir, "-DLLAMA_BUILD_EXAMPLES=ON")
if ($UseCuda) {
    $CmakeArgs += "-DGGML_CUDA=ON"
    Write-Host "Compilando llama.cpp con CUDA..." -ForegroundColor Yellow
} else {
    Write-Host "Compilando llama.cpp para CPU..." -ForegroundColor Yellow
}
& $CMake @CmakeArgs
& $CMake --build $BuildDir --config Release --parallel

New-Item -ItemType Directory -Path $ModelDir -Force | Out-Null
New-Item -ItemType Directory -Path $HfDir -Force | Out-Null

Write-Host "Instalando utilidades de descarga/conversión..." -ForegroundColor Yellow
& $Python -m pip install --upgrade "huggingface_hub>=0.34" safetensors sentencepiece protobuf numpy

$LlamaRequirements = Join-Path $LlamaDir "requirements.txt"
if (Test-Path $LlamaRequirements) {
    & $Python -m pip install -r $LlamaRequirements
}

if (-not (Test-Path (Join-Path $HfDir "model.safetensors"))) {
    Write-Host "Descargando HunyuanOCR-1.5 oficial (puede tardar; son varios GB)..." -ForegroundColor Yellow
    $env:TINTAES_HYOCR_HF_DIR = $HfDir
    @'
import os
from huggingface_hub import snapshot_download
snapshot_download(
    repo_id="tencent/HunyuanOCR",
    local_dir=os.environ["TINTAES_HYOCR_HF_DIR"],
    ignore_patterns=["v1.0/*", "dflash/*"],
)
'@ | & $Python -
}

$Converter = Join-Path $LlamaDir "convert_hf_to_gguf.py"
if (-not (Test-Path $Converter)) {
    throw "La versión descargada de llama.cpp no contiene convert_hf_to_gguf.py."
}

if (-not (Test-Path $ModelFile)) {
    Write-Host "Convirtiendo el modelo base a GGUF F16..." -ForegroundColor Yellow
    & $Python $Converter --outfile $ModelFile --outtype f16 $HfDir
}

if (-not (Test-Path $MmprojFile)) {
    Write-Host "Convirtiendo el proyector visual a GGUF F16..." -ForegroundColor Yellow
    & $Python $Converter --outfile $MmprojFile --outtype f16 --mmproj $HfDir
}

$ServerCandidates = @(
    (Join-Path $BuildDir "bin\Release\llama-server.exe"),
    (Join-Path $BuildDir "bin\llama-server.exe")
)
$Server = $ServerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($null -eq $Server) {
    throw "La compilación terminó pero no encuentro llama-server.exe."
}

Write-Host ""
Write-Host "HunyuanOCR-1.5 preparado correctamente." -ForegroundColor Green
Write-Host "Modelo: $ModelFile"
Write-Host "Proyector: $MmprojFile"
Write-Host "Servidor: $Server"
Write-Host "TintaES lo arrancará automáticamente al analizar una página."
Write-Host "Para probarlo manualmente: .\start-hunyuanocr.ps1"
