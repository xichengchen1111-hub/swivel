# Downloads the semantic search model into this folder from a pinned commit and checks every byte (see UPSTREAM.md).
# Run once on a development PC before building; build.ps1 calls it when the files are missing.
$ErrorActionPreference = 'Stop'
$commit = '761b726dd34fb83930e26aab4e9ac3899aa1fa78'
$files = [ordered]@{
    'onnx/model_quantized.onnx' = 'f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193'
    'tokenizer.json'            = '0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39'
}
foreach ($entry in $files.GetEnumerator()) {
    $target = Join-Path $PSScriptRoot (Split-Path $entry.Key -Leaf)
    if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash.ToLower() -eq $entry.Value) { continue }
    $temporary = "$target.download"
    Invoke-WebRequest "https://huggingface.co/Xenova/multilingual-e5-small/resolve/$commit/$($entry.Key)" -OutFile $temporary -UseBasicParsing
    $hash = (Get-FileHash $temporary -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $entry.Value) { Remove-Item $temporary; throw "$($entry.Key): unexpected SHA-256 $hash" }
    Move-Item $temporary $target -Force
}
Write-Host 'Semantic search model ready.'
