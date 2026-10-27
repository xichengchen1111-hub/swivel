# Signs the MSIX packages made by build-msix.ps1 with the self-signed test certificate.
# For sideload testing only: testers must trust the exported .cer first.
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Sign on Windows with the Windows SDK installed.' }
$parent = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../build/windows'))
$unsigned = @(Get-ChildItem -LiteralPath $parent -Filter '*-unsigned.msix' -File)
if ($unsigned.Count -eq 0) { throw 'No unsigned MSIX packages found. Run build-msix.ps1 first.' }
if (-not $env:PFX_BASE64 -or -not $env:PFX_PASSWORD) {
    Write-Host 'No test certificate secrets are configured; packages stay unsigned and cannot be installed.'
    exit 0
}
$kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$signtool = Get-ChildItem -LiteralPath $kits -Directory | Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
    Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1
if (-not $signtool) { throw 'Windows SDK SignTool.exe is required; no tools are downloaded.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('swivel-sign-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $pfx = Join-Path $work 'test.pfx'
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:PFX_BASE64))
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx, $env:PFX_PASSWORD)
    if ($certificate.Subject -ne 'CN=Swivel Windows Preview') { throw 'The certificate subject must match the manifest Publisher.' }
    foreach ($package in $unsigned) {
        $signed = Join-Path $parent ($package.Name -replace '-unsigned\.msix$', '.msix')
        if (Test-Path -LiteralPath $signed) { throw "A signed package already exists: $signed" }
        Copy-Item -LiteralPath $package.FullName -Destination $signed
        & $signtool sign /fd SHA256 /f $pfx /p $env:PFX_PASSWORD $signed
        if ($LASTEXITCODE -ne 0) { throw 'SignTool failed.' }
        Get-FileHash -LiteralPath $signed -Algorithm SHA256
    }
    [IO.File]::WriteAllBytes((Join-Path $parent 'Swivel-Windows-Preview-test-certificate.cer'), $certificate.Export('Cert'))
} finally { Remove-Item -LiteralPath $work -Recurse -Force }
