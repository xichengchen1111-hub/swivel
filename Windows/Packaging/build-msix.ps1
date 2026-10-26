param([ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64')
$ErrorActionPreference='Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Build this optional MSIX on Windows with the Windows SDK installed. No package is signed or installed by this script.' }
$version='0.87.0'
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../build/windows/$version/$Runtime"))
if (-not (Test-Path (Join-Path $source 'Swivel.exe') -PathType Leaf)) { throw 'Build the corresponding portable application first.' }
$kits=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$tools=Get-ChildItem -LiteralPath $kits -Directory | Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending
$makeappx=$null
foreach ($tool in $tools) { $candidate=Join-Path $tool.FullName 'x64/makeappx.exe'; if (Test-Path $candidate -PathType Leaf) { $makeappx=$candidate;break } }
if (-not $makeappx) { throw 'Windows SDK MakeAppx.exe is required; no tools are downloaded.' }
$parent=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../build/windows'))
$work=Join-Path $parent ('.msix-'+[guid]::NewGuid().ToString('N'))
$stage=Join-Path $work 'package'
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    # A published application is a build input, never modified in place.
    if ((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Published root is a reparse point.' }
    if (Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Published input contains a reparse point.' }
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets') -Destination $stage -Recurse
    [xml]$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
    $manifest.Package.Identity.SetAttribute('ProcessorArchitecture',$(if($Runtime -eq 'win-x64'){'x64'}else{'arm64'}))
    $manifest.Save((Join-Path $stage 'AppxManifest.xml'))
    $temporary=Join-Path $work 'application.msix'
    & $makeappx pack /d $stage /p $temporary
    if ($LASTEXITCODE -ne 0) { throw 'MakeAppx validation or packing failed.' }
    $destination=Join-Path $parent "Swivel-Windows-$version-$Runtime-unsigned.msix"
    # Same-volume move refuses an existing destination, including a concurrent one.
    [IO.File]::Move($temporary,$destination)
    Get-FileHash -LiteralPath $destination -Algorithm SHA256
    Write-Host 'Unsigned package created. Signing, certificate trust, installation and packaged feature acceptance are separate manual steps. This script performs none of them.'
} finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
