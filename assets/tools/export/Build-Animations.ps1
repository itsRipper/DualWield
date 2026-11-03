param([string]$BlenderPath = $env:BLENDER_EXE, [switch]$Regenerate, [switch]$Review)
# Bakes assets/blender/animations/dw_reload_choreography.blend into assets/exports/unity/*.json,
# which DualWieldLab.dll embeds. -Regenerate first rebuilds the three .blend files from
# build_assets.py (discarding hand edits); -Review renders a local contact sheet.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (!$BlenderPath) { $BlenderPath = Join-Path $env:ProgramFiles 'Blender Foundation\Blender 5.2\blender.exe' }
if (!(Test-Path -LiteralPath $BlenderPath)) { throw 'Blender 5.2+ not found; supply -BlenderPath.' }
$blend = Join-Path $root 'assets\blender\animations\dw_reload_choreography.blend'
function Invoke-Blender([string[]]$arguments) {
    $output = & $BlenderPath @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { $output | Select-Object -Last 30; throw 'Blender step failed.' }
    $output | Where-Object { $_ -match '^@@JSON@@' } | ForEach-Object { $_.Substring(8) }
}
if ($Regenerate) { Invoke-Blender @('-b', '--factory-startup', '--python-exit-code', '1', '-P', (Join-Path $PSScriptRoot 'build_assets.py'), '--', '--root', $root) }
Invoke-Blender @('-b', $blend, '--factory-startup', '--python-exit-code', '1', '-P', (Join-Path $PSScriptRoot 'export_clips.py'))
if ($Review) {
    Invoke-Blender @('-b', $blend, '--factory-startup', '--python-exit-code', '1', '-P', (Join-Path $PSScriptRoot 'review_frames.py'), '--', '--out', (Join-Path $root 'artifacts\animation-review'))
}
