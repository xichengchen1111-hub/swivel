# Downloads the official Real-ESRGAN x4plus checkpoint, converts it with ../ModelConvert/ (no Python),
# and refuses anything whose SHA-256 differs from the pinned values.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes large downloads very slow
$root = Resolve-Path (Join-Path $PSScriptRoot '../../..')
$target = Join-Path $root 'build/models/realesrgan-x4plus.onnx'
$checkpoint = Join-Path $root 'build/models/RealESRGAN_x4plus.pth'
$onnxSha = '043A257B04A3F1B54D9D7D1AC17B5BECA43FFDD457D55278FB944F96E0BE5994'
$pthSha = '4FA0D38905F75AC06EB49A7951B426670021BE3018265FD191D2125DF9D682F1'
if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $onnxSha) { return }
New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
if (-not ((Test-Path $checkpoint) -and (Get-FileHash $checkpoint -Algorithm SHA256).Hash -eq $pthSha)) {
    $part = "$checkpoint.$([guid]::NewGuid().ToString('N')).part"
    try {
        Invoke-WebRequest -Uri 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth' -OutFile $part -UseBasicParsing
        if ((Get-FileHash $part -Algorithm SHA256).Hash -ne $pthSha) { throw 'RealESRGAN_x4plus.pth does not match the pinned SHA-256.' }
        Move-Item -LiteralPath $part -Destination $checkpoint -Force
    } finally { if (Test-Path $part) { Remove-Item -LiteralPath $part } }
}
$converted = "$target.converted"
try {
    # From Windows/ so its global.json picks the SDK.
    Push-Location (Join-Path $PSScriptRoot '../..')
    try { & dotnet run --project 'ThirdParty/ModelConvert' -c Release -- realesrgan $checkpoint $converted | Out-Null } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Converting the Real-ESRGAN model failed.' }
    if ((Get-FileHash $converted -Algorithm SHA256).Hash -ne $onnxSha) { throw 'The converted Real-ESRGAN model does not match the pinned SHA-256.' }
    Move-Item -LiteralPath $converted -Destination $target -Force
} finally { foreach ($leftover in @($converted, "$converted.part")) { if (Test-Path $leftover) { Remove-Item -LiteralPath $leftover } } }
