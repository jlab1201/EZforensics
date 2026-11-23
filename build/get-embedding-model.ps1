<#
.SYNOPSIS
  Downloads the bge-small-en-v1.5 quantized ONNX embedding model (used by the knowledge engine) into models\bge-small-en-v1.5\.
.DESCRIPTION
  Source: Hugging Face repo Xenova/bge-small-en-v1.5, pinned to one commit. The SHA-256 of both files is checked.
  The App project copies models\** to the output when the folder exists. Without the files the app still works (text search only).
#>
param([string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) 'models\bge-small-en-v1.5'))

$ErrorActionPreference = 'Stop'
$revision = 'ea104dacec62c0de699686887e3f920caeb4f3e3'
$files = @(
    @{ Remote = 'onnx/model_quantized.onnx'; Local = 'model_quantized.onnx'; Sha256 = '6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4' },
    @{ Remote = 'vocab.txt';                 Local = 'vocab.txt';             Sha256 = '07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3' }
)

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
foreach ($f in $files) {
    $target = Join-Path $Destination $f.Local
    if ((Test-Path $target) -and ((Get-FileHash $target -Algorithm SHA256).Hash -ieq $f.Sha256)) {
        Write-Host "OK (already present): $($f.Local)"
        continue
    }
    $url = "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/$revision/$($f.Remote)"
    Write-Host "Downloading $url"
    $tmp = "$target.part"
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
    $hash = (Get-FileHash $tmp -Algorithm SHA256).Hash
    if ($hash -ine $f.Sha256) {
        Remove-Item $tmp -Force
        throw "SHA-256 mismatch for $($f.Local): expected $($f.Sha256), got $hash"
    }
    Move-Item $tmp $target -Force
    Write-Host "OK: $($f.Local)"
}
Write-Host "Embedding model ready in $Destination"
