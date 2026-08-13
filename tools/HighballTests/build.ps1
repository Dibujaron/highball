<#
    Builds and runs the Highball decision tests.

    Decisions.cs must never reference UnityEngine. This script only supplies the BCL,
    so a Unity dependency creeping in shows up here as a compile error.
#>
[CmdletBinding()]
param([string]$RailroaderDir = "D:\SteamLibrary\steamapps\common\Railroader")

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here "..\..")

$managed = Join-Path $RailroaderDir "Railroader_Data\Managed"
if (-not (Test-Path $managed)) { throw "Managed directory not found: $managed" }

$csc = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
if (-not (Test-Path $csc)) { throw "Roslyn compiler not found at $csc" }

$outDir = Join-Path $here "bin"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outExe = Join-Path $outDir "HighballTests.exe"

$refs = @("mscorlib.dll", "System.dll", "System.Core.dll") | ForEach-Object {
    "-r:$(Join-Path $managed $_)"
}

$sources = @(
    (Join-Path $repo "src\Highball\Decisions.cs"),
    (Join-Path $here "Tests.cs")
)

& $csc (@("-nologo", "-noconfig", "-nostdlib+", "-target:exe",
          "-langversion:7.3", "-warn:4", "-out:$outExe") + $refs + $sources)
if ($LASTEXITCODE -ne 0) { throw "Test compilation failed with exit code $LASTEXITCODE" }

& $outExe
exit $LASTEXITCODE
