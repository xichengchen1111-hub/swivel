param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $PSScriptRoot
try {
    $output = Join-Path $PSScriptRoot "../build/windows/0.87.0/$Runtime"
    $zip = Join-Path $PSScriptRoot "../build/windows/Swivel-Windows-0.87.0-$Runtime.zip"
    if ((Test-Path $output) -or (Test-Path $zip)) { throw 'This version already has output. Preserve it and choose a new version before rebuilding.' }
    & (Join-Path $PSScriptRoot 'ThirdParty/U2Net/fetch-model.ps1') # pinned background model, checked by SHA-256
    if (-not $SkipTests) {
        & dotnet run --project Swivel.Core.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
        if ($env:OS -ne 'Windows_NT') { throw 'Native tests require Windows. Cross-builds must explicitly use -SkipTests.' }
        & dotnet run --project Swivel.Windows.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Windows native checks failed.' }
    }
    New-Item -ItemType Directory -Path $output | Out-Null
    & dotnet publish Swivel.Windows -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    (Get-Content README.md -Raw).Replace('../reports/Windows与macOS兼容调研.md', 'COMPATIBILITY.md') | Set-Content (Join-Path $output 'README.md') -Encoding utf8
    (Get-Content '../reports/Windows与macOS兼容调研.md' -Raw).Replace('../Windows/TESTING.md', 'TESTING.md').Replace('../Windows/PORTING.md', 'PORTING.md').Replace('../Windows/PROGRESS.md', 'PROGRESS.md') | Set-Content (Join-Path $output 'COMPATIBILITY.md') -Encoding utf8
    Copy-Item TESTING.md (Join-Path $output 'TESTING.md')
    Copy-Item PORTING.md (Join-Path $output 'PORTING.md')
    ((Get-Content PROGRESS.md -Raw).Replace('../reports/Windows与macOS兼容调研.md', 'COMPATIBILITY.md') -replace '\[([^\]]+)\]\(([^)]*\.(cs|swift))\)', '$1: `$2`') | Set-Content (Join-Path $output 'PROGRESS.md') -Encoding utf8
    # No Force: a previous archive, including a concurrent one, must not be replaced.
    Compress-Archive -Path "$output/*" -DestinationPath $zip
    Get-FileHash $zip -Algorithm SHA256
    Write-Host "Windows preview package: $zip"
} finally { Pop-Location }
