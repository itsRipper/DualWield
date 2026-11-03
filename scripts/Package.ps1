param(
    [string]$SptRoot = $env:SPT_ROOT,
    [switch]$SkipBuild
)
# Builds and checks (scripts\Build.ps1), then writes dist\DualWield-<version>.zip, laid out to be
# extracted into the SPT folder, and dist\DualWield-<version>.sha256.txt.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -SptRoot $SptRoot }

$version = ([xml](Get-Content (Join-Path $root 'src\DualWieldLab\DualWieldLab.csproj') -Raw)).Project.PropertyGroup.InformationalVersion
if (!$version) { throw 'InformationalVersion not found in DualWieldLab.csproj.' }
$name = "DualWield-$version"
$dist = Join-Path $root 'dist'
$zip = Join-Path $dist "$name.zip"

# Release layout: archive path -> source file. Nothing else is packaged.
$layout = [ordered]@{
    'BepInEx/plugins/DualWieldLab/DualWieldLab.dll'                                   = 'src\DualWieldLab\bin\Release\DualWieldLab.dll'
    'BepInEx/plugins/DualWieldLab.DualHolster/DualWieldLab.DualHolster.dll'           = 'src\DualWieldLab.DualHolster\bin\Release\DualWieldLab.DualHolster.dll'
    'SPT_Runtime/user/mods/DualWieldLab-DualHolster/DualWieldLab.DualHolster.Server.dll' = 'server\DualWieldLab.DualHolster\bin\Release\DualWieldLab.DualHolster.Server.dll'
    'SPT_Runtime/user/mods/DualWieldLab-DualHolster/config.json'                      = 'server\DualWieldLab.DualHolster\config.json'
    'DualWield-README.md'                                                              = 'README.md'
    'DualWield-LICENSE.txt'                                                            = 'LICENSE'
    'DualWield-THIRD-PARTY-NOTICES.md'                                                 = 'THIRD-PARTY-NOTICES.md'
}
foreach ($source in $layout.Values) {
    if (!(Test-Path -LiteralPath (Join-Path $root $source))) { throw "Missing $source; run scripts\Build.ps1 first." }
}

New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $layout.GetEnumerator()) {
        # Forward slashes: Windows PowerShell 5.1's Compress-Archive writes backslash entry names.
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $entry.Value), $entry.Key, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose() }

$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = @($archive.Entries | ForEach-Object FullName)
    if ($names.Count -ne $layout.Count -or @($names | Where-Object { $_ -match '\\' }).Count -ne 0) { throw 'Unexpected ZIP contents.' }
    if (@($names | Where-Object { $_ -match '\.(pdb|bundle|assets|resS|blend|exe)$' }).Count -ne 0) { throw 'Non-release file in ZIP.' }
}
finally { $archive.Dispose() }

$lines = @(foreach ($entry in $layout.GetEnumerator()) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $root $entry.Value) -Algorithm SHA256).Hash.ToLowerInvariant(), $entry.Key
})
$lines += '{0}  {1}' -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(), "$name.zip"
$sums = Join-Path $dist "$name.sha256.txt"
[IO.File]::WriteAllLines($sums, [string[]]$lines)
$lines
"ZIP: $zip"
