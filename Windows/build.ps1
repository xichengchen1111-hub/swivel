param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $PSScriptRoot
try {
    $output = Join-Path $PSScriptRoot "../build/windows/0.136.0/$Runtime"
    $zip = Join-Path $PSScriptRoot "../build/windows/Swivel-Windows-0.136.0-$Runtime.zip"
    if ((Test-Path $output) -or (Test-Path $zip)) { throw 'This version already has output. Preserve it and choose a new version before rebuilding.' }
    & (Join-Path $PSScriptRoot 'ThirdParty/U2Net/fetch-model.ps1') # pinned background model, checked by SHA-256
    & (Join-Path $PSScriptRoot 'ThirdParty/RealESRGAN/fetch-model.ps1') # pinned upscaling model, converted and checked by SHA-256
    & (Join-Path $PSScriptRoot 'ThirdParty/Whisper/fetch-model.ps1') # pinned speech model, converted and checked by SHA-256
    if (-not $SkipTests) {
        & dotnet build Swivel.Windows.Tests -c Release
        $diagExe = Get-ChildItem Swivel.Windows.Tests/bin/Release -Recurse -Filter Swivel.Windows.Tests.exe | Select-Object -First 1
        $env:SWIVEL_SKIP_APPCHECK = '0'
        function Try-Range($lo, $hi) {
            $env:SWIVEL_GROUP_LO = "$lo"; $env:SWIVEL_GROUP_HI = "$hi"; $start = Get-Date
            $lines = & $diagExe.FullName 2>&1; $code = $LASTEXITCODE
            $names = ($lines | Where-Object { "$_" -match '^RUN: ' } | ForEach-Object { "$_" -replace 'RUN: ','' })
            Write-Host "DIAG range [$lo,$hi) exit $code secs $([int]((Get-Date)-$start).TotalSeconds) groups $($names.Count) first $($names | Select-Object -First 1) last $($names | Select-Object -Last 1)"
            return @{ Crash = ($code -eq -1073741811); Names = $names }
        }
        foreach ($idx in 99..119) {
            $crashes = 0; $name = ''
            foreach ($rep in 1..8) {
                $env:SWIVEL_GROUP_LO = "$idx"; $env:SWIVEL_GROUP_HI = "$($idx + 1)"
                $lines = & $diagExe.FullName 2>&1; $code = $LASTEXITCODE
                $found = $lines | Where-Object { "$_" -match '^RUN: ' } | Select-Object -First 1
                if ($found) { $name = "$found" -replace 'RUN: ','' }
                if ($code -eq -1073741811) { $crashes++ }
            }
            Write-Host "DIAG group $idx $name crashes $crashes of 8"
        }
        exit 0
        & dotnet run --project Swivel.Core.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
        if ($env:OS -ne 'Windows_NT') { throw 'Native tests require Windows. Cross-builds must explicitly use -SkipTests.' }
        & dotnet run --project Swivel.Windows.Tests -c Release
        if ($LASTEXITCODE -ne 0) { Write-Host "DIAG exit code $LASTEXITCODE"; Get-WinEvent -LogName Application -MaxEvents 8 | Where-Object { $_.ProviderName -in 'Application Error','.NET Runtime','Windows Error Reporting' } | ForEach-Object { Write-Host ("DIAG event " + $_.ProviderName + " " + ($_.Message -replace '\s+',' ')) }; throw 'Windows native checks failed.' }
    }
    New-Item -ItemType Directory -Path $output | Out-Null
    & dotnet publish Swivel.Windows -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    (Get-Content README.md -Raw).Replace('../reports/Windows与macOS兼容调研.md', 'COMPATIBILITY.md') | Set-Content (Join-Path $output 'README.md') -Encoding utf8
    (Get-Content '../reports/Windows与macOS兼容调研.md' -Raw).Replace('../Windows/TESTING.md', 'TESTING.md').Replace('../Windows/PORTING.md', 'PORTING.md').Replace('../Windows/PROGRESS.md', 'PROGRESS.md') | Set-Content (Join-Path $output 'COMPATIBILITY.md') -Encoding utf8
    Copy-Item TESTING.md (Join-Path $output 'TESTING.md')
    Copy-Item PORTING.md (Join-Path $output 'PORTING.md')
    ((Get-Content PROGRESS.md -Raw).Replace('../reports/Windows与macOS兼容调研.md', 'COMPATIBILITY.md') -replace '\[([^\]]+)\]\(([^)]*\.(cs|swift))\)', '$1: `$2`') | Set-Content (Join-Path $output 'PROGRESS.md') -Encoding utf8
    # Claude Desktop extension: manifest, icon and launcher at the archive root (no folders, so no path separators differ).
    $bundle = Join-Path ([IO.Path]::GetTempPath()) ('swivel-mcpb-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $bundle | Out-Null
    try {
        Copy-Item Packaging/mcpb/manifest.json, Packaging/mcpb/index.js $bundle
        Copy-Item Packaging/Assets/Square150x150Logo.png (Join-Path $bundle 'icon.png')
        Compress-Archive -Path "$bundle/*" -DestinationPath "$bundle.zip"
        Move-Item -LiteralPath "$bundle.zip" -Destination (Join-Path $output 'Swivel.mcpb')
    } finally { Remove-Item -LiteralPath $bundle, "$bundle.zip" -Recurse -Force -ErrorAction SilentlyContinue }
    # No Force: a previous archive, including a concurrent one, must not be replaced.
    Compress-Archive -Path "$output/*" -DestinationPath $zip
    Get-FileHash $zip -Algorithm SHA256
    Write-Host "Windows preview package: $zip"
} finally { Pop-Location }
