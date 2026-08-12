<#
    Builds AEProbe without requiring a .NET Framework targeting pack.

    We compile directly against the Mono BCL that Railroader ships. That is the
    runtime the mod actually executes on, so this is more faithful than building
    against a desktop .NET Framework reference assembly set.

    Usage:
        .\build.ps1
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
if (-not (Test-Path $managed)) {
    throw "Managed directory not found: $managed"
}

$csc = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if (-not (Test-Path $csc)) {
    throw "Roslyn compiler not found at $csc"
}

$outDir = Join-Path $here "bin\Release"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outDll = Join-Path $outDir "AEProbe.dll"

$refs = @(
    "mscorlib.dll",
    "System.dll",
    "System.Core.dll",
    "netstandard.dll",
    "UnityEngine.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.IMGUIModule.dll",
    "Assembly-CSharp.dll",
    "UnityModManager\UnityModManager.dll",
    "UnityModManager\0Harmony.dll"
) | ForEach-Object {
    $p = Join-Path $managed $_
    if (-not (Test-Path $p)) { throw "Missing reference assembly: $p" }
    "-r:$p"
}

$sources = @(
    (Join-Path $here "Main.cs"),
    (Join-Path $here "Probe.cs"),
    (Join-Path $here "Properties\AssemblyInfo.cs")
)

$cscArgs = @(
    "-nologo",
    "-noconfig",
    "-nostdlib+",
    "-target:library",
    "-optimize+",
    "-langversion:7.3",
    "-warn:4",
    "-out:$outDll"
) + $refs + $sources

Write-Host "Compiling AEProbe..."
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
Write-Host "Built $outDll"

if ($Deploy) {
    $dest = Join-Path $RailroaderDir "Mods\AEProbe"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $outDll $dest -Force
    Copy-Item (Join-Path $here "Info.json") $dest -Force
    Write-Host "Deployed to $dest"
}
