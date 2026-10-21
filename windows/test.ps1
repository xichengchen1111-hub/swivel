# Runs the MSTest checks (converters, documents/PDF, wheel hit-testing and sounds, editors' snapping, settings,
# tools, clothesline physics, strings). Extra arguments go to `dotnet test`, e.g. ./test.ps1 --filter ClassName~Wheel
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet test Swivel.Tests\Swivel.Tests.csproj @args
exit $LASTEXITCODE
