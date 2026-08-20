param(
    [string]$Release = "b10509",
    [string]$CudaVersion = "12.4"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$RuntimeDir = Join-Path $Here "runtime\cuda"
$TemporaryDir = Join-Path ([IO.Path]::GetTempPath()) ("TintaES-HunyuanCuda-" + [Guid]::NewGuid().ToString("N"))
$BinaryName = "llama-$Release-bin-win-cuda-$CudaVersion-x64.zip"
$CudaRuntimeName = "cudart-llama-bin-win-cuda-$CudaVersion-x64.zip"
$ReleaseRoot = "https://github.com/ggml-org/llama.cpp/releases/download/$Release"

Write-Host "TintaES · runtime CUDA oficial de llama.cpp $Release" -ForegroundColor Cyan
Write-Host "Destino: $RuntimeDir"

$ExistingServer = Join-Path $RuntimeDir "llama-server.exe"
if (Test-Path $ExistingServer) {
    $ExistingDevices = & $ExistingServer --list-devices 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0 -and $ExistingDevices -match "CUDA\d+") {
        Write-Host "El runtime CUDA ya está preparado." -ForegroundColor Green
        Write-Host ($ExistingDevices.Trim())
        return
    }
}

New-Item -ItemType Directory -Path $TemporaryDir -Force | Out-Null
try {
    $BinaryArchive = Join-Path $TemporaryDir $BinaryName
    $CudaRuntimeArchive = Join-Path $TemporaryDir $CudaRuntimeName
    $Expanded = Join-Path $TemporaryDir "expanded"
    New-Item -ItemType Directory -Path $Expanded -Force | Out-Null

    Write-Host "Descargando llama.cpp CUDA $CudaVersion..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri "$ReleaseRoot/$BinaryName" -OutFile $BinaryArchive
    Invoke-WebRequest -Uri "$ReleaseRoot/$CudaRuntimeName" -OutFile $CudaRuntimeArchive

    Expand-Archive -LiteralPath $BinaryArchive -DestinationPath $Expanded -Force
    Expand-Archive -LiteralPath $CudaRuntimeArchive -DestinationPath $Expanded -Force
    if (-not (Test-Path (Join-Path $Expanded "llama-server.exe"))) {
        throw "El paquete oficial no contiene llama-server.exe."
    }

    New-Item -ItemType Directory -Path $RuntimeDir -Force | Out-Null
    Get-ChildItem -LiteralPath $Expanded | Copy-Item -Destination $RuntimeDir -Recurse -Force

    $Server = Join-Path $RuntimeDir "llama-server.exe"
    $Devices = & $Server --list-devices 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $Devices -notmatch "CUDA\d+") {
        throw "El runtime se instaló, pero no puede usar la GPU NVIDIA. Detalle: $Devices"
    }

    [IO.File]::WriteAllText(
        (Join-Path $RuntimeDir "tinta-runtime-version.txt"),
        "$Release`nCUDA $CudaVersion`n",
        [Text.UTF8Encoding]::new($false))
    Write-Host "Runtime CUDA preparado correctamente." -ForegroundColor Green
    Write-Host ($Devices.Trim())
}
finally {
    if (Test-Path $TemporaryDir) {
        Remove-Item -LiteralPath $TemporaryDir -Recurse -Force
    }
}
