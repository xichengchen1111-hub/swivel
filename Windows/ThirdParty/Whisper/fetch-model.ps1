# Downloads the official Whisper base checkpoint and its multilingual tokenizer, converts the
# checkpoint with ../ModelConvert/ (no Python), and refuses anything whose SHA-256 differs from the pinned values.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes large downloads very slow
$root = Resolve-Path (Join-Path $PSScriptRoot '../../..')
$models = Join-Path $root 'build/models'
$encoder = Join-Path $models 'whisper-base-encoder.onnx'
$decoder = Join-Path $models 'whisper-base-decoder.onnx'
$tokens = Join-Path $models 'whisper-multilingual.tiktoken'
$checkpoint = Join-Path $models 'whisper-base.pt'
$encoderSha = '4AF8CAAA455D00DCA07D6EBC6333479295AD3A7D738639120D0458F44BF9891F'
$decoderSha = '7D6F84754DF3A51D65A5469608CD75C2ADDB687DB51279ACBD9235E3DA41DAF1'
$tokensSha = 'B34B360DBB493E781E479794586D661700670D65564001F23024971D1F2FA126'
$ptSha = 'ED3A0B6B1C0EDF879AD9B11B1AF5A0E6AB5DB9205F891F668F8B0E6C6326E34E'
function Matches($file, $sha) { (Test-Path $file) -and (Get-FileHash $file -Algorithm SHA256).Hash -eq $sha }
function Fetch($url, $file, $sha) {
    if (Matches $file $sha) { return }
    $part = "$file.$([guid]::NewGuid().ToString('N')).part"
    try {
        Invoke-WebRequest -Uri $url -OutFile $part -UseBasicParsing
        if ((Get-FileHash $part -Algorithm SHA256).Hash -ne $sha) { throw "$(Split-Path $file -Leaf) does not match the pinned SHA-256." }
        Move-Item -LiteralPath $part -Destination $file -Force
    } finally { if (Test-Path $part) { Remove-Item -LiteralPath $part } }
}
New-Item -ItemType Directory -Force -Path $models | Out-Null
Fetch 'https://raw.githubusercontent.com/openai/whisper/31243bad24cc746f07d4c8bfdd2d974872cb1803/whisper/assets/multilingual.tiktoken' $tokens $tokensSha
if ((Matches $encoder $encoderSha) -and (Matches $decoder $decoderSha)) { return }
Fetch "https://openaipublic.azureedge.net/main/whisper/models/$($ptSha.ToLowerInvariant())/base.pt" $checkpoint $ptSha
$leftovers = @("$encoder.converted", "$encoder.converted.part", "$decoder.converted", "$decoder.converted.part")
try {
    # From Windows/ so its global.json picks the SDK.
    Push-Location (Join-Path $PSScriptRoot '../..')
    try { & dotnet run --project 'ThirdParty/ModelConvert' -c Release -- whisper $checkpoint "$encoder.converted" "$decoder.converted" | Out-Null } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Converting the Whisper model failed.' }
    if (-not ((Matches "$encoder.converted" $encoderSha) -and (Matches "$decoder.converted" $decoderSha))) { throw 'The converted Whisper model does not match the pinned SHA-256.' }
    Move-Item -LiteralPath "$encoder.converted" -Destination $encoder -Force
    Move-Item -LiteralPath "$decoder.converted" -Destination $decoder -Force
} finally { foreach ($leftover in $leftovers) { if (Test-Path $leftover) { Remove-Item -LiteralPath $leftover } } }
