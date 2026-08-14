<#
    Builds Highball against the Mono BCL that Railroader ships, avoiding any
    need for a .NET Framework targeting pack.

    Usage:
        .\build.ps1
        .\build.ps1 -Deploy
        .\build.ps1 -RailroaderDir "C:\Path\To\Railroader" -Deploy
#>
[CmdletBinding()]
param(
    [string]$RailroaderDir = "D:\SteamLibrary\steamapps\common\Railroader",
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$managed = Join-Path $RailroaderDir "Railroader_Data\Managed"
if (-not (Test-Path $managed)) { throw "Managed directory not found: $managed" }

$csc = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if (-not (Test-Path $csc)) { throw "Roslyn compiler not found at $csc" }

$outDir = Join-Path $here "bin\Release"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outDll = Join-Path $outDir "Highball.dll"

$refs = @(
    "mscorlib.dll",
    "System.dll",
    "System.Core.dll",
    "netstandard.dll",
    "UnityEngine.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.PhysicsModule.dll",
    "UnityEngine.IMGUIModule.dll",
    "UnityEngine.TerrainModule.dll",
    "Assembly-CSharp.dll",
    "Definition.dll",
    "UnityModManager\UnityModManager.dll",
    "UnityModManager\0Harmony.dll"
) | ForEach-Object {
    $p = Join-Path $managed $_
    if (-not (Test-Path $p)) { throw "Missing reference assembly: $p" }
    "-r:$p"
}

$sources = @(
    (Join-Path $here "Main.cs"),
    (Join-Path $here "Settings.cs"),
    (Join-Path $here "Decisions.cs"),
    (Join-Path $here "CarFacts.cs"),
    (Join-Path $here "TrackedCar.cs"),
    (Join-Path $here "CarRegistry.cs"),
    (Join-Path $here "Evaluator.cs"),
    (Join-Path $here "IFeature.cs"),
    (Join-Path $here "FeatureHost.cs"),
    (Join-Path $here "CarRendererFeature.cs"),
    (Join-Path $here "SolverLodFeature.cs"),
    (Join-Path $here "TerrainLodFeature.cs"),
    (Join-Path $here "SleepHeadroomProbe.cs"),
    (Join-Path $here "Telemetry.cs"),
    (Join-Path $here "Properties\AssemblyInfo.cs")
)

$cscArgs = @(
    "-nologo", "-noconfig", "-nostdlib+",
    "-target:library", "-optimize+", "-langversion:7.3", "-warn:4",
    "-out:$outDll"
) + $refs + $sources

Write-Host "Compiling Highball..."
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
Write-Host "Built $outDll"

if ($Deploy) {
    $dest = Join-Path $RailroaderDir "Mods\Highball"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $outDll $dest -Force
    Copy-Item (Join-Path $here "Info.json") $dest -Force
    Write-Host "Deployed to $dest"
}
