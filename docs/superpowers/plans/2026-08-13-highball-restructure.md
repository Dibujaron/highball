# Highball Restructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename `StockPhysicsLOD` to Highball and restructure it from a single-purpose mod into a core plus independently-toggleable feature modules, adding a read-only sleep headroom probe.

**Architecture:** A core (`CarRegistry`, `Evaluator`, `FeatureHost`, `Telemetry`) discovers rolling stock and computes per-car facts once per interval. Features implement `IFeature` and are offered each car in priority order; the first enabled feature to claim a car acts on it, which makes it structurally impossible for two features to mutate the same rigidbody. All decision logic that can be expressed in primitives lives in a Unity-free `Decisions` class so it can be unit tested.

**Tech Stack:** C# 7.3, Unity 2022.3.62f2, Unity Mod Manager 0.27.0, Roslyn `csc.exe` from VS 2019 Build Tools compiling against the Mono BCL that Railroader ships.

**Spec:** `docs/superpowers/specs/2026-08-13-highball-design.md`

## Global Constraints

- **No .NET SDK and no .NET Framework targeting packs are installed.** Never add a `dotnet build`, `msbuild`, `nuget`, or `dotnet test` step. Everything compiles through `csc.exe` at `C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe`.
- **Adding a `.cs` file requires adding it to the `$sources` array in `build.ps1`.** There is no globbing. A file not listed simply is not compiled.
- `-langversion:7.3`. No nullable reference types, no `record`, no target-typed `new`, no switch expressions.
- Railroader install: `D:\SteamLibrary\steamapps\common\Railroader`. Managed assemblies at `Railroader_Data\Managed`.
- Mod id, assembly name, namespace, and deploy folder are all exactly `Highball`.
- Telemetry CSV is `Highball.csv` in `Application.persistentDataPath`. Never write to `StockPhysicsLOD.csv`; that file holds session 1 and 2 data and must stay readable.
- **Restore-biased safety:** downgrades require sustained calm, restores are immediate and unconditional. Every feature hands back everything it touched on toggle-off, unload, and car removal. No physics change ever persists to the save.
- `Rigidbody.Sleep()` is **not** implemented in this plan. The `StationarySleepFeature` is gated behind the headroom probe's result and its own spec decision rule.

---

### Task 1: Pure decision core with a real test loop

Everything in this task is Unity-free by design, so it can be compiled and run as a console
program. This is the only code in the project with an automated test cycle; the rest is
Unity glue verified in-game.

**Files:**
- Create: `src/Highball/Decisions.cs`
- Create: `tools/HighballTests/Tests.cs`
- Create: `tools/HighballTests/build.ps1`

**Interfaces:**
- Consumes: nothing.
- Produces: `Highball.Decisions` static class with:
  - `static float AccumulateCalm(float current, float value, float threshold, float deltaTime)` — returns `current + deltaTime` when `value <= threshold`, otherwise `0f`.
  - `static bool QualifiesForSolverLod(float distance, float steadySeconds, float minDistance, float requiredSteadySeconds)`
  - `static bool QualifiesForSleep(float distance, float stationarySeconds, bool isAsleep, bool consistMoving, float minDistance, float requiredStationarySeconds)`
  - `static string ClassifyHeadroom(int stationaryAwake, int tracked)` — returns `"none"`, `"marginal"`, or `"real"`.

- [ ] **Step 1: Write the failing tests**

Create `tools/HighballTests/Tests.cs`:

```csharp
using System;
using Highball;

internal static class Tests
{
    private static int _failed;

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
        if (!condition) _failed++;
    }

    private static void CheckFloat(float actual, float expected, string name)
    {
        Check(Math.Abs(actual - expected) < 0.0001f, name + " (got " + actual + ", want " + expected + ")");
    }

    private static int Main()
    {
        // AccumulateCalm: a value at or under threshold accrues time.
        CheckFloat(Decisions.AccumulateCalm(1.0f, 0.2f, 0.5f, 0.25f), 1.25f, "calm accrues below threshold");
        CheckFloat(Decisions.AccumulateCalm(1.0f, 0.5f, 0.5f, 0.25f), 1.25f, "calm accrues at exactly threshold");

        // AccumulateCalm: any excursion above threshold resets the clock to zero.
        CheckFloat(Decisions.AccumulateCalm(9.0f, 0.51f, 0.5f, 0.25f), 0f, "jolt resets calm clock");

        // Solver LOD: needs BOTH distance and sustained calm.
        Check(Decisions.QualifiesForSolverLod(600f, 3f, 500f, 3f), "solver qualifies when far and steady");
        Check(!Decisions.QualifiesForSolverLod(400f, 9f, 500f, 3f), "solver rejects near cars however steady");
        Check(!Decisions.QualifiesForSolverLod(600f, 2.9f, 500f, 3f), "solver rejects not-yet-steady cars");
        Check(!Decisions.QualifiesForSolverLod(500f, 3f, 500f, 3f), "solver distance gate is strictly greater");

        // Sleep: same gates, plus never touch an already-asleep car, plus consist guard.
        Check(Decisions.QualifiesForSleep(600f, 5f, false, false, 500f, 5f), "sleep qualifies when far, parked, awake");
        Check(!Decisions.QualifiesForSleep(600f, 5f, true, false, 500f, 5f), "sleep skips already-asleep cars");
        Check(!Decisions.QualifiesForSleep(600f, 5f, false, true, 500f, 5f), "sleep refuses when consist is moving");
        Check(!Decisions.QualifiesForSleep(400f, 5f, false, false, 500f, 5f), "sleep rejects near cars");
        Check(!Decisions.QualifiesForSleep(600f, 4.9f, false, false, 500f, 5f), "sleep rejects not-yet-stationary cars");

        // Headroom classification against the spec's 10% / 30% thresholds.
        Check(Decisions.ClassifyHeadroom(51, 519) == "none", "9.8% classifies as none");
        Check(Decisions.ClassifyHeadroom(52, 519) == "marginal", "10.0% classifies as marginal");
        Check(Decisions.ClassifyHeadroom(155, 519) == "marginal", "29.9% classifies as marginal");
        Check(Decisions.ClassifyHeadroom(156, 519) == "real", "30.1% classifies as real");
        Check(Decisions.ClassifyHeadroom(0, 0) == "none", "zero tracked does not divide by zero");

        Console.WriteLine(_failed == 0 ? "ALL PASS" : _failed + " FAILED");
        return _failed == 0 ? 0 : 1;
    }
}
```

- [ ] **Step 2: Write the test build script**

Create `tools/HighballTests/build.ps1`. It compiles `Decisions.cs` together with the test
file into a console exe, referencing only the BCL — no Unity assemblies, which is what
keeps `Decisions.cs` honest.

```powershell
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
```

- [ ] **Step 3: Run the tests to verify they fail**

```powershell
.\tools\HighballTests\build.ps1
```

Expected: compilation fails — `src\Highball\Decisions.cs` does not exist yet.

- [ ] **Step 4: Write the minimal implementation**

Create `src/Highball/Decisions.cs`:

```csharp
namespace Highball
{
    /// <summary>
    /// Every decision the mod makes that can be expressed in primitives, with no Unity
    /// types. Isolated here so it can be compiled into a console test runner and given a
    /// real red-green loop; the game itself cannot be scripted.
    /// </summary>
    internal static class Decisions
    {
        /// <summary>
        /// Advances a calm clock. Any excursion above the threshold resets it to zero,
        /// so the clock measures *continuous* calm rather than total calm.
        /// </summary>
        internal static float AccumulateCalm(float current, float value, float threshold, float deltaTime)
        {
            return value <= threshold ? current + deltaTime : 0f;
        }

        internal static bool QualifiesForSolverLod(
            float distance, float steadySeconds, float minDistance, float requiredSteadySeconds)
        {
            return distance > minDistance && steadySeconds >= requiredSteadySeconds;
        }

        /// <summary>
        /// A car may be slept only when it is far away, has been genuinely at rest for
        /// long enough, is not already asleep, and belongs to no moving consist. The
        /// consist guard matters because a car mid-train can read near-zero speed during
        /// slack action while its train is moving.
        /// </summary>
        internal static bool QualifiesForSleep(
            float distance, float stationarySeconds, bool isAsleep, bool consistMoving,
            float minDistance, float requiredStationarySeconds)
        {
            if (isAsleep || consistMoving) return false;
            return distance > minDistance && stationarySeconds >= requiredStationarySeconds;
        }

        /// <summary>
        /// The spec's pre-agreed decision rule for whether forcing sleep is worth building.
        /// Agreed before any data was collected, deliberately.
        /// </summary>
        internal static string ClassifyHeadroom(int stationaryAwake, int tracked)
        {
            if (tracked <= 0) return "none";

            float share = (float)stationaryAwake / tracked;
            if (share < 0.10f) return "none";
            return share <= 0.30f ? "marginal" : "real";
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

```powershell
.\tools\HighballTests\build.ps1
```

Expected: every line `PASS`, final line `ALL PASS`, exit code 0.

- [ ] **Step 6: Commit**

```bash
git add src/Highball/Decisions.cs tools/HighballTests/
git commit -m "Add pure decision core with a console test loop"
```

---

### Task 2: Rename to Highball

Mechanical and behaviour-preserving. The mod must build, deploy, and behave exactly as
`StockPhysicsLOD` did when this task ends. Restructuring happens in Task 3, not here —
keeping the rename separate means a reviewer can verify "nothing changed but names".

**Files:**
- Create: `src/Highball/Main.cs`, `Settings.cs`, `LodManager.cs`, `Experiment.cs`, `Info.json`, `build.ps1`, `Properties/AssemblyInfo.cs` (moved from `src/StockPhysicsLOD/`)
- Delete: `src/StockPhysicsLOD/` entirely

**Interfaces:**
- Consumes: `Highball.Decisions` from Task 1.
- Produces: namespace `Highball`, entry point `Highball.Main.Load`.

- [ ] **Step 1: Move the source tree**

```bash
git mv src/StockPhysicsLOD src/Highball
```

- [ ] **Step 2: Rename the namespace in every source file**

In `src/Highball/Main.cs`, `Settings.cs`, `LodManager.cs`, `Experiment.cs`, and
`Properties/AssemblyInfo.cs`, replace `namespace StockPhysicsLOD` with `namespace Highball`.

In `Main.cs`, change the log prefix and the panel heading:

```csharp
public static void Log(string msg)
{
    ModEntry?.Logger.Log("[Highball] " + msg);
}
```

```csharp
GUILayout.Label("Highball");
```

In `Properties/AssemblyInfo.cs`, update `AssemblyTitle` and `AssemblyProduct` to `Highball`.

- [ ] **Step 3: Rewrite Info.json**

```json
{
  "Id": "Highball",
  "DisplayName": "Highball",
  "Author": "dibujaron",
  "Version": "0.2.0",
  "ManagerVersion": "0.27.0",
  "Requirements": [],
  "AssemblyName": "Highball.dll",
  "EntryMethod": "Highball.Main.Load"
}
```

- [ ] **Step 4: Update build.ps1**

In `src/Highball/build.ps1`, change the output DLL, the deploy folder, the progress
message, the header comment, and add `Decisions.cs` to `$sources`:

```powershell
$outDll = Join-Path $outDir "Highball.dll"
```

```powershell
$sources = @(
    (Join-Path $here "Main.cs"),
    (Join-Path $here "Settings.cs"),
    (Join-Path $here "Decisions.cs"),
    (Join-Path $here "LodManager.cs"),
    (Join-Path $here "Experiment.cs"),
    (Join-Path $here "Properties\AssemblyInfo.cs")
)
```

```powershell
Write-Host "Compiling Highball..."
```

```powershell
$dest = Join-Path $RailroaderDir "Mods\Highball"
```

- [ ] **Step 5: Change the telemetry filename**

In `src/Highball/Experiment.cs`, `Init()`:

```csharp
CsvPath = Path.Combine(Application.persistentDataPath, "Highball.csv");
```

- [ ] **Step 6: Build to verify the rename compiles**

```powershell
.\src\Highball\build.ps1
```

Expected: `Built ...\src\Highball\bin\Release\Highball.dll`, exit code 0.

- [ ] **Step 7: Remove the old deployed mod from the game install**

If both folders exist UMM loads two mods that fight over the same rigidbodies.

```powershell
Remove-Item -Recurse -Force "D:\SteamLibrary\steamapps\common\Railroader\Mods\StockPhysicsLOD" -ErrorAction SilentlyContinue
```

- [ ] **Step 8: Commit**

```bash
git add -A src/Highball
git commit -m "Rename StockPhysicsLOD to Highball"
```

---

### Task 3: Split the core into CarRegistry and Evaluator

`LodManager` currently does discovery, evaluation, and action in one class. Split the
first two out; action moves to features in Task 4.

**Files:**
- Create: `src/Highball/CarRegistry.cs`, `src/Highball/CarFacts.cs`, `src/Highball/Evaluator.cs`
- Modify: `src/Highball/LodManager.cs` (shrinks to the solver action only), `src/Highball/build.ps1`

**Interfaces:**
- Consumes: `Decisions.AccumulateCalm`.
- Produces:
  - `Highball.TrackedCar` — class with fields `Id` (string), `Car` (`Model.Car`), `Rigidbody` (`UnityEngine.Rigidbody`), `Facts` (`CarFacts`), `ClaimedBy` (string), and feature scratch fields `OriginalSolverIterations` (int), `IsDowngraded` (bool).
  - `Highball.CarFacts` — struct with `Distance`, `Speed`, `Acceleration`, `SteadySeconds`, `StationarySeconds` (all float) and `IsAsleep` (bool).
  - `Highball.CarRegistry` — `void Refresh()`, `IList<TrackedCar> Cars { get; }`, `int TrackedCount { get; }`.
  - `Highball.Evaluator` — `void Evaluate(IList<TrackedCar> cars, float deltaTime)`, `int MovingCount { get; }`.

- [ ] **Step 1: Create CarFacts.cs**

```csharp
namespace Highball
{
    /// <summary>
    /// Per-car facts computed once per evaluation pass and read by every feature.
    /// Computing these once rather than per-feature keeps the pass cost independent of
    /// how many features are enabled.
    /// </summary>
    internal struct CarFacts
    {
        internal float Distance;
        internal float Speed;
        internal float Acceleration;

        /// <summary>Continuous seconds under the acceleration threshold.</summary>
        internal float SteadySeconds;

        /// <summary>Continuous seconds under the speed threshold. Not the same as steady:
        /// a consist at constant speed is steady but emphatically not stationary.</summary>
        internal float StationarySeconds;

        internal bool IsAsleep;
    }
}
```

- [ ] **Step 2: Create CarRegistry.cs**

Move `RefreshCars`, `ReportOnce`, the reflection fields, the discovery diagnostics, and the
reaping loop out of `LodManager` verbatim. The child-rigidbody fallback and its counters
are load-bearing and must survive the move unchanged — on the reference save 0 of 519 cars
carry a rigidbody on the root, so a root-only lookup tracks nothing.

The reaping loop currently calls `Restore(state)` before dropping a car. `CarRegistry` has
no features, so it must instead notify. Give it a callback:

```csharp
internal Action<TrackedCar> OnCarRemoved;
```

and in the reaping loop, replace `Restore(state)` with:

```csharp
if (OnCarRemoved != null)
{
    OnCarRemoved(state);
}
```

`Main` wires this to `FeatureHost.ReleaseAll(car)` in Task 4.

- [ ] **Step 3: Create Evaluator.cs**

```csharp
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Walks the car table once per interval and fills in CarFacts. Knows nothing about
    /// what any feature intends to do with them.
    /// </summary>
    internal sealed class Evaluator
    {
        internal int MovingCount { get; private set; }

        internal void Evaluate(System.Collections.Generic.IList<TrackedCar> cars, float deltaTime)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            Vector3 eye = cam.transform.position;
            Settings s = Settings.Instance;
            int moving = 0;

            for (int i = 0; i < cars.Count; i++)
            {
                TrackedCar car = cars[i];
                if (car?.Rigidbody == null || car.Car == null)
                {
                    continue;
                }

                Rigidbody rb = car.Rigidbody;
                float speed = rb.velocity.magnitude;
                float accel = Mathf.Abs(speed - car.Facts.Speed) / Mathf.Max(deltaTime, 0.0001f);

                if (speed > s.MovingSpeedThreshold)
                {
                    moving++;
                }

                CarFacts facts;
                facts.Distance = Vector3.Distance(rb.position, eye);
                facts.Speed = speed;
                facts.Acceleration = accel;
                facts.SteadySeconds = Decisions.AccumulateCalm(
                    car.Facts.SteadySeconds, accel, s.SteadyAccelThreshold, deltaTime);
                facts.StationarySeconds = Decisions.AccumulateCalm(
                    car.Facts.StationarySeconds, speed, s.MovingSpeedThreshold, deltaTime);
                facts.IsAsleep = rb.IsSleeping();

                car.Facts = facts;
            }

            MovingCount = moving;
        }
    }
}
```

- [ ] **Step 4: Add the new files to build.ps1**

Add `CarFacts.cs`, `CarRegistry.cs`, and `Evaluator.cs` to `$sources` in
`src/Highball/build.ps1`.

- [ ] **Step 5: Build**

```powershell
.\src\Highball\build.ps1
```

Expected: exit code 0. Fix any compile errors from the extraction before continuing.

- [ ] **Step 6: Re-run the decision tests**

```powershell
.\tools\HighballTests\build.ps1
```

Expected: `ALL PASS`. This confirms the extraction did not change `Decisions.cs`.

- [ ] **Step 7: Commit**

```bash
git add -A src/Highball
git commit -m "Split discovery and evaluation out of LodManager"
```

---

### Task 4: Feature abstraction with claim arbitration

**Files:**
- Create: `src/Highball/IFeature.cs`, `src/Highball/FeatureHost.cs`, `src/Highball/SolverLodFeature.cs`
- Delete: `src/Highball/LodManager.cs`
- Modify: `src/Highball/Main.cs`, `src/Highball/build.ps1`

**Interfaces:**
- Consumes: `TrackedCar`, `CarFacts`, `Decisions.QualifiesForSolverLod`.
- Produces:
  - `Highball.IFeature` — `string Id { get; }`, `string DisplayName { get; }`, `bool IsExperimental { get; }`, `bool Enabled { get; }`, `bool Active { get; set; }`, `bool TryClaim(TrackedCar car)`, `void Release(TrackedCar car)`, `void ReleaseAll()`, `string[] TelemetryHeaders { get; }`, `string[] TelemetryValues { get; }`, `void DrawGui()`.
  - `Highball.FeatureHost` — `FeatureHost(IFeature[] featuresInPriorityOrder)`, `void Apply(IList<TrackedCar> cars)`, `void ReleaseAll(TrackedCar car)`, `void ReleaseAll()`, `IFeature[] Features { get; }`, `IFeature Find(string id)`.

- [ ] **Step 1: Create IFeature.cs**

```csharp
namespace Highball
{
    /// <summary>
    /// One optimization, independently toggleable. Features are offered each car in a
    /// fixed priority order and the first one to claim it acts on it, so two features can
    /// never mutate the same rigidbody.
    /// </summary>
    internal interface IFeature
    {
        string Id { get; }
        string DisplayName { get; }

        /// <summary>Shown in the panel. Experimental features ship off.</summary>
        bool IsExperimental { get; }

        /// <summary>The player's toggle, backed by Settings.</summary>
        bool Enabled { get; }

        /// <summary>
        /// Flipped by the A/B harness when this feature is the experiment target. A
        /// feature that is Enabled but not Active must claim nothing and release
        /// everything, so a BASELINE window is a true control.
        /// </summary>
        bool Active { get; set; }

        /// <summary>Returns true if this feature took the car and acted on it.</summary>
        bool TryClaim(TrackedCar car);

        /// <summary>Hands one car back, unconditionally.</summary>
        void Release(TrackedCar car);

        /// <summary>Hands every car back, unconditionally.</summary>
        void ReleaseAll();

        string[] TelemetryHeaders { get; }
        string[] TelemetryValues { get; }

        void DrawGui();
    }
}
```

- [ ] **Step 2: Create FeatureHost.cs**

```csharp
using System.Collections.Generic;

namespace Highball
{
    /// <summary>
    /// Offers each car to features in priority order. Sleeping dominates solver LOD —
    /// a sleeping body is skipped by the solver entirely, so lowering its iteration count
    /// would be meaningless — hence sleep sits earlier in the array.
    /// </summary>
    internal sealed class FeatureHost
    {
        private readonly IFeature[] _features;

        internal FeatureHost(IFeature[] featuresInPriorityOrder)
        {
            _features = featuresInPriorityOrder;
        }

        internal IFeature[] Features { get { return _features; } }

        internal IFeature Find(string id)
        {
            for (int i = 0; i < _features.Length; i++)
            {
                if (_features[i].Id == id) return _features[i];
            }

            return null;
        }

        internal void Apply(IList<TrackedCar> cars)
        {
            for (int c = 0; c < cars.Count; c++)
            {
                TrackedCar car = cars[c];
                if (car?.Rigidbody == null)
                {
                    continue;
                }

                string claimed = null;

                for (int f = 0; f < _features.Length; f++)
                {
                    IFeature feature = _features[f];

                    if (claimed != null || !feature.Enabled || !feature.Active)
                    {
                        // Either someone already owns this car, or this feature is off.
                        // Either way it must not be holding it.
                        feature.Release(car);
                        continue;
                    }

                    if (feature.TryClaim(car))
                    {
                        claimed = feature.Id;
                    }
                    else
                    {
                        feature.Release(car);
                    }
                }

                car.ClaimedBy = claimed;
            }
        }

        /// <summary>Every feature releases this one car. Used when a car leaves the world.</summary>
        internal void ReleaseAll(TrackedCar car)
        {
            for (int i = 0; i < _features.Length; i++)
            {
                _features[i].Release(car);
            }

            car.ClaimedBy = null;
        }

        /// <summary>
        /// Every feature releases everything, regardless of its enabled state — a feature
        /// switched off at runtime must still hand back what it was holding.
        /// </summary>
        internal void ReleaseAll()
        {
            for (int i = 0; i < _features.Length; i++)
            {
                _features[i].ReleaseAll();
            }
        }
    }
}
```

- [ ] **Step 3: Create SolverLodFeature.cs**

Carries over the substance of `LodManager.Downgrade`/`Restore`.

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Reduces PhysX solver iterations on distant rolling stock in steady-state motion.
    ///
    /// Experimental. Its only measurement so far put ACTIVE 4.6 fps slower than BASELINE
    /// across a single window per arm, well inside a measured +/-9 fps noise floor. That
    /// is not evidence of harm, but it is not evidence of benefit either, so this ships
    /// off until a run of four or more windows per arm says otherwise.
    /// </summary>
    internal sealed class SolverLodFeature : IFeature
    {
        private readonly List<TrackedCar> _held = new List<TrackedCar>();

        public string Id { get { return "solver_lod"; } }
        public string DisplayName { get { return "Solver iteration LOD"; } }
        public bool IsExperimental { get { return true; } }
        public bool Enabled { get { return Settings.Instance.EnableSolverLod; } }
        public bool Active { get; set; }

        public bool TryClaim(TrackedCar car)
        {
            Settings s = Settings.Instance;

            if (!Decisions.QualifiesForSolverLod(
                    car.Facts.Distance, car.Facts.SteadySeconds,
                    s.MinDistanceMeters, s.RequiredSteadySeconds))
            {
                return false;
            }

            if (car.IsDowngraded)
            {
                return true;
            }

            try
            {
                car.OriginalSolverIterations = car.Rigidbody.solverIterations;
                car.Rigidbody.solverIterations = s.LowSolverIterations;
                car.IsDowngraded = true;
                _held.Add(car);
            }
            catch
            {
                // A destroyed rigidbody is reaped on the next refresh.
                return false;
            }

            return true;
        }

        public void Release(TrackedCar car)
        {
            if (!car.IsDowngraded)
            {
                return;
            }

            try
            {
                car.Rigidbody.solverIterations = car.OriginalSolverIterations;
            }
            catch
            {
                // Same as above; nothing useful to do.
            }

            car.IsDowngraded = false;
            _held.Remove(car);
        }

        public void ReleaseAll()
        {
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                Release(_held[i]);
            }

            _held.Clear();
        }

        public string[] TelemetryHeaders { get { return new[] { "solver_downgraded" }; } }

        public string[] TelemetryValues
        {
            get { return new[] { _held.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }; }
        }

        public void DrawGui()
        {
            GUILayout.Label(string.Format("Low solver iterations: {0}", Settings.Instance.LowSolverIterations));
            Settings.Instance.LowSolverIterations = Mathf.RoundToInt(
                GUILayout.HorizontalSlider(Settings.Instance.LowSolverIterations, 1f, 6f));
        }
    }
}
```

- [ ] **Step 4: Delete LodManager.cs and rewire Main.cs**

```bash
git rm src/Highball/LodManager.cs
```

In `Main.cs`, replace the `_lod` field with a registry, evaluator and host, and wire the
removal callback so a car leaving the world is released by every feature first:

```csharp
private static CarRegistry _registry;
private static Evaluator _evaluator;
private static FeatureHost _host;
```

In `Load`, after settings are loaded:

```csharp
_registry = new CarRegistry();
_evaluator = new Evaluator();
_host = new FeatureHost(new IFeature[]
{
    // Priority order. Sleep, once it exists, goes ahead of solver LOD.
    new SolverLodFeature()
});

_registry.OnCarRemoved = car => _host.ReleaseAll(car);
```

Replace `_lod.Tick(deltaTime)` in `OnUpdate` with refresh/evaluate/apply on the existing
two timers, and replace every `_lod.Clear()` with `_host.ReleaseAll()`.

- [ ] **Step 5: Add the new files to build.ps1 and remove LodManager.cs from `$sources`**

- [ ] **Step 6: Build**

```powershell
.\src\Highball\build.ps1
```

Expected: exit code 0.

- [ ] **Step 7: Commit**

```bash
git add -A src/Highball
git commit -m "Add feature abstraction with claim arbitration"
```

---

### Task 5: Sleep headroom probe

**Files:**
- Create: `src/Highball/SleepHeadroomProbe.cs`
- Modify: `src/Highball/Main.cs`, `src/Highball/build.ps1`

**Interfaces:**
- Consumes: `IFeature`, `TrackedCar`, `Decisions.ClassifyHeadroom`.
- Produces: `Highball.SleepHeadroomProbe : IFeature` with `Id == "sleep_headroom"`.

- [ ] **Step 1: Create SleepHeadroomProbe.cs**

```csharp
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Read-only. Answers one question before any sleep code is written: how many cars are
    /// parked but still awake, and therefore actually addressable by forcing sleep?
    ///
    /// The premise is genuinely uncertain in both directions. PhysX auto-sleeps bodies
    /// whose mass-normalized kinetic energy stays under sleepThreshold, which would make
    /// forcing sleep redundant. But bodies in constant contact or bound by joints
    /// routinely fail to auto-sleep, and rolling stock sits on track colliders with bogies
    /// and coupler constraints, which is exactly that configuration.
    /// </summary>
    internal sealed class SleepHeadroomProbe : IFeature
    {
        private int _asleep;
        private int _stationary;
        private int _stationaryAwake;
        private int _tracked;

        public string Id { get { return "sleep_headroom"; } }
        public string DisplayName { get { return "Sleep headroom probe (read-only)"; } }
        public bool IsExperimental { get { return false; } }
        public bool Enabled { get { return Settings.Instance.EnableSleepHeadroomProbe; } }

        /// <summary>Always observes. A read-only probe has no baseline arm to control for.</summary>
        public bool Active { get { return true; } set { } }

        internal void Observe(IList<TrackedCar> cars)
        {
            int asleep = 0, stationary = 0, stationaryAwake = 0, tracked = 0;

            for (int i = 0; i < cars.Count; i++)
            {
                TrackedCar car = cars[i];
                if (car?.Rigidbody == null) continue;

                tracked++;
                bool isStationary = car.Facts.Speed <= Settings.Instance.MovingSpeedThreshold;

                if (car.Facts.IsAsleep) asleep++;
                if (isStationary) stationary++;
                if (isStationary && !car.Facts.IsAsleep) stationaryAwake++;
            }

            _asleep = asleep;
            _stationary = stationary;
            _stationaryAwake = stationaryAwake;
            _tracked = tracked;
        }

        /// <summary>Never claims. This feature mutates nothing.</summary>
        public bool TryClaim(TrackedCar car) { return false; }

        public void Release(TrackedCar car) { }

        public void ReleaseAll() { }

        public string[] TelemetryHeaders
        {
            get { return new[] { "asleep", "stationary", "stationary_awake" }; }
        }

        public string[] TelemetryValues
        {
            get
            {
                return new[]
                {
                    _asleep.ToString(CultureInfo.InvariantCulture),
                    _stationary.ToString(CultureInfo.InvariantCulture),
                    _stationaryAwake.ToString(CultureInfo.InvariantCulture)
                };
            }
        }

        public void DrawGui()
        {
            GUILayout.Label(string.Format(
                "asleep {0}   stationary {1}   stationary+awake {2}   verdict: {3}",
                _asleep, _stationary, _stationaryAwake,
                Decisions.ClassifyHeadroom(_stationaryAwake, _tracked)));
        }
    }
}
```

- [ ] **Step 2: Wire it into Main.cs**

Add it to the `FeatureHost` array **after** `SolverLodFeature` (it never claims, so its
position does not affect arbitration, but keeping mutating features first makes the
priority order readable), hold a typed reference so `Observe` can be called each pass, and
call `_probe.Observe(_registry.Cars)` immediately after `_evaluator.Evaluate(...)` and
before `_host.Apply(...)`.

- [ ] **Step 3: Add to build.ps1 `$sources` and build**

```powershell
.\src\Highball\build.ps1
```

Expected: exit code 0.

- [ ] **Step 4: Commit**

```bash
git add -A src/Highball
git commit -m "Add read-only sleep headroom probe"
```

---

### Task 6: Telemetry with per-feature columns and an experiment target

**Files:**
- Modify: `src/Highball/Experiment.cs` (rename to `src/Highball/Telemetry.cs`), `src/Highball/Main.cs`, `src/Highball/build.ps1`

**Interfaces:**
- Consumes: `FeatureHost`, `IFeature.TelemetryHeaders`, `IFeature.TelemetryValues`.
- Produces: `Highball.Telemetry` — `Telemetry(FeatureHost host, CarRegistry registry, Evaluator evaluator)`, `void Init()`, `void Tick(float)`, `void Shutdown()`, `void ForceActive(bool)`, `bool ActiveWindow { get; }`, `int RowsWritten { get; }`, `string CsvPath { get; }`.

- [ ] **Step 1: Rename the file and class**

```bash
git mv src/Highball/Experiment.cs src/Highball/Telemetry.cs
```

Rename the class `Experiment` to `Telemetry` and update `Main.cs` accordingly. Keep the
alternating window design and the 2 s `SettleSeconds` discard — both were right.

- [ ] **Step 2: Build the header from enabled features**

Replace the fixed header line in `Init()`:

```csharp
private string[] BaseHeaders()
{
    return new[] { "wall_clock", "mode", "window_s", "frames", "avg_frame_ms", "fps", "tracked", "moving" };
}

private string[] EnabledFeatures()
{
    var ids = new List<string>();
    IFeature[] features = _host.Features;
    for (int i = 0; i < features.Length; i++)
    {
        if (features[i].Enabled) ids.Add(features[i].Id);
    }

    return ids.ToArray();
}

private string[] FullHeader()
{
    var cells = new List<string>(BaseHeaders());
    IFeature[] features = _host.Features;
    for (int i = 0; i < features.Length; i++)
    {
        if (features[i].Enabled) cells.AddRange(features[i].TelemetryHeaders);
    }

    return cells.ToArray();
}
```

In `Init()`, always write a session banner naming the enabled set, then the header. Writing
the header once per session rather than once per file means a schema change mid-file is
still readable:

```csharp
_writer.WriteLine("# SESSION " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture)
                  + " features=" + string.Join("|", EnabledFeatures()));
_writer.WriteLine(string.Join(",", FullHeader()));
```

- [ ] **Step 3: Append feature values in FlushWindow**

Build the row from base values plus each enabled feature's `TelemetryValues`, walking
features in the same order `FullHeader()` did so columns stay aligned:

```csharp
private void FlushWindow()
{
    if (_frames > 0 && _windowElapsed > 0f)
    {
        double avgFrameMs = (_frameSeconds * 1000.0) / _frames;
        double fps = _frames / _windowElapsed;

        var cells = new List<string>
        {
            DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            _activeWindow ? "ACTIVE" : "BASELINE",
            _windowElapsed.ToString("F2", CultureInfo.InvariantCulture),
            _frames.ToString(CultureInfo.InvariantCulture),
            avgFrameMs.ToString("F3", CultureInfo.InvariantCulture),
            fps.ToString("F3", CultureInfo.InvariantCulture),
            _registry.TrackedCount.ToString(CultureInfo.InvariantCulture),
            _evaluator.MovingCount.ToString(CultureInfo.InvariantCulture)
        };

        IFeature[] features = _host.Features;
        for (int i = 0; i < features.Length; i++)
        {
            if (features[i].Enabled) cells.AddRange(features[i].TelemetryValues);
        }

        WriteRow(cells.ToArray());
    }

    _frames = 0;
    _frameSeconds = 0f;
    _windowElapsed = 0f;
}
```

`WriteRow` keeps its existing `string[]` signature and its write-failure shutdown path.

- [ ] **Step 4: Add ExperimentTarget handling**

`SwitchMode` currently flips everything. Flip only the targeted feature; every other
feature holds whatever its own toggle says:

```csharp
private void SwitchMode()
{
    _activeWindow = !_activeWindow;
    ApplyMode();
    _settleRemaining = SettleSeconds;
}

/// <summary>
/// Only the feature under test alternates. Flipping all of them at once would confound
/// the comparison, since a fps delta could not be attributed to any one of them.
/// </summary>
private void ApplyMode()
{
    IFeature[] features = _host.Features;
    string target = Settings.Instance.ExperimentTarget;

    for (int i = 0; i < features.Length; i++)
    {
        features[i].Active = features[i].Id == target ? _activeWindow : true;
    }
}
```

`ForceActive(bool)` sets `_activeWindow` then calls `ApplyMode()`.

- [ ] **Step 5: Build**

```powershell
.\src\Highball\build.ps1
```

Expected: exit code 0.

- [ ] **Step 6: Commit**

```bash
git add -A src/Highball
git commit -m "Add per-feature telemetry columns and an experiment target"
```

---

### Task 7: Per-feature settings and panel

**Files:**
- Modify: `src/Highball/Settings.cs`, `src/Highball/Main.cs`

**Interfaces:**
- Consumes: `FeatureHost.Features`.
- Produces: `Settings.EnableSolverLod` (bool, default `false`), `Settings.EnableSleepHeadroomProbe` (bool, default `true`), `Settings.ExperimentTarget` (string, default `"solver_lod"`), `Settings.RequiredStationarySeconds` (float, default `5f`), `Settings.SleepMinDistanceMeters` (float, default `500f`).

- [ ] **Step 1: Add the per-feature settings**

```csharp
// --- features ---

/// <summary>Experimental: its one measurement showed no benefit. Ships off.</summary>
public bool EnableSolverLod = false;

/// <summary>Read-only measurement. Safe to leave on.</summary>
public bool EnableSleepHeadroomProbe = true;

// --- reserved for StationarySleepFeature, gated on the headroom probe result ---
public float SleepMinDistanceMeters = 500f;
public float RequiredStationarySeconds = 5f;

// --- experiment ---

/// <summary>Feature id the A/B alternates. Others hold their own toggle state.</summary>
public string ExperimentTarget = "solver_lod";
```

- [ ] **Step 2: Replace DrawGui with a per-feature layout**

`Settings.DrawGui` keeps only the core settings — `MinDistanceMeters`,
`SteadyAccelThreshold`, `RequiredSteadySeconds`, `RunExperiment`, `ExperimentWindowSeconds`.
Feature settings move to each feature's own `DrawGui`.

In `Main.OnGUI`, draw one group per feature:

```csharp
GUILayout.Label("Highball");
GUILayout.Label(string.Format("Tracked: {0}   Moving: {1}",
    _registry.TrackedCount, _evaluator.MovingCount));

if (Settings.Instance.RunExperiment)
{
    GUILayout.Label(string.Format("Window: {0}   target: {1}   rows: {2}",
        _telemetry.ActiveWindow ? "ACTIVE" : "BASELINE",
        Settings.Instance.ExperimentTarget,
        _telemetry.RowsWritten));
}

GUILayout.Space(8f);
Settings.Instance.DrawGui();

IFeature[] features = _host.Features;
for (int i = 0; i < features.Length; i++)
{
    IFeature f = features[i];

    GUILayout.Space(10f);
    GUILayout.Label(f.DisplayName + (f.IsExperimental ? "   [experimental]" : ""));

    bool wanted = GUILayout.Toggle(f.Enabled, "Enabled");
    SetFeatureEnabled(f.Id, wanted);

    if (!f.Enabled)
    {
        continue;
    }

    f.DrawGui();
}
```

Add a small `SetFeatureEnabled(string id, bool value)` in `Main` that writes the matching
`Settings` field, and — critically — calls `f.ReleaseAll()` when a feature is switched
from on to off, so nothing stays held:

```csharp
private static void SetFeatureEnabled(string id, bool value)
{
    Settings s = Settings.Instance;
    bool before;

    if (id == "solver_lod") { before = s.EnableSolverLod; s.EnableSolverLod = value; }
    else if (id == "sleep_headroom") { before = s.EnableSleepHeadroomProbe; s.EnableSleepHeadroomProbe = value; }
    else { return; }

    if (before && !value)
    {
        IFeature f = _host.Find(id);
        if (f != null) f.ReleaseAll();
    }
}
```

- [ ] **Step 3: Build**

```powershell
.\src\Highball\build.ps1
```

Expected: exit code 0.

- [ ] **Step 4: Commit**

```bash
git add -A src/Highball
git commit -m "Add per-feature settings and panel groups"
```

---

### Task 8: README, deploy, and in-game verification

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Rewrite README.md**

The current file still says `# railroader-better-ae`, describes the project as a "Better
Auto Engineer" mod, and reports status "Pre-design". All three are stale, and it is the
public landing page. Rewrite it to cover: what Highball is and what the name means; the
feature roster with each feature's status (solver LOD experimental and off, headroom probe
read-only and on); the measurement-first approach and why (`docs/STATE.md` records two
hypotheses that failed); build instructions via `build.ps1`; and a short prior-art credit
naming `RailroaderStockOptimizer` by thebikwirm plainly, with no commentary on its
implementation.

- [ ] **Step 2: Build and deploy**

```powershell
.\src\Highball\build.ps1 -Deploy
```

Expected: `Deployed to D:\SteamLibrary\steamapps\common\Railroader\Mods\Highball`.

- [ ] **Step 3: Confirm the old mod folder is gone**

```powershell
Test-Path "D:\SteamLibrary\steamapps\common\Railroader\Mods\StockPhysicsLOD"
```

Expected: `False`. If `True`, delete it — two mods mutating the same rigidbodies makes
every measurement meaningless.

- [ ] **Step 4: Back up the save**

Only the solver LOD mutates anything and it ships off, but the precedent from
`WCR_2.20260811-preLOD.bak` applies.

```powershell
$saves = "$env:USERPROFILE\AppData\LocalLow\Giraffe Lab LLC\Railroader\Saves"
Copy-Item "$saves\WCR_2.shortsave" "$saves\WCR_2.20260813-preHighball.bak" -Force
```

- [ ] **Step 5: Launch and verify discovery**

Start Railroader, load `WCR_2.shortsave`, enable Highball in the UMM panel.

Expected in the UMM log:

```
[Highball] Loaded.
[Highball] Discovery: 519 culler records -> 519 tracked (rb on root: 0, rb in children: 519, no rigidbody: 0)
```

**Non-zero tracked with zero on-root is the specific evidence the discovery fix works.**
If tracked is 0, stop — nothing downstream means anything.

- [ ] **Step 6: Verify the probe reports**

In the panel, the headroom probe line should show non-zero counters and a verdict of
`none`, `marginal`, or `real`.

Let it run at least 4 minutes, then check `Highball.csv` in
`%USERPROFILE%\AppData\LocalLow\Giraffe Lab LLC\Railroader\`. Expected: a `# SESSION`
line naming `sleep_headroom`, a header ending in `asleep,stationary,stationary_awake`, and
four or more populated rows.

- [ ] **Step 7: Verify feature toggles release cleanly**

Enable solver LOD in the panel, wait for `solver_downgraded` to climb above zero, then
disable it. Expected: `solver_downgraded` returns to `0` and stays there.

- [ ] **Step 8: Commit and push**

```bash
git add README.md
git commit -m "Rewrite README for Highball"
git push
```

---

## Not in this plan

**`StationarySleepFeature`.** It is gated on the headroom probe clearing 10% per the spec's
decision rule. Building it now would prejudge the measurement, which is the mistake this
project has already made twice.

When it is built, two things must be settled first:

1. **The consist API is unverified.** `Model.Car` is known to expose `id`, `velocity`,
   `IsVisible` and `gameObject`; the consist relationship has not been located in the game
   assemblies. `Decisions.QualifiesForSleep` already takes `consistMoving` as a parameter,
   so the pure logic is ready — but the glue that supplies it is not. If no accessor
   exists, the fallback is a longer `RequiredStationarySeconds` window, which is weaker and
   must be documented in the README.
2. **The rollaway hazard.** A forced-asleep car on a grade will not roll away when it
   physically should, and Railroader simulates rollaway. Solver LOD has no equivalent
   hazard. This must be called out next to the toggle in the panel and in the README.
