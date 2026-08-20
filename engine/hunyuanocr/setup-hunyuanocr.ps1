param(
    [ValidateSet("auto", "cuda", "cpu")]
    [string]$Backend = "auto"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$LlamaDir = Join-Path $Here "llama.cpp"
$ModelDir = Join-Path $Here "model"
$HfDir = Join-Path $ModelDir "hf"
$ToolVenv = Join-Path $Here ".venv"
$ToolPython = Join-Path $ToolVenv "Scripts\python.exe"
$ModelFile = Join-Path $ModelDir "hyocr-f16.gguf"
$MmprojFile = Join-Path $ModelDir "mmproj-hyocr-f16.gguf"
$CudaInstaller = Join-Path $Here "install-cuda-runtime.ps1"

function Require-Command([string]$Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Falta '$Name' en PATH. Instálalo antes de continuar."
    }
    return $command.Source
}

Write-Host "TintaES · preparación local de HunyuanOCR-1.5" -ForegroundColor Cyan
Write-Host "Carpeta: $Here"
Write-Host "Hunyuan usa un entorno Python propio y NO modifica manga-image-translator." -ForegroundColor DarkGray

$Git = Require-Command "git"
$SystemPython = Require-Command "python"

if (-not (Test-Path $ToolPython)) {
    Write-Host "Creando entorno Python aislado para HunyuanOCR..." -ForegroundColor Yellow
    & $SystemPython -m venv $ToolVenv
}

if (-not (Test-Path $ToolPython)) {
    throw "No se pudo crear el entorno Python aislado de HunyuanOCR."
}

if (-not (Test-Path $LlamaDir)) {
    Write-Host "Clonando llama.cpp..." -ForegroundColor Yellow
    & $Git clone --depth 1 https://github.com/ggml-org/llama.cpp.git $LlamaDir
} else {
    Write-Host "Actualizando llama.cpp..." -ForegroundColor Yellow
    & $Git -C $LlamaDir pull --ff-only
}

$HasNvcc = $null -ne (Get-Command "nvcc" -ErrorAction SilentlyContinue)
$HasNvidiaGpu = $null -ne (Get-Command "nvidia-smi" -ErrorAction SilentlyContinue)
$UseCudaBuild = $HasNvcc -and $Backend -ne "cpu"
$UsePrebuiltCuda = -not $HasNvcc -and $HasNvidiaGpu -and $Backend -ne "cpu"
if ($Backend -eq "cuda" -and -not $HasNvcc -and -not $HasNvidiaGpu) {
    throw "Se solicitó CUDA, pero no se detecta ni nvcc ni una GPU NVIDIA compatible."
}

$BuildDir = Join-Path $LlamaDir "build"
if ($UsePrebuiltCuda) {
    if (-not (Test-Path $CudaInstaller)) {
        throw "Falta $CudaInstaller."
    }
    Write-Host "No hay CUDA Toolkit; instalando el runtime CUDA oficial precompilado..." -ForegroundColor Yellow
    & $CudaInstaller
} else {
    $CMake = Require-Command "cmake"
    $CmakeArgs = @("-S", $LlamaDir, "-B", $BuildDir, "-DLLAMA_BUILD_EXAMPLES=ON")
    if ($UseCudaBuild) {
        $CmakeArgs += "-DGGML_CUDA=ON"
        Write-Host "Compilando llama.cpp con CUDA..." -ForegroundColor Yellow
    } else {
        Write-Host "Compilando llama.cpp para CPU..." -ForegroundColor Yellow
    }
    & $CMake @CmakeArgs
    & $CMake --build $BuildDir --config Release --parallel
}

New-Item -ItemType Directory -Path $ModelDir -Force | Out-Null
New-Item -ItemType Directory -Path $HfDir -Force | Out-Null

Write-Host "Instalando utilidades de descarga/conversión en el entorno aislado..." -ForegroundColor Yellow
& $ToolPython -m pip install --upgrade pip
& $ToolPython -m pip install --upgrade "huggingface_hub>=0.34" safetensors sentencepiece protobuf numpy

$LlamaRequirements = Join-Path $LlamaDir "requirements.txt"
if (Test-Path $LlamaRequirements) {
    & $ToolPython -m pip install -r $LlamaRequirements
}

if (-not (Test-Path (Join-Path $HfDir "config.json"))) {
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
'@ | & $ToolPython -
}

$Converter = Join-Path $LlamaDir "convert_hf_to_gguf.py"
if (-not (Test-Path $Converter)) {
    throw "La versión descargada de llama.cpp no contiene convert_hf_to_gguf.py."
}

if (-not (Test-Path $ModelFile)) {
    Write-Host "Convirtiendo el modelo base a GGUF F16..." -ForegroundColor Yellow
    & $ToolPython $Converter --outfile $ModelFile --outtype f16 $HfDir
}

if (-not (Test-Path $MmprojFile)) {
    Write-Host "Convirtiendo el proyector visual a GGUF F16..." -ForegroundColor Yellow
    & $ToolPython $Converter --outfile $MmprojFile --outtype f16 --mmproj $HfDir
}

$ServerCandidates = @(
    (Join-Path $Here "runtime\cuda\llama-server.exe"),
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
Write-Host "Entorno Python aislado: $ToolVenv"
Write-Host "TintaES lo arrancará automáticamente al analizar una página."
Write-Host "Para probarlo manualmente: .\start-hunyuanocr.ps1"
