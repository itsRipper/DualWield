param(
    [string]$SptRoot = $env:SPT_ROOT,
    [string]$BlenderPath = $env:BLENDER_EXE,
    [string]$FikaCore = $env:FIKA_CORE
)
# Builds the runtime plugin, its Fika integration, the dual-holster client plugin and the
# dual-holster server mod, then runs the contract checks. The SPT installation and Fika.Core are
# only read (assembly references, item data); Fika is not redistributed.
# ContinuousIntegrationBuild maps source paths, so the DLLs do not depend on the checkout folder.
$ErrorActionPreference = 'Stop'
if (!$SptRoot) { throw 'Supply -SptRoot (the SPT 4.1.6 folder) or set SPT_ROOT.' }
$SptRoot = [IO.Path]::GetFullPath($SptRoot)
foreach ($required in 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll', 'BepInEx\core\BepInEx.dll', 'SPT_Runtime\SPTarkov.Server.Core.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $SptRoot $required))) { throw "Not an SPT 4.1 installation: $required is missing under $SptRoot." }
}
# The Fika integration compiles against Fika.Core 2.4.x: the one installed in SptRoot, or -FikaCore.
if (!$FikaCore) { $FikaCore = Join-Path $SptRoot 'BepInEx\plugins\Fika\Fika.Core.dll' }
if (!(Test-Path -LiteralPath $FikaCore)) { throw "Fika.Core.dll (Fika 2.4.x for SPT 4.1) not found at $FikaCore. Supply -FikaCore or set FIKA_CORE." }
$FikaCore = [IO.Path]::GetFullPath($FikaCore)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$logs = Join-Path $root 'artifacts\logs'
New-Item -ItemType Directory -Force $logs | Out-Null

function Invoke-Step([string]$name, [scriptblock]$command) {
    Write-Host "== $name"
    $log = Join-Path $logs ($name + '.log')
    # dotnet writes progress to stderr; Windows PowerShell 5.1 would treat it as a terminating error.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $command 2>&1 | Tee-Object -FilePath $log | Out-Host; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw "$name failed (exit code $code); see $log." }
}

Push-Location $root
try {
    Invoke-Step 'build-runtime' { dotnet build 'src\DualWieldLab\DualWieldLab.csproj' -c Release "-p:SptRoot=$SptRoot" -p:ContinuousIntegrationBuild=true --nologo }
    Invoke-Step 'build-fika-integration' { dotnet build 'src\DualWieldLab.Fika\DualWieldLab.Fika.csproj' -c Release "-p:SptRoot=$SptRoot" "-p:FikaCore=$FikaCore" -p:ContinuousIntegrationBuild=true --nologo }
    Invoke-Step 'build-dual-holster-client' { dotnet build 'src\DualWieldLab.DualHolster\DualWieldLab.DualHolster.csproj' -c Release "-p:SptRoot=$SptRoot" -p:ContinuousIntegrationBuild=true --nologo }
    Invoke-Step 'build-dual-holster-server' { dotnet build 'server\DualWieldLab.DualHolster\DualWieldLab.DualHolster.Server.csproj' -c Release "-p:SptRoot=$SptRoot" -p:ContinuousIntegrationBuild=true --nologo }
    $cecil = Join-Path $SptRoot 'BepInEx\core\Mono.Cecil.dll'
    Invoke-Step 'offline-checks' { dotnet run --project 'tests\OfflineChecks\OfflineChecks.csproj' "-p:CecilPath=$cecil" -- $root $SptRoot }
    Invoke-Step 'inventory-checks' { dotnet run --project 'tests\InventoryChecks\InventoryChecks.csproj' "-p:SptRoot=$SptRoot" -- $root $SptRoot }
    Invoke-Step 'dual-holster-checks' { dotnet run --project 'tests\DualHolsterChecks\DualHolsterChecks.csproj' "-p:SptRoot=$SptRoot" -- $root $SptRoot }
    Invoke-Step 'config-migration-checks' { dotnet run --project 'tests\ConfigMigrationChecks\ConfigMigrationChecks.csproj' "-p:SptRoot=$SptRoot" }

    # Optional: the embedded reload clips must match a fresh export of their Blender source.
    if (!$BlenderPath -and $env:ProgramFiles) { $BlenderPath = Join-Path $env:ProgramFiles 'Blender Foundation\Blender 5.2\blender.exe' }
    if ($BlenderPath -and (Test-Path -LiteralPath $BlenderPath)) {
        $check = Join-Path $root 'artifacts\clip-check'
        Invoke-Step 'clip-export-check' { & $BlenderPath -b 'assets\blender\animations\dw_reload_choreography.blend' --factory-startup --python-exit-code 1 -P 'assets\tools\export\export_clips.py' -- --out $check }
        foreach ($clip in Get-ChildItem 'assets\exports\unity' -Filter 'dw_*.json') {
            if ((Get-FileHash $clip.FullName).Hash -ne (Get-FileHash (Join-Path $check $clip.Name)).Hash) {
                throw "Exported clip $($clip.Name) is stale; run assets\tools\export\Build-Animations.ps1."
            }
        }
        Write-Host 'Embedded reload clips match their Blender source.'
    }
    else { Write-Host 'Blender 5.2 not found; the clip export check was skipped.' }

    Write-Host ''
    foreach ($output in 'src\DualWieldLab\bin\Release\DualWieldLab.dll', 'src\DualWieldLab.Fika\bin\Release\DualWieldLab.Fika.dll', 'src\DualWieldLab.DualHolster\bin\Release\DualWieldLab.DualHolster.dll', 'server\DualWieldLab.DualHolster\bin\Release\DualWieldLab.DualHolster.Server.dll') {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant(), $output
    }
}
finally { Pop-Location }
