# Rendering Features Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship three draw-call-reduction features — tree LOD, ground detail LOD, and car renderer LOD — with their settings exposed in Railroader's own in-game preferences window so they can be tuned without relaunching.

**Architecture:** Split `IFeature` into a lifecycle interface and an `ICarFeature` that adds per-car claim/release, so features that act on global state (terrains) sit alongside features that act on cars. All threshold and clamp arithmetic moves into the Unity-free `Decisions` class where the console test runner can pin it. A Harmony postfix on the game's own preferences builder adds a Highball tab.

**Tech Stack:** C# 7.3, Unity 2022.3.62f2, Unity Mod Manager 0.27.0, Harmony (`0Harmony.dll`, already referenced), Roslyn `csc.exe` from VS 2019 Build Tools against the Mono BCL Railroader ships.

**Specs:** `docs/superpowers/specs/2026-08-13-tree-lod-design.md`, `docs/superpowers/specs/2026-08-13-car-renderer-lod-design.md`

## Global Constraints

- **No .NET SDK and no .NET Framework targeting packs.** Never add `dotnet`, `msbuild`, `nuget`, or `dotnet test`. Everything compiles through `csc.exe` at `C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe`.
- **Every `.cs` file must be listed in `$sources` in `src/Highball/build.ps1`.** There is no globbing; an omitted file is silently not compiled.
- `-langversion:7.3`. No nullable reference types, no records, no target-typed `new`, no switch expressions.
- `src/Highball/Decisions.cs` must never reference `UnityEngine`. `tools/HighballTests/build.ps1` compiles it against BCL-only references, which is what enforces this.
- **Restore-biased safety.** Reductions require the car or terrain to be past a threshold; restores are immediate and unconditional. Everything is handed back on toggle-off, feature-disable, unload, car reaping, and A/B baseline windows. Nothing persists to the save.
- **Per-feature exception isolation.** Every fan-out over features wraps each feature's call in its own try/catch inside the loop, logging via `Main.Log`. This exists in `FeatureHost.ReleaseAll` (both overloads), `Telemetry.ApplyMode`, and `Main.ReleaseDisabledFeatures`. Any new fan-out must match.
- **Never assume you know an original value.** Capture it from the object before writing. This applies to `shadowCastingMode`, terrain settings, and solver iterations alike.
- Shell is PowerShell; Windows PowerShell 5.1 has no `&&` or `||`. Use `;` and `if ($?) { }`.

---

### Task 1: Interface split and the new decision functions

**Files:**
- Modify: `src/Highball/IFeature.cs`, `src/Highball/FeatureHost.cs`, `src/Highball/SolverLodFeature.cs`, `src/Highball/SleepHeadroomProbe.cs`, `src/Highball/Main.cs`, `src/Highball/Decisions.cs`, `src/Highball/build.ps1`
- Modify: `tools/HighballTests/Tests.cs`

**Interfaces:**
- Consumes: existing `IFeature`, `TrackedCar`, `FeatureHost`.
- Produces:
  - `IFeature` — `Id`, `DisplayName`, `IsExperimental`, `Enabled`, `Active {get;set;}`, `Tick(float deltaTime)`, `ReleaseAll()`, `TelemetryHeaders`, `TelemetryValues`.
  - `ICarFeature : IFeature` — adds `bool TryClaim(TrackedCar)`, `void Release(TrackedCar)`.
  - `FeatureHost.Tick(float deltaTime)` — calls `Tick` on features that are `Enabled && Active`; calls `ReleaseAll()` on those that are not.
  - `Decisions.ClampReduction(float configured, float original)` → `Math.Min`, so a feature can only reduce.
  - `Decisions.ClampReductionInt(int configured, int original)` → same for ints.
  - `Decisions.ShouldSuppressAtDistance(float distance, float threshold, float hysteresisMeters, bool currentlySuppressed)` → edge-triggered with a hysteresis band.

- [ ] **Step 1: Write the failing tests**

Append to `tools/HighballTests/Tests.cs`, inside `Main()` before the final summary:

```csharp
        // ClampReduction: a feature may only ever reduce work, never raise it.
        CheckFloat(Decisions.ClampReduction(60f, 200f), 60f, "clamp keeps the lower configured value");
        CheckFloat(Decisions.ClampReduction(300f, 120f), 120f, "clamp keeps the original when configured is higher");
        CheckFloat(Decisions.ClampReduction(80f, 80f), 80f, "clamp is a no-op when equal");
        Check(Decisions.ClampReductionInt(50, 200) == 50, "int clamp keeps the lower configured value");
        Check(Decisions.ClampReductionInt(500, 50) == 50, "int clamp keeps the original when configured is higher");

        // Hysteresis: suppress past the threshold, but do not restore until well inside it,
        // so a car hovering at the boundary cannot thrash thousands of renderer writes.
        Check(Decisions.ShouldSuppressAtDistance(310f, 300f, 50f, false), "suppresses past the threshold");
        Check(!Decisions.ShouldSuppressAtDistance(290f, 300f, 50f, false), "does not suppress inside the threshold");
        Check(Decisions.ShouldSuppressAtDistance(280f, 300f, 50f, true), "stays suppressed inside the band");
        Check(!Decisions.ShouldSuppressAtDistance(240f, 300f, 50f, true), "restores below the band");
        Check(Decisions.ShouldSuppressAtDistance(250f, 300f, 50f, true), "band edge stays suppressed");
        Check(!Decisions.ShouldSuppressAtDistance(300f, 300f, 50f, false), "threshold itself does not suppress");
```

- [ ] **Step 2: Run the tests to verify they fail**

```powershell
.\tools\HighballTests\build.ps1
```

Expected: compile failure — `ClampReduction`, `ClampReductionInt` and `ShouldSuppressAtDistance` do not exist.

- [ ] **Step 3: Add the decision functions**

Append to `src/Highball/Decisions.cs` inside the class:

```csharp
        /// <summary>
        /// A feature may only ever reduce work. Railroader sets its own values and we do
        /// not learn them until runtime, so a fixed configured value can easily be higher
        /// than the game's — which would turn an optimization into a de-optimization,
        /// silently, while the panel claimed the feature was on.
        /// </summary>
        internal static float ClampReduction(float configured, float original)
        {
            return configured < original ? configured : original;
        }

        internal static int ClampReductionInt(int configured, int original)
        {
            return configured < original ? configured : original;
        }

        /// <summary>
        /// Edge-triggered distance test with a hysteresis band. Suppression begins past
        /// the threshold and does not end until the object is well inside it. Without the
        /// band, an object hovering at the boundary would flip state every pass, and for
        /// the renderer feature that means thousands of component writes per second —
        /// a performance mod making things worse.
        /// </summary>
        internal static bool ShouldSuppressAtDistance(
            float distance, float threshold, float hysteresisMeters, bool currentlySuppressed)
        {
            if (currentlySuppressed)
            {
                return distance >= threshold - hysteresisMeters;
            }

            return distance > threshold;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

```powershell
.\tools\HighballTests\build.ps1
```

Expected: `ALL PASS`, exit 0, 28 assertions.

- [ ] **Step 5: Split the interface**

`src/Highball/IFeature.cs` keeps everything except `TryClaim` and `Release`, and gains:

```csharp
        /// <summary>
        /// Called once per evaluation pass on features that are Enabled and Active.
        /// Features that act on global state rather than individual cars do their work
        /// here. Car-shaped features can leave it empty.
        /// </summary>
        void Tick(float deltaTime);
```

Add `ICarFeature` in the same file:

```csharp
    /// <summary>
    /// A feature that acts on individual cars, and therefore participates in claim
    /// arbitration. The first enabled, active ICarFeature to claim a car acts on it.
    /// </summary>
    internal interface ICarFeature : IFeature
    {
        bool TryClaim(TrackedCar car);
        void Release(TrackedCar car);
    }
```

`SolverLodFeature` becomes `ICarFeature` with no behaviour change and an empty `Tick`.

- [ ] **Step 6: Update FeatureHost**

`Apply(IList<TrackedCar> cars)` must now skip features that are not `ICarFeature`:

```csharp
        internal void Apply(IList<TrackedCar> cars)
        {
            for (int c = 0; c < cars.Count; c++)
            {
                TrackedCar car = cars[c];
                if (car?.Rigidbody == null)
                {
                    continue;
                }

                bool claimed = false;

                for (int f = 0; f < _features.Length; f++)
                {
                    ICarFeature feature = _features[f] as ICarFeature;
                    if (feature == null)
                    {
                        continue;
                    }

                    if (claimed || !feature.Enabled || !feature.Active)
                    {
                        feature.Release(car);
                        continue;
                    }

                    if (feature.TryClaim(car))
                    {
                        claimed = true;
                    }
                    else
                    {
                        feature.Release(car);
                    }
                }
            }
        }
```

Add `Tick`, with the same per-feature isolation every other fan-out uses:

```csharp
        /// <summary>
        /// Drives features that act on their own schedule. A feature that is disabled or
        /// inactive is released here rather than merely skipped, so an A/B baseline window
        /// or a runtime toggle-off restores immediately rather than waiting for a tick.
        /// </summary>
        internal void Tick(float deltaTime)
        {
            for (int i = 0; i < _features.Length; i++)
            {
                IFeature f = _features[i];

                try
                {
                    if (f.Enabled && f.Active)
                    {
                        f.Tick(deltaTime);
                    }
                    else
                    {
                        f.ReleaseAll();
                    }
                }
                catch (Exception ex)
                {
                    Main.Log("Feature '" + f.Id + "' threw from Tick/ReleaseAll: " + ex);
                }
            }
        }
```

- [ ] **Step 7: Fold the probe's Observe into Tick**

`SleepHeadroomProbe` implements `IFeature` (not `ICarFeature`). Its `Observe(IList<TrackedCar>)` becomes the body of `Tick(float)`, reading the car list from a reference handed to it at construction. Delete the explicit `_probe.Observe(...)` call from `Main.OnUpdate` and delete `TryClaim`/`Release`/the always-true `Active` override — `Active` becomes a plain auto-property, since `FeatureHost.Tick` now handles enabled/active gating.

In `Main.OnUpdate`, the evaluate block becomes:

```csharp
                if (_evalTimer >= Settings.Instance.EvaluateIntervalSeconds)
                {
                    float dt = _evalTimer;
                    _evalTimer = 0f;
                    _evaluator.Evaluate(_registry.Cars, dt);
                    _host.Apply(_registry.Cars);
                    _host.Tick(dt);
                }
```

- [ ] **Step 8: Build and verify**

```powershell
.\src\Highball\build.ps1
```

Expected: exit 0, no warnings.

- [ ] **Step 9: Commit**

```bash
git add -A src/Highball tools/HighballTests
git commit -m "Split IFeature and ICarFeature, add reduction and hysteresis decisions"
```

---

### Task 2: TerrainLodFeature — trees and ground detail

**Files:**
- Create: `src/Highball/TerrainLodFeature.cs`
- Modify: `src/Highball/Settings.cs`, `src/Highball/Main.cs`, `src/Highball/build.ps1`

**Interfaces:**
- Consumes: `IFeature`, `Decisions.ClampReduction`, `Decisions.ClampReductionInt`.
- Produces: `Highball.TerrainLodFeature : IFeature`, `Id == "terrain_lod"`.

- [ ] **Step 1: Add settings**

```csharp
        [Draw("Tree & ground detail LOD  [experimental]", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "Draws distant trees as flat billboards and shortens ground-detail draw distance. "
                      + "Never changes density — the forest stays as thick as you set it.")]
        public bool EnableTerrainLod = false;

        [Draw("Tree billboard distance (m)", Type = DrawType.Slider, Min = 10, Max = 250,
              VisibleOn = "EnableTerrainLod|true",
              Tooltip = "Past this distance a tree is drawn as a batched billboard instead of a 3D mesh.")]
        public float TreeBillboardDistanceMeters = 60f;

        [Draw("Max full-detail trees", Type = DrawType.Slider, Min = 0, Max = 250, Precision = 0,
              VisibleOn = "EnableTerrainLod|true")]
        public int TreeMaxFullLodCount = 50;

        [Draw("Tree crossfade length (m)", Type = DrawType.Slider, Min = 0, Max = 100,
              VisibleOn = "EnableTerrainLod|true",
              Tooltip = "Softens the pop as a tree switches to a billboard.")]
        public float TreeCrossFadeLengthMeters = 20f;

        [Draw("Ground detail distance (m)", Type = DrawType.Slider, Min = 10, Max = 250,
              VisibleOn = "EnableTerrainLod|true",
              Tooltip = "Draw distance for grass and ground detail. Density is never changed.")]
        public float DetailObjectDistanceMeters = 80f;
```

- [ ] **Step 2: Create TerrainLodFeature.cs**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Highball
{
    /// <summary>
    /// Shortens the distance at which trees become batched billboards, caps how many trees
    /// render at full 3D LOD, and shortens ground-detail draw distance.
    ///
    /// Density is never touched. Neither is treeDistance, which is the cull distance —
    /// lowering that makes trees vanish rather than simplify, which is the outcome this
    /// feature exists to avoid.
    ///
    /// Unity draws 3D terrain trees individually but batches billboards into one mesh, so
    /// this is primarily a draw-call reduction, which is a CPU saving.
    /// </summary>
    internal sealed class TerrainLodFeature : IFeature
    {
        private sealed class TerrainState
        {
            public Terrain Terrain;

            public float OriginalBillboardDistance;
            public int OriginalMaxFullLod;
            public float OriginalCrossFade;
            public float OriginalDetailDistance;

            // What this feature last wrote. If the terrain no longer matches, something
            // outside changed it and the originals must be re-captured rather than
            // clobbering the player's new choice.
            public float WroteBillboardDistance;
            public int WroteMaxFullLod;
            public float WroteCrossFade;
            public float WroteDetailDistance;

            public bool Applied;
        }

        private readonly List<TerrainState> _terrains = new List<TerrainState>();
        private float _timer;
        private bool _reported;

        internal int TerrainCount { get { return _terrains.Count; } }

        public string Id { get { return "terrain_lod"; } }
        public string DisplayName { get { return "Tree & ground detail LOD"; } }
        public bool IsExperimental { get { return true; } }
        public bool Enabled { get { return Settings.Instance.EnableTerrainLod; } }
        public bool Active { get; set; }

        public void Tick(float deltaTime)
        {
            _timer += deltaTime;
            if (_timer < 2f && _terrains.Count > 0)
            {
                return;
            }

            _timer = 0f;
            Apply();
        }

        private void Apply()
        {
            Terrain[] active = Terrain.activeTerrains;

            // Drop terrains that have gone away.
            for (int i = _terrains.Count - 1; i >= 0; i--)
            {
                if (_terrains[i].Terrain == null)
                {
                    _terrains.RemoveAt(i);
                }
            }

            for (int i = 0; i < active.Length; i++)
            {
                Terrain t = active[i];
                if (t == null)
                {
                    continue;
                }

                TerrainState s = Find(t);
                if (s == null)
                {
                    s = Capture(t);
                    _terrains.Add(s);
                }
                else if (s.Applied)
                {
                    // Something outside changed these — the game re-applies its own values
                    // when graphics settings change. Yield to it rather than fight.
                    if (t.treeBillboardDistance != s.WroteBillboardDistance
                        || t.treeMaximumFullLODCount != s.WroteMaxFullLod
                        || t.detailObjectDistance != s.WroteDetailDistance)
                    {
                        Recapture(s, t);
                    }
                }

                Settings cfg = Settings.Instance;

                s.WroteBillboardDistance = Decisions.ClampReduction(
                    cfg.TreeBillboardDistanceMeters, s.OriginalBillboardDistance);
                s.WroteMaxFullLod = Decisions.ClampReductionInt(
                    cfg.TreeMaxFullLodCount, s.OriginalMaxFullLod);
                s.WroteDetailDistance = Decisions.ClampReduction(
                    cfg.DetailObjectDistanceMeters, s.OriginalDetailDistance);
                s.WroteCrossFade = cfg.TreeCrossFadeLengthMeters;

                try
                {
                    t.treeBillboardDistance = s.WroteBillboardDistance;
                    t.treeMaximumFullLODCount = s.WroteMaxFullLod;
                    t.treeCrossFadeLength = s.WroteCrossFade;
                    t.detailObjectDistance = s.WroteDetailDistance;
                    s.Applied = true;
                }
                catch (Exception ex)
                {
                    Main.Log("TerrainLod: write failed: " + ex.Message);
                }
            }

            if (!_reported && _terrains.Count > 0)
            {
                _reported = true;
                TerrainState f = _terrains[0];
                Main.Log(string.Format(CultureInfo.InvariantCulture,
                    "TerrainLod: {0} terrain(s). Game defaults: billboard={1} maxFullLOD={2} crossfade={3} detail={4}",
                    _terrains.Count, f.OriginalBillboardDistance, f.OriginalMaxFullLod,
                    f.OriginalCrossFade, f.OriginalDetailDistance));
            }
        }

        private TerrainState Find(Terrain t)
        {
            for (int i = 0; i < _terrains.Count; i++)
            {
                if (ReferenceEquals(_terrains[i].Terrain, t)) return _terrains[i];
            }

            return null;
        }

        private static TerrainState Capture(Terrain t)
        {
            var s = new TerrainState { Terrain = t };
            Recapture(s, t);
            return s;
        }

        private static void Recapture(TerrainState s, Terrain t)
        {
            s.OriginalBillboardDistance = t.treeBillboardDistance;
            s.OriginalMaxFullLod = t.treeMaximumFullLODCount;
            s.OriginalCrossFade = t.treeCrossFadeLength;
            s.OriginalDetailDistance = t.detailObjectDistance;
            s.Applied = false;
        }

        public void ReleaseAll()
        {
            for (int i = 0; i < _terrains.Count; i++)
            {
                TerrainState s = _terrains[i];
                if (s.Terrain == null || !s.Applied)
                {
                    continue;
                }

                try
                {
                    s.Terrain.treeBillboardDistance = s.OriginalBillboardDistance;
                    s.Terrain.treeMaximumFullLODCount = s.OriginalMaxFullLod;
                    s.Terrain.treeCrossFadeLength = s.OriginalCrossFade;
                    s.Terrain.detailObjectDistance = s.OriginalDetailDistance;
                }
                catch
                {
                    // Destroyed terrain; nothing to restore to.
                }

                s.Applied = false;
            }
        }

        public string[] TelemetryHeaders
        {
            get { return new[] { "terrains", "tree_billboard_distance", "detail_object_distance" }; }
        }

        public string[] TelemetryValues
        {
            get
            {
                float bb = _terrains.Count > 0 ? _terrains[0].WroteBillboardDistance : 0f;
                float dd = _terrains.Count > 0 ? _terrains[0].WroteDetailDistance : 0f;
                return new[]
                {
                    _terrains.Count.ToString(CultureInfo.InvariantCulture),
                    bb.ToString("F1", CultureInfo.InvariantCulture),
                    dd.ToString("F1", CultureInfo.InvariantCulture)
                };
            }
        }
    }
}
```

- [ ] **Step 3: Register it, add to build.ps1, build**

Add `new TerrainLodFeature()` to the `FeatureHost` array in `Main.Load`, add `TerrainLodFeature.cs` to `$sources`, then:

```powershell
.\src\Highball\build.ps1
```

Expected: exit 0, no warnings.

- [ ] **Step 4: Commit**

```bash
git add -A src/Highball
git commit -m "Add terrain LOD feature for trees and ground detail"
```

---

### Task 3: CarRendererFeature

**Files:**
- Create: `src/Highball/CarRendererFeature.cs`
- Modify: `src/Highball/TrackedCar.cs`, `src/Highball/Settings.cs`, `src/Highball/Main.cs`, `src/Highball/build.ps1`

**Interfaces:**
- Consumes: `ICarFeature`, `TrackedCar`, `Decisions.ShouldSuppressAtDistance`.
- Produces: `Highball.CarRendererFeature : ICarFeature`, `Id == "car_renderer_lod"`.

- [ ] **Step 1: Add settings**

```csharp
        [Draw("Car renderer LOD  [experimental]", Type = DrawType.Toggle, Box = true, Collapsible = true,
              Tooltip = "Stops distant rolling stock from casting shadows. Cars never disappear or change shape.")]
        public bool EnableCarRendererLod = false;

        [Draw("Car shadow distance (m)", Type = DrawType.Slider, Min = 50, Max = 2000,
              VisibleOn = "EnableCarRendererLod|true",
              Tooltip = "Past this distance a car stops casting shadows. Its own shadow is a few pixels there.")]
        public float CarShadowDistanceMeters = 300f;
```

- [ ] **Step 2: Add per-car scratch to TrackedCar**

```csharp
        // --- scratch fields private to CarRendererFeature. A second feature must NOT
        // reuse these; keep your own state keyed by car. ---
        public Renderer[] Renderers;
        public UnityEngine.Rendering.ShadowCastingMode[] OriginalShadowModes;
        public bool ShadowsSuppressed;
```

- [ ] **Step 3: Create CarRendererFeature.cs**

```csharp
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;

namespace Highball
{
    /// <summary>
    /// Stops distant rolling stock from casting shadows. A shadow-casting renderer is
    /// drawn again per shadow-casting light, so suppressing it removes draw calls; a
    /// distant car's own shadow is a few pixels and its loss is not visible in practice.
    ///
    /// Never disables a renderer. That would make cars vanish, which is the mistake this
    /// project already refused to make with tree cull distance.
    ///
    /// Edge-triggered: renderer arrays are gathered lazily on first suppression and writes
    /// happen only on a threshold transition, with a hysteresis band. Walking 519 cars'
    /// worth of renderers every pass would cost more than it saves.
    /// </summary>
    internal sealed class CarRendererFeature : ICarFeature
    {
        private const float HysteresisMeters = 50f;

        private int _suppressed;
        private int _renderersTouched;

        public string Id { get { return "car_renderer_lod"; } }
        public string DisplayName { get { return "Car renderer LOD"; } }
        public bool IsExperimental { get { return true; } }
        public bool Enabled { get { return Settings.Instance.EnableCarRendererLod; } }
        public bool Active { get; set; }

        public void Tick(float deltaTime)
        {
            // Per-car work happens in TryClaim; nothing global to do.
        }

        public bool TryClaim(TrackedCar car)
        {
            bool want = Decisions.ShouldSuppressAtDistance(
                car.Facts.Distance,
                Settings.Instance.CarShadowDistanceMeters,
                HysteresisMeters,
                car.ShadowsSuppressed);

            if (!want)
            {
                Release(car);
                return false;
            }

            if (car.ShadowsSuppressed)
            {
                // Already in the desired state; claim without touching anything.
                return true;
            }

            if (!Gather(car))
            {
                return false;
            }

            for (int i = 0; i < car.Renderers.Length; i++)
            {
                Renderer r = car.Renderers[i];
                if (r == null)
                {
                    continue;
                }

                try
                {
                    car.OriginalShadowModes[i] = r.shadowCastingMode;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    _renderersTouched++;
                }
                catch
                {
                    // Destroyed mid-write; the array is re-gathered next transition.
                }
            }

            car.ShadowsSuppressed = true;
            _suppressed++;
            return true;
        }

        public void Release(TrackedCar car)
        {
            if (!car.ShadowsSuppressed)
            {
                return;
            }

            if (car.Renderers != null && car.OriginalShadowModes != null)
            {
                for (int i = 0; i < car.Renderers.Length; i++)
                {
                    Renderer r = car.Renderers[i];
                    if (r == null)
                    {
                        continue;
                    }

                    try
                    {
                        r.shadowCastingMode = car.OriginalShadowModes[i];
                        _renderersTouched++;
                    }
                    catch
                    {
                    }
                }
            }

            car.ShadowsSuppressed = false;
            if (_suppressed > 0) _suppressed--;
        }

        /// <summary>
        /// Gathers a car's renderers on first need. Re-gathers if any cached entry has
        /// died, since Railroader can add or remove child objects after we cached them.
        /// </summary>
        private static bool Gather(TrackedCar car)
        {
            bool stale = car.Renderers == null;

            if (!stale)
            {
                for (int i = 0; i < car.Renderers.Length; i++)
                {
                    if (car.Renderers[i] == null) { stale = true; break; }
                }
            }

            if (!stale)
            {
                return true;
            }

            GameObject go = car.Car != null ? car.Car.gameObject : null;
            if (go == null)
            {
                return false;
            }

            car.Renderers = go.GetComponentsInChildren<Renderer>(true);
            car.OriginalShadowModes = new ShadowCastingMode[car.Renderers.Length];
            return car.Renderers.Length > 0;
        }

        public void ReleaseAll()
        {
            // Per-car release is driven by FeatureHost.Apply; nothing held globally.
            _suppressed = 0;
        }

        public string[] TelemetryHeaders
        {
            get { return new[] { "cars_shadows_off", "renderers_touched" }; }
        }

        public string[] TelemetryValues
        {
            get
            {
                return new[]
                {
                    _suppressed.ToString(CultureInfo.InvariantCulture),
                    _renderersTouched.ToString(CultureInfo.InvariantCulture)
                };
            }
        }
    }
}
```

- [ ] **Step 4: Register, build, commit**

Add `new CarRendererFeature()` to the `FeatureHost` array **before** `SolverLodFeature`, add the file to `$sources`, build, then:

```bash
git add -A src/Highball
git commit -m "Add car renderer LOD feature"
```

**Known gap to fix during review:** `ReleaseAll()` cannot restore per-car state because it has no car list. `FeatureHost.Apply` releases each car whenever the feature is disabled or inactive, which covers the normal paths — but a car reaped while suppressed goes through `FeatureHost.ReleaseAll(TrackedCar)`, which does reach `Release(car)`. Confirm both paths during review and document the reasoning.

---

### Task 4: Telemetry always on, A/B optional

**Files:**
- Modify: `src/Highball/Settings.cs`, `src/Highball/Telemetry.cs`, `src/Highball/Main.cs`

- [ ] **Step 1: Default the experiment off**

`RunExperiment` becomes `false` by default, with its label updated to make clear it is a measurement mode, not the normal mode.

- [ ] **Step 2: Log rows regardless**

`Main.OnUpdate` currently calls `_telemetry.Tick(deltaTime)` only when `RunExperiment` is true, so with the experiment off nothing is recorded at all. Call it unconditionally instead.

In `Telemetry`, when `RunExperiment` is false: do not alternate, do not settle, and stamp the `mode` column `LIVE` instead of `ACTIVE`/`BASELINE`. Every feature stays at whatever its own toggle says. Window length still governs how often a row is written.

- [ ] **Step 3: Build and commit**

```bash
git add -A src/Highball
git commit -m "Record telemetry continuously; make the A/B an optional mode"
```

---

### Task 5: In-game settings tab

**Files:**
- Create: `src/Highball/GamePreferencesPatch.cs`
- Modify: `src/Highball/Main.cs`, `src/Highball/build.ps1`

**Verified API** (by reflection over the shipped assemblies — build against these):
- `UI.PreferencesWindow.PreferencesBuilder.BuildTabs(UITabbedPanelBuilder)` — instance method that builds every tab.
- `UI.Builder.UITabbedPanelBuilder.AddTab(string title, string tabId, Action<UIPanelBuilder> closure)`
- `UI.Builder.UIPanelBuilder.AddSection(string title, Action<UIPanelBuilder> closure, float spacing)`
- `UI.Builder.UIPanelBuilder.AddFieldToggle(string label, Func<bool> valueClosure, Action<bool> action, bool interactable)`
- `UI.Builder.UIPanelBuilder.AddSlider(Func<float> valueClosure, Func<string> textValueClosure, Action<float> valueChangedAction, float minValue, float maxValue, bool wholeNumbers, Action<float> editingEndedAction)`
- `UI.Builder.UIPanelBuilder.AddLabel(string text)`

- [ ] **Step 1: Create the Harmony patch**

A postfix on `BuildTabs` that appends a Highball tab. Everything is wrapped so a failure degrades to "no in-game tab" and never breaks the game's own preferences window.

```csharp
using System;
using HarmonyLib;
using UI.Builder;

namespace Highball
{
    /// <summary>
    /// Adds a Highball tab to Railroader's own preferences window, so the features can be
    /// tuned in-game without relaunching. The UMM panel remains as a fallback.
    ///
    /// Everything here is defensive: if the game's UI changes shape, the patch must fail
    /// to a missing tab, never to a broken preferences window.
    /// </summary>
    [HarmonyPatch]
    internal static class GamePreferencesPatch
    {
        internal static void Apply(Harmony harmony)
        {
            try
            {
                Type builder = AccessTools.TypeByName("UI.PreferencesWindow.PreferencesBuilder");
                if (builder == null)
                {
                    Main.Log("In-game settings tab unavailable: PreferencesBuilder not found.");
                    return;
                }

                var target = AccessTools.Method(builder, "BuildTabs");
                if (target == null)
                {
                    Main.Log("In-game settings tab unavailable: BuildTabs not found.");
                    return;
                }

                harmony.Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(GamePreferencesPatch), nameof(BuildTabsPostfix))));

                Main.Log("In-game settings tab installed.");
            }
            catch (Exception ex)
            {
                Main.Log("In-game settings tab unavailable: " + ex.Message);
            }
        }

        private static void BuildTabsPostfix(UITabbedPanelBuilder builder)
        {
            try
            {
                builder.AddTab("Highball", "highball", BuildHighballTab);
            }
            catch (Exception ex)
            {
                Main.Log("Highball tab failed to build: " + ex.Message);
            }
        }

        private static void BuildHighballTab(UIPanelBuilder panel)
        {
            Settings s = Settings.Instance;

            panel.AddSection("Trees & ground detail", b =>
            {
                b.AddFieldToggle("Enable", () => s.EnableTerrainLod,
                    v => { s.EnableTerrainLod = v; s.OnChange(); }, true);

                b.AddSlider(() => s.TreeBillboardDistanceMeters,
                    () => s.TreeBillboardDistanceMeters.ToString("F0") + " m",
                    v => { s.TreeBillboardDistanceMeters = v; s.OnChange(); },
                    10f, 250f, false, null);

                b.AddSlider(() => s.DetailObjectDistanceMeters,
                    () => s.DetailObjectDistanceMeters.ToString("F0") + " m",
                    v => { s.DetailObjectDistanceMeters = v; s.OnChange(); },
                    10f, 250f, false, null);
            }, 8f);

            panel.AddSection("Rolling stock", b =>
            {
                b.AddFieldToggle("Car renderer LOD", () => s.EnableCarRendererLod,
                    v => { s.EnableCarRendererLod = v; s.OnChange(); }, true);

                b.AddSlider(() => s.CarShadowDistanceMeters,
                    () => s.CarShadowDistanceMeters.ToString("F0") + " m",
                    v => { s.CarShadowDistanceMeters = v; s.OnChange(); },
                    50f, 2000f, false, null);

                b.AddFieldToggle("Solver iteration LOD (no measured benefit)",
                    () => s.EnableSolverLod,
                    v => { s.EnableSolverLod = v; s.OnChange(); }, true);
            }, 8f);

            panel.AddSection("Diagnostics", b =>
            {
                b.AddFieldToggle("Sleep headroom probe (read-only)",
                    () => s.EnableSleepHeadroomProbe,
                    v => { s.EnableSleepHeadroomProbe = v; s.OnChange(); }, true);

                b.AddFieldToggle("Run A/B experiment", () => s.RunExperiment,
                    v => { s.RunExperiment = v; s.OnChange(); }, true);
            }, 8f);
        }
    }
}
```

Call `GamePreferencesPatch.Apply(harmony)` from `Main.Load`, creating a `Harmony` instance with id `"highball"`. Unpatch it in `OnUnload`.

- [ ] **Step 2: Build**

```powershell
.\src\Highball\build.ps1
```

If `AddSlider`'s trailing `Action<float>` will not accept `null`, pass an empty lambda. If any signature differs from the list above, adapt to what compiles — do not fall back to hand-rolled UI.

- [ ] **Step 3: Commit**

```bash
git add -A src/Highball
git commit -m "Add a Highball tab to the game's own preferences window"
```

---

### Task 6: Cleanups, README, deploy

**Files:**
- Modify: `src/Highball/Settings.cs`, `src/Highball/Main.cs`, `README.md`

- [ ] **Step 1: Remove the dead sleep settings**

`SleepMinDistanceMeters` and `RequiredStationarySeconds` are dead — session 3 measured the addressable population at 0.31% against a 10% threshold and `StationarySleepFeature` will not be built. Delete both and any references.

- [ ] **Step 2: Drop the doubled log prefix**

`Main.Log` prepends `[Highball] ` and UMM prepends its own, so every line reads `[Highball] [Highball] …`. Log the message alone.

- [ ] **Step 3: Make ExperimentTarget a dropdown**

It currently wants a technical id (`solver_lod`) typed by hand, which the panel gives no way to discover. Change it to `DrawType.PopupList` over the registered feature ids if that works with a `string` field; if not, keep it a field but extend the tooltip to list the valid ids verbatim.

- [ ] **Step 4: Rewrite the README feature roster**

Cover all four features and their status, the session 3 results (physics dead, three hypotheses down), the in-game tab, and the one-file-per-session CSV. Keep the prior-art credit exactly as it is — a single neutral sentence, no comparison.

- [ ] **Step 5: Build, test, deploy**

```powershell
.\src\Highball\build.ps1
.\tools\HighballTests\build.ps1
.\src\Highball\build.ps1 -Deploy
```

- [ ] **Step 6: Commit and push**

```bash
git add -A
git commit -m "Remove dead sleep settings, fix log prefix, refresh README"
git push origin master
```

---

## Not in this plan

**In-game verification.** Every feature here is unverified until the owner runs it. The three things that must be checked first, in order:

1. `TerrainLod: N terrain(s)` with N non-zero. Zero means Railroader does not use Unity terrain trees and the whole terrain feature is inert.
2. The Highball tab appears in the game's preferences window. If the Harmony patch failed, the log says so and the UMM panel still works.
3. `renderers_touched` plateaus when the camera is still. If it climbs continuously, the hysteresis band is wrong and the car feature is thrashing.
