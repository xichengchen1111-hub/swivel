# Native checks only (CI, before the suite): creates small exFAT and FAT32 virtual disks for the native checks (CI runners are admin;
# the app itself never needs elevation). Exports SWIVEL_TEST_EXFAT / SWIVEL_TEST_FAT32.
$ErrorActionPreference = 'Stop'
$base = Join-Path $env:RUNNER_TEMP 'swivel-fat'
New-Item -ItemType Directory -Force $base | Out-Null
foreach ($v in @(@{fs='exfat';letter='X';name='SWIVELEX';env='SWIVEL_TEST_EXFAT'}, @{fs='fat32';letter='Y';name='SWIVELF32';env='SWIVEL_TEST_FAT32'})) {
    $file = Join-Path $base ($v.fs + '.vhdx')
    $script = Join-Path $base ($v.fs + '.txt')
    @(
        "create vdisk file=`"$file`" maximum=96 type=expandable",
        "select vdisk file=`"$file`"",
        'attach vdisk',
        'convert mbr',
        'create partition primary',
        "format fs=$($v.fs) quick label=$($v.name)",
        "assign letter=$($v.letter)"
    ) | Set-Content -Encoding ascii $script
    diskpart /s $script | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "diskpart failed for $($v.fs)" }
    $root = "$($v.letter):\"
    $format = (Get-Volume -DriveLetter $v.letter).FileSystemType
    Write-Host "$root is $format"
    "$($v.env)=$root" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
}
