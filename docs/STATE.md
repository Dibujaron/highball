# Project state — 2026-08-14

Resume point for the Railroader performance investigation. Started at the end of session 1
and updated each session since.

## The problem

Railroader runs at a median ~50 fps with dips to 27–35 and frame spikes to 59 ms, on
hardware that should be nowhere near its limits. The player runs **12+ concurrent AI
consists**, which is load-bearing for how they play and is not negotiable.

## Environment

| | |
|---|---|
| CPU | Ryzen 9 9900X (12C/24T, 2 CCDs) |
| GPU | RTX 4070 Ti SUPER (16 GB — WMI misreports 4 GB) |
| RAM | 96 GB |
| Displays | 2× 1920×1080 @ 60 Hz |
| Drives | C: and D: both NVMe SSD |
| Game | Railroader 2025.1.0b, Unity 2022.3.62f2, DX11 |
| Install | `D:\SteamLibrary\steamapps\common\Railroader` |
| Save | `WCR_2.shortsave`, ~947 KB, **519 cars** |

Graphics prefs (registry `HKCU\Software\Giraffe Lab LLC\Railroader`):
quality index **1** (low), tree density **1.0 (max)**, detail density 0.5, vsync `-1`
(uncapped). GPU is substantially idle; this is a CPU-side problem.

Mods: ~22 via Unity Mod Manager (long-standing), ~23 via Railloader (installed 2026-08-08,
from two Nexus scenery packs — Eastern and Western right-of-way — which unpacked into many
folders). Perf predates the Railloader install.

## Ruled out, with evidence

### Auto Engineer planning — dead, measured

Built a throwaway probe (`tools/AEProbe`, since removed from the Mods folder) that timed
`Model.AI.AutoEngineerPlanner.UpdateTargets` and the `TryFindDistance` pathfinding nested
inside it, over 114 windows.

| Metric | Median | Max |
|---|---|---|
| `UpdateTargets` share of wall time | **0.23%** | 0.58% |
| `TryFindDistance` share | 0.04% | 0.34% |
| `UpdateTargets` calls/sec **per consist** | **~1.24** | — |

Against a pre-agreed threshold of 2%, this is dead. AE plans at roughly **1 Hz per
consist, not every tick** — about 50× cheaper than both of us assumed.

Correlation between active planner count and fps: **−0.059** (none). Caveat: the session
only spanned 10–13 consists, so this shows the *marginal* consist is cheap; it does not
prove 0→12 is free. That would need a stop-all-AI test.

**Consequence:** a "throttle the AE" mod is pointless. But the *correctness* complaint —
AE doesn't re-plan when it should — stands on its own, and is now known to be cheap to
fix. Planning costs 0.23%, so it can afford to run *more* often and smarter.

### Rigidbody discovery: the body is not on the car's root

A physics-LOD mod can only act on cars whose `Rigidbody` it can find, so we measured where
the body actually lives. Our diagnostic reports **0 of 519 cars** with a rigidbody on the
root GameObject; all 519 have one in a child.

The practical consequence for anything built here: a root-only lookup

```csharp
Rigidbody rb = go.GetComponent<Rigidbody>();
```

resolves to null for every car in the save, and any action gated behind a non-null
rigidbody then silently never runs. Discovery must fall back to
`GetComponentInChildren<Rigidbody>(true)`, and must report which path succeeded so a
zero-tracked result is visible rather than silent.

## Current work: `src/Highball`

Two rendering-CPU features, both experimental and both shipping off: **car renderer LOD**
(stops distant rolling stock casting shadows) and **tree & ground detail LOD** (billboards
distant trees, shortens ground-detail draw distance, never touches density). Neither has
been measured in-game yet.

Safety model is restore-biased: restores are immediate and unconditional, and everything
is handed back on toggle-off and unload. Every change is runtime-only and never persists
to the save.

Telemetry is a plain CSV, **off by default**, logging to
`%LOCALLOW%\Giraffe Lab LLC\Railroader\Highball-<timestamp>.csv`. The alternating
BASELINE/ACTIVE A/B harness that produced the session-3 result below was removed on
2026-08-14, once all three hypotheses it existed to test had been answered — comparing a
feature against itself is now two recordings, one with the toggle off and one with it on.

### Results — DEAD, measured 2026-08-13

Session 3 (2026-08-13 20:44–20:50) is the proper run: 5 windows per arm, 30 s each,
`Highball-20260813-204232284-2.csv`.

| Arm | Windows | Mean fps | Range | Mean moving |
|---|---|---|---|---|
| BASELINE | 5 | **40.38** | 36.0–43.5 | 66.0 |
| ACTIVE | 5 | **40.20** | 33.0–52.8 | 69.4 |

**0.18 fps apart, in the wrong direction.** The within-arm spread on ACTIVE alone is about
20 fps — roughly a hundred times the between-arm difference. Workloads were comparable.

The mechanism is definitely working: 413–479 cars downgraded in every ACTIVE window and 0
in every BASELINE window. It simply buys nothing. Solver-iteration LOD on rolling stock is
dead. `SolverLodFeature` was deleted on 2026-08-14; it lives in the git history if the
question ever reopens.

(Earlier sessions, kept for context: session 1 was invalid — `tracked=0`, the discovery
bug — but established a **±9 fps noise floor** with the mod provably inert. Session 2 had
one window per arm and was inconclusive.)

### Stationary sleep — DEAD before it was built, measured 2026-08-13

The read-only headroom probe answered the gating question in the same session. Means over
10 gameplay windows, out of 519 tracked cars:

| | Mean | Share |
|---|---|---|
| stationary | 451.3 | 87.0% |
| already asleep | 449.7 | 86.6% |
| **stationary and awake** | **1.6** | **0.31%** |

Against the pre-agreed rule (<10% → "PhysX already handles it, do not build"), this is
**0.31%**. Essentially every parked car is already asleep.

The prior argument for building it was that bodies in constant contact with track colliders
and bound by bogie/coupler joints often fail to auto-sleep. On this save they sleep fine.
`StationarySleepFeature` will not be built. The probe itself was deleted on 2026-08-14 —
its question is answered and re-asking it is a `git show` away.

### Tree crossfade — NULL, measured 2026-08-14

`TerrainLodFeature` writes `treeCrossFadeLength` straight from the slider, without the
`ClampReduction` its three siblings go through, so the shipped default of 20 m was being
written over the game's own 5 m. Since crossfade is the band where a tree renders *both*
its mesh and its billboard, blended, a 20 m band against a 19.6 m billboard distance meant
very nearly every visible tree was drawn twice — on a feature whose whole thesis is that
billboards batch and meshes do not.

Mechanically sound. Measured, it bought nothing:

| Arm | Windows | Mean fps | SD | Mean moving |
|---|---|---|---|---|
| crossfade 20 m | 21 | **45.79** | 4.95 | 80.3 |
| crossfade 4.8 m | 23 | **45.51** | 7.00 | 87.7 |

0.28 fps apart, wrong direction, against a per-arm SD of 5–7. Adjusting for the second
run's ~7 extra moving cars puts the crossfade arm about 1.3 fps ahead, which is 0.7
standard errors — nothing. Not a controlled A/B (sequential runs, uncontrolled workload),
so it is weaker evidence than the solver-LOD kill, but it is nowhere near a signal.

Keep crossfade at ~5 anyway: writing 4× the game's own value was never justified. Just do
not count it as a gain. This is the third good-sounding mechanism to buy nothing.

### Discovery — confirmed working

```
Discovery: 519 culler records -> 519 tracked (rb on root: 0, rb in children: 519, no rigidbody: 0)
```

0 of 519 cars carry a rigidbody on the root; all 519 carry one in a child, exactly as
predicted. The child-search fallback is what makes the mod able to act at all.

## THE ANSWER — `TrainController.FixedUpdate`, measured 2026-08-14

Four hypotheses were killed by guesswork. Two probes answered it in an afternoon.

86 telemetry windows, mean 41.3 fps / 24.81 ms frame, mean 84 moving cars.

### Where the frame goes

| | ms/frame | Share |
|---|---|---|
| `total_fixed_ms` (the whole FixedUpdate phase) | **11.37** | **46%** |
| ↳ `script_fixed_ms` (C# in FixedUpdate) | 8.65 | 35% |
| ↳ `phys_fixed_ms` (PhysX itself) | 2.44 | 9.8% |
| `script_update_ms` | 2.41 | 10% |

`script_fixed_ms` explains 84% of frame-time variance (r = 0.918). PhysX is a tenth of
the frame — the fifth and final confirmation that the physics branch was never the problem.

### Who is spending it

`ScriptAttributionProbe` Harmony-patched 170 MonoBehaviour `FixedUpdate`/`Update` methods
(0 failures) and ranked them over 87 windows:

| ms/s | calls/s | Owner |
|---|---|---|
| **122.76** | **50** | **`Assembly-CSharp:TrainController.FixedUpdate`** |
| 19.42 | 23107 | `Assembly-CSharp:Car.FixedUpdate` |
| 19.12 | 300 | `Assembly-CSharp:CullingManager.FixedUpdate` |
| 18.86 | 43 | `UnityEngine.UI:EventSystem.Update` |
| 9.38 | 50 | `KinematicCharacterController:KinematicCharacterSystem.FixedUpdate` |

**One `TrainController`, once per fixed step, 2.46 ms per call.** That is 2.97 ms/frame —
**12% of the entire frame** — and six times the next item. Its call count is 50/s, so the
probe's own ~75 ns/call overhead is 0.004 ms/s against it: this number is essentially
unperturbed, unlike the high-call-count rows (`Car`, `Hose`) which are inflated ~9%.

**It scales linearly with traffic**: against moving cars, **r = +0.894, slope 0.55 ms/s per
moving car**, ranging 74 ms/s at 20 moving to 149 ms/s at 126 moving. That closes the chain
the whole project has been chasing:

> more moving cars → `TrainController.FixedUpdate` does more work → frame time rises → fps falls

The moving-car correlation found earlier (r = −0.57) was real but was a *proxy*. This is
the mechanism, and it is exactly the complaint the project opened with: 12+ concurrent AI
consists degrading framerate.

### Mods are not the problem

By declaring assembly, the base game (`Assembly-CSharp`) accounts for roughly four-fifths
of attributed script time; every mod combined is the remaining fifth, and the largest single
mod entry is under 6 ms/s. **`PassengerHelper` does not appear anywhere in the ranking** —
forking it, which was on the table purely because it is noisy in the log, would have been
wasted effort. This is why attribution came before modification.

Caveats worth carrying: roughly half of `script_fixed_ms + script_update_ms` (~456 ms/s) is
attributed (~220 ms/s), the rest being coroutines, `Invoke`, `LateUpdate` (unpatched) and
Unity's own iteration overhead; and the traffic correlation aligns log windows to CSV
windows by sequence rather than timestamp, since the log lines carry no timestamp.

### The lever this hands us

`fixedDeltaTime` measures **exactly 20.0 ms (50 Hz)**, at 1.21 steps per frame. Everything
in that 11.37 ms bucket runs per *step*, so the tick rate scales it directly:

| Tick rate | Predicted frame | Predicted fps |
|---|---|---|
| 50 Hz (current) | 24.81 ms | 41.3 |
| 40 Hz | 22.54 ms | **44.4** |
| 33 Hz | 21.01 ms | **47.6** |

+3 to +6 fps, from a one-line setting. It is a genuine fidelity trade — coupler slack and
braking are simulated in that loop, so handling may change and it must ship off — but it is
the first lever in this project that the measurements *predict* will work rather than one
that merely sounded plausible. Predicted, not measured: it needs the same A/B treatment
everything else got.

## The signal that led here: framerate scales with moving cars

Measured 2026-08-14, from 44 telemetry windows across the two runs above. Regressing the
`fps` column on the `moving` column:

| | n | r | Slope |
|---|---|---|---|
| Run 2 (09:49) | 23 | **−0.671** | −0.237 fps per moving car |
| Run 1 (09:31) | 21 | −0.350 | −0.157 |
| **Pooled** | **44** | **−0.573** | **−0.210** |

Each additional moving car costs roughly **0.21 fps**. Over the observed range (50 → 116
moving) that is a ~14 fps swing — an order of magnitude larger than anything any feature
here has moved. For contrast, the AE probe's correlation against *consist* count was
−0.059, i.e. noise. This is the first relationship in the project that is clearly not.

Two things make it more than a restatement of "busy scenes are slower":

- `moving` counts cars anywhere in the world, not just on-screen. It tracks AI activity
  across the map rather than camera proximity.
- Moving cars are, near enough, the awake rigidbodies: the sleep probe found ~87% of cars
  asleep, so `moving` is close to a direct count of what PhysX actually simulates. Solver
  LOD cut *iterations* on exactly those bodies and bought nothing, which points away from
  iteration count and toward per-body costs — broadphase, contact generation, or the
  bogie/coupler joints.

**Confounds, unresolved.** More moving cars usually means being near a yard or junction,
which also means more scenery, more decals and more of everything else on screen. Moving
consists also bring wheel and rod animation, smoke and sound sources with them. This
establishes *what scales*, not *which subsystem*, and the telemetry cannot separate them.

What it does give the profiler is a reproducible condition and a specific question, which
none of the three killed hypotheses ever had: drive until `moving` is above 100, attach,
and ask where the per-moving-car cost lands.

## Decisions and constraints

- **Unity Mod Manager, not Railloader** — a deliberate choice for this project.
- **No dependency on Waypoint Queue**, not even soft.
- GitHub repo to live under **`dibujaron`**. **Not yet created** — nothing pushed. Public
  vs private undecided.
- **Do not lower tree density.** User: reducing graphics "looks like 2005", removing trees
  "looks like an alien planet". A *tree optimizer* (full density near camera, aggressive
  LOD/billboarding at distance) is wanted instead. Parked.
- User framing for the project: "Optifine for Railroader". Repo dir renamed from
  `railroader-better-ae` to `railroader-optimizer` to match.
- The 2026-08-11 derailment was **user-caused** — a 14.3 mph collision, and the mod had
  touched zero rigidbodies at that point. Not a mod defect.
- Solver-LOD safety is **still untested**, since the mod was inert until session 2.

## Build notes

No `dotnet` SDK and **no .NET Framework targeting packs** are installed, so MSBuild cannot
build these projects. `build.ps1` drives Roslyn from VS 2019 Build Tools directly against
the Mono BCL that Railroader ships — which is also more faithful, since that is the runtime
mods actually execute on.

```powershell
.\src\Highball\build.ps1 -Deploy
```

Car discovery pattern (no public API exists):
`FindObjectOfType<RollingStock.CarCuller>()` → private field `_records` (IList) → each
record's public `Car` field → `car.gameObject` → **`GetComponentInChildren<Rigidbody>()`**.
The child search is essential; root-only returns null for every car.

## Backups

`WCR_2.20260811-preLOD.bak` in the Saves folder, taken before the mod first touched physics.

## Open threads

**Four hypotheses are dead with evidence**: AE planning (0.23% of wall time vs a 2%
threshold), stationary sleep (0.31% addressable vs a 10% threshold), solver LOD (−0.18 fps
over 5+5 windows) and tree crossfade (−0.28 fps over 21+23 windows). Three of the four were
mechanically sound arguments that simply did not show up in the framerate. **The bottleneck
is now identified** — see THE ANSWER above — so what follows is work, not guesses.

1. **The physics tick-rate lever.** Predicted +3 fps at 40 Hz, +6 at 33 Hz. Cheapest real
   win available, and the prediction is falsifiable: build it, A/B it, believe the CSV.
   Must ship off; it trades simulation fidelity in the loop that handles coupler slack and
   braking.
2. **Make `TrainController.FixedUpdate` cheaper.** 12% of the frame in one method, scaling
   at 0.55 ms/s per moving car. Requires decompiling `Assembly-CSharp` to see what it does
   per step; a Harmony transpiler or a targeted skip would be far more invasive than
   anything here so far, and could break the train sim outright. High value, high risk —
   understand it before touching it.
3. **`EventSystem.Update` at 18.86 ms/s** (0.44 ms/frame) is Unity UI raycasting, which is
   suspicious for a game not in a menu. Cheap to investigate, plausibly free to fix.
4. **Rendering-CPU**, the other third of the frame (`total_postlate_ms` 7.18 ms). The
   profiler counters show `batches == draw_calls` exactly — nothing is batching — with only
   ~450 SetPass calls, i.e. thousands of separate objects sharing few materials. That is
   what the existing car/tree LOD features are aimed at, and now has a real success metric.
5. "Better AE" as a *correctness* mod: re-plan triggers, and switch contention between
   trains. Known affordable, and independent of all of the above.

Tree LOD and `detailObjectDistance` were the previous lead; both are built, both ship off,
and neither has shown a measurable gain. They stay available but are no longer the thread
to pull.

## Profile the game — decided 2026-08-14, route 4 BUILT

Four hypotheses have now been killed by building bespoke instrumentation for each one. A
profiler would have answered all four in an afternoon. Agreed on 2026-08-14 to do this
before any further hypothesis.

**Route 4 is built and shipped** as `FrameBudgetProbe` (`frame_budget`, off by default) —
see the README. It times Unity's player-loop subsystems by inserting marker systems around
each one, needs no download, works in the release player, and reports physics / rendering /
scripts / present as cumulative telemetry columns. Start here; the routes below are the
follow-ups for when it says *which* subsystem and you need to know *where inside it*.

Two corrections to what this section previously assumed, both verified 2026-08-14:

- **Route 3 is dead.** `mono-2.0-bdwgc.dll` contains no `profiler-log`, `log:report` or
  `sample-freq` strings — Unity stripped the log profiler out of its runtime build. Don't
  spend an afternoon on it.
- **Route 1 is weaker than claimed below.** The game root holds exactly two DLLs,
  `UnityPlayer.dll` and `winhttp.dll`. Modern Unity links PhysX *into* `UnityPlayer.dll`,
  so module-level attribution cannot separate physics from rendering — they are the same
  binary — and without symbols for it, samples resolve to `UnityPlayer.dll+0x...` for both.
  Still worth doing, but it will not cleanly settle rendering-vs-physics on its own.

Nothing else needed is installed, verified on 2026-08-14: no AMD μProf, no Unity Editor or
standalone profiler, and only VS 2019 Build Tools (no full Visual Studio, so no VS
performance profiler either). Every route below therefore starts with a download.

Two facts make it tractable, both verified on the install:

- `MonoBleedingEdge/` exists, so this is a **Mono** build, not IL2CPP. Managed code is
  profilable.
- `Railroader_Data\boot.config` currently reads:
  `build-guid`, `hdr-display-enabled`, `gfx-enable-gfx-jobs`, `gfx-enable-native-gfx-jobs`,
  `wait-for-native-debugger=0`, `vr-enabled`, `gc-max-time-slice`. There is **no**
  `player-connection-debug` line, which is why the Unity Profiler cannot attach as shipped.

Four routes, cheapest first:

1. **Native sampling profiler — no game changes at all.** AMD μProf is free and this is a
   Ryzen 9. Attach to the running process and read the flame graph. Mono JIT frames resolve
   poorly, but Unity's *native* frames do not — `Camera.Render`, culling, `PhysX::simulate`
   and the scripting-update boundary all show clearly. That alone settles
   rendering-vs-physics-vs-scripts, which is the question we keep guessing at.
2. **Unity Profiler attach.** Add `player-connection-debug=1` to `boot.config` and attach
   the standalone Unity Profiler over localhost. Gives real Unity markers and a proper
   timeline. Back up `boot.config` first; unverified whether this build honours it.
3. ~~**Mono's own log profiler.**~~ **Dead** — the log profiler is not compiled into
   Unity's Mono build. See above.
4. **In-mod player-loop timing and `ProfilerRecorder` counters — BUILT.** Shipped as
   `FrameBudgetProbe`. The player-loop timing is the part that answers
   rendering-vs-physics-vs-scripts, and it needed no download at all. The
   `ProfilerRecorder` counters (draw calls, batches, SetPass, triangles) ride along in the
   same feature; they are expected to read `na` in a non-development player, and the log
   records their validity at startup so we find out either way.

Route 4 is done and costs nothing to run, so it goes first. Route 1 second, once route 4
has narrowed the question enough that unsymbolized `UnityPlayer.dll+0x...` frames are still
worth reading.

## Cleanups owed

Session-1 and session-2 cleanups are all done. The 2026-08-14 pass then removed the
measurement scaffolding wholesale, now that every hypothesis it was built for has been
answered:

- `SolverLodFeature` and `SleepHeadroomProbe` deleted, along with their settings
  (`EnableSolverLod`, `LowSolverIterations`, `EnableSleepHeadroomProbe`) and the
  eligibility sliders only they consumed (`MinDistanceMeters`, `SteadyAccelThreshold`,
  `RequiredSteadySeconds`, `MovingSpeedThreshold`).
- The A/B harness is gone: `RunExperiment`, `ExperimentTarget`, the BASELINE/ACTIVE
  alternation, the settle timer, target validation, and the claim-starvation warning.
  `Telemetry.cs` went from 756 lines to ~330, and the CSV lost its `mode` column.
- `IFeature.Active` and `IFeature.IsExperimental` are gone — `Active` existed only so the
  A/B harness could pin a feature inactive, and nothing ever read `IsExperimental`.
- `CarFacts` is down to `Distance` and `Speed`; the calm clocks, acceleration and sleep
  flag had no readers left. The `moving` telemetry column survives with its threshold as a
  constant in `Evaluator` rather than a slider, since no feature acts on it.
- Telemetry is now **off by default** (`EnableTelemetry`), and `ExperimentWindowSeconds`
  became `TelemetryIntervalSeconds`.

Also fixed in the same pass: every control in the in-game tab is now built through
`UIPanelBuilder.AddField(label, control)`. A bare `AddSlider` returns an unlabelled,
full-width element — which is why that tab's five sliders rendered with no labels at all
and overlapped the gutter their section titles are drawn in. Slider gating also moved from
poking at the child `Selectable` to the API's own `IConfigurableElement.Disable(bool)`, and
each row now carries a `.Tooltip(...)`.

Outstanding:

- The in-game tab still does not expose either cadence setting
  (`RefreshIntervalSeconds`, `EvaluateIntervalSeconds`), so tuning those requires the UMM
  panel and therefore a trip to the main menu.
