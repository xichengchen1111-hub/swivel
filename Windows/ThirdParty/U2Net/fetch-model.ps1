# Downloads the pinned U²-Net ONNX file into build/models/ and refuses anything else.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes large downloads very slow
$root = Resolve-Path (Join-Path $PSScriptRoot '../../..')
$target = Join-Path $root 'build/models/u2net.onnx'
$sha = '8D10D2F3BB75AE3B6D527C77944FC5E7DCD94B29809D47A739A7A728A912B491'
if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $sha) { return }
New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
$part = "$target.$([guid]::NewGuid().ToString('N')).part"
try {
    Invoke-WebRequest -Uri 'https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx' -OutFile $part -UseBasicParsing
    if ((Get-FileHash $part -Algorithm SHA256).Hash -ne $sha) { throw 'u2net.onnx does not match the pinned SHA-256.' }
    Move-Item -LiteralPath $part -Destination $target -Force
} finally { if (Test-Path $part) { Remove-Item -LiteralPath $part } }
