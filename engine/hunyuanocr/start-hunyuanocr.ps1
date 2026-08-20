$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ModelFile = Join-Path $Here "model\hyocr-f16.gguf"
$MmprojFile = Join-Path $Here "model\mmproj-hyocr-f16.gguf"
$ServerCandidates = @(
    (Join-Path $Here "runtime\cuda\llama-server.exe"),
    (Join-Path $Here "llama.cpp\build\bin\Release\llama-server.exe"),
    (Join-Path $Here "llama.cpp\build\bin\llama-server.exe"),
    (Join-Path $Here "llama-server.exe")
)
$Server = $ServerCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($null -eq $Server -or -not (Test-Path $ModelFile) -or -not (Test-Path $MmprojFile)) {
    throw "HunyuanOCR todavía no está preparado. Ejecuta primero .\setup-hunyuanocr.ps1"
}

Write-Host "HunyuanOCR-1.5 · http://127.0.0.1:8080" -ForegroundColor Cyan
Write-Host "Cierra esta ventana para detener el servidor."

& $Server `
    --model $ModelFile `
    --mmproj $MmprojFile `
    --host 127.0.0.1 `
    --port 8080 `
    --alias HYVL `
    --ctx-size 10240 `
    --n-predict 4096 `
    --parallel 1 `
    --gpu-layers all
