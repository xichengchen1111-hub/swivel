# Builds Swivel for Windows into windows\build\Swivel\Swivel.exe.
#   ./build.ps1                 framework-dependent (needs the .NET 10 Desktop Runtime), ~small
#   ./build.ps1 -SelfContained  bundles the runtime into one Swivel.exe (no runtime needed on the target PC)
#   ./build.ps1 -Arch arm64     for Windows on Arm
param(
    [switch]$SelfContained,
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot 'build\Swivel'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$publish = @('publish', 'Swivel\Swivel.csproj', '-c', 'Release', '-r', "win-$Arch", '-o', $out, '-p:DebugType=none')
if ($SelfContained) {
    $publish += @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
} else {
    $publish += @('--self-contained', 'false')
}
dotnet @publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $out\Swivel.exe"
