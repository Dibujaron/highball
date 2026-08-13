# Highball — design

Date: 2026-08-13

Renames and restructures the `railroader-optimizer` effort into **Highball**, a single
Railroader mod hosting several independently-toggleable performance features. Supersedes
the single-purpose `StockPhysicsLOD` mod, whose code becomes the first feature.

"Highball" is the railroad signal and radio call meaning *clear track ahead, proceed at
maximum authorized speed*.

## Context

Railroader runs at a median ~50 fps with dips to 27–35 on hardware nowhere near its
limits, with 12+ concurrent AI consists and a 519-car save. See `docs/STATE.md` for the
full investigation. Two hypotheses are already dead or unproven:

- **Auto Engineer planning cost** — measured at 0.23% of wall time against a pre-agreed
  2% threshold. Dead.
- **Solver-iteration LOD on moving stock** — implemented, mechanism confirmed working
  (430 of 519 cars downgraded), but the one A/B window showed ACTIVE 4.6 fps *slower*,
  well inside a measured ±9 fps noise floor. Unproven either way.

The pattern is that plausible performance hypotheses here keep failing. Highball is built
so that each feature carries its own evidence.

## Goals

1. One mod, many features, each independently toggleable and each declaring whether it is
   experimental or proven.
2. Discover rolling stock correctly, including the child-rigidbody search without which
   nothing is found at all.
3. Determine whether forcing distant parked cars to sleep is a real lever, before building
   it as a shipped feature.
4. Never leave the player's physics state modified when a feature is off.

## Non-goals

- Not a fork. Highball builds on the existing `StockPhysicsLOD` code in this repo and
  keeps its own license choice.
- No tree optimizer and no Better-AE work in this spec. Both remain parked in `STATE.md`;
  the architecture must simply leave room for them.
- No physics-bubble / floating-origin work. That is the direction upstream is exploring on
  its own branch and is out of scope here.

## Naming and rename

Nothing has been pushed and the GitHub repo was never created, so the rename is free.

| Thing | From | To |
|---|---|---|
| Repo directory | `railroader-optimizer` | `highball` |
| Mod `Id` / `AssemblyName` | `StockPhysicsLOD` | `Highball` |
| `DisplayName` | Rolling Stock Physics LOD | Highball |
| Namespace | `StockPhysicsLOD` | `Highball` |
| `EntryMethod` | `StockPhysicsLOD.Main.Load` | `Highball.Main.Load` |
| Deploy target | `Mods/StockPhysicsLOD` | `Mods/Highball` |
| Telemetry CSV | `StockPhysicsLOD.csv` | `Highball.csv` |

Consequences to handle during implementation:

- UMM keys settings by mod `Id`, so existing tuned settings are lost. Acceptable — the
  current values are defaults plus experiment tuning, all recorded in this repo.
- The old `Mods/StockPhysicsLOD` folder must be deleted from the game install, or UMM will
  load both mods and they will both mutate the same rigidbodies.
- The existing `StockPhysicsLOD.csv` is left in place. Session 1 and 2 data stay readable;
  `Highball.csv` starts clean rather than mixing schemas.

`tools/AEProbe` keeps its name. It is a retired throwaway diagnostic, not a feature.

## Architecture

Two layers: a core that knows about cars, and features that decide what to do with them.

```
Main            UMM entry, lifecycle, panel host
 └─ Core
     ├─ CarRegistry     discovery + the car state table
     ├─ Evaluator       one pass per interval; computes per-car facts
     ├─ FeatureHost     owns features, arbitrates claims, restores on shutdown
     └─ Telemetry       CSV writer + A/B window driver
 └─ Features (IFeature)
     ├─ SolverLodFeature        existing behaviour, experimental
     ├─ StationarySleepFeature  new, gated on Phase 0
     └─ SleepHeadroomProbe      read-only measurement
```

### CarRegistry

Lifts the existing `LodManager` discovery verbatim in behaviour: `FindObjectOfType<CarCuller>()`
→ private `_records` field → each record's public `Car` field → `car.gameObject` →
`GetComponent<Rigidbody>()` **falling back to `GetComponentInChildren<Rigidbody>(true)`**.

The child fallback is the proven fix. On the reference save, 0 of 519 cars carry a
rigidbody on the root and all 519 carry one in a child. This is core behaviour, not a
toggleable feature — without it the mod tracks nothing.

Keeps the existing discovery diagnostics (`records`, `rb on root`, `rb in children`,
`no rigidbody`) logged once per session. These exist because the first version of this
code silently tracked zero cars.

Also keeps the existing reaping pass: cars that leave the world are restored before being
dropped from the table.

### Evaluator

Runs every `EvaluateIntervalSeconds` (0.25 s) over the car table, once, and computes a
single `CarFacts` per car that every feature reads. One walk, not one per feature.

`CarFacts` carries: `DistanceToCamera`, `Speed`, `Acceleration`, `SteadySeconds`
(sustained low acceleration), `StationarySeconds` (sustained near-zero *speed*), and
`IsAsleep` (`Rigidbody.IsSleeping()`).

`SteadySeconds` and `StationarySeconds` are deliberately separate clocks. A consist
cruising at constant speed has near-zero acceleration but is emphatically not stationary,
and the sleep feature must not confuse the two.

### FeatureHost and claim arbitration

Features are offered each car in a fixed priority order. The first enabled feature that
claims a car acts on it; the rest skip it. This makes it structurally impossible for two
features to mutate the same rigidbody.

Priority: `StationarySleep` > `SolverLod`. Sleeping dominates — a sleeping body is skipped
by the solver entirely, so reducing its iteration count is meaningless.

Read-only features (`SleepHeadroomProbe`) never claim and always observe.

Every feature implements `RestoreAll()`. `FeatureHost.RestoreAll()` fans out to all of
them regardless of enabled state, so disabling a feature at runtime still hands back
everything it touched.

### Telemetry

The existing `Experiment` becomes `Telemetry`. It keeps the alternating BASELINE/ACTIVE
window design and the 2 s post-switch settle discard, both of which were right.

Two changes:

- Base columns are written by the core (`wall_clock, mode, window_s, frames, avg_frame_ms,
  fps, tracked, moving`). Each enabled feature appends its own columns after those. The
  header is written once per session from the enabled set, and a `# SESSION` comment line
  records which features were enabled.
- A new `ExperimentTarget` setting names which single feature the A/B toggles. With N
  features, flipping all of them at once would confound the comparison. Features not
  under test hold whatever state their toggle says.

## Features

### SolverLodFeature — experimental, default off

The existing behaviour, unchanged in substance. Claims a car when
`distance > MinDistanceMeters && SteadySeconds >= RequiredSteadySeconds`, sets
`solverIterations` to `LowSolverIterations` (2), and restores the captured original on
release.

Marked experimental and shipped **off**, because the only measurement so far showed it
4.6 fps slower inside a ±9 fps noise floor. Open thread: a ≥4 minute run giving ~4 windows
per arm, compared as distributions rather than single samples.

Feature columns: `solver_downgraded`.

### SleepHeadroomProbe — read-only, default on

Answers one question before any sleep code is written: how many cars are parked but still
awake, and therefore actually addressable by forcing sleep?

Claims nothing and mutates nothing. Contributes three counters to the base telemetry row:

- `asleep` — cars where `Rigidbody.IsSleeping()`
- `stationary` — cars where `Speed <= MovingSpeedThreshold`
- `stationary_awake` — stationary **and** awake

The premise being tested is genuinely uncertain in both directions. PhysX auto-sleeps
bodies whose mass-normalized kinetic energy stays under `sleepThreshold`, which would make
forcing sleep redundant. But bodies in constant contact or bound by joints routinely fail
to auto-sleep — and rolling stock sits on track colliders with bogies and coupler
constraints, which is exactly that configuration. If Railroader's cars never auto-sleep,
519 permanently-awake bodies is a large real cost.

**Decision rule, agreed before looking at data**, in the spirit of the AEProbe 2% rule:

| `stationary_awake` / `tracked` | Verdict |
|---|---|
| < 10% | PhysX already handles it. Do not build the sleep feature. |
| 10–30% | Marginal. Build it, default off, decided by its own A/B. |
| > 30% | Real headroom. Build it as a first-class feature. |

### StationarySleepFeature — build only if the probe clears 10%

Claims a car when all hold:

- `distance > SleepMinDistanceMeters`
- `StationarySeconds >= RequiredStationarySeconds`
- `!IsAsleep` — never touch a car PhysX already parked
- no member of the car's consist is moving

Action is `Rigidbody.Sleep()`. Release is `Rigidbody.WakeUp()`, unconditional.

The consist check is not optional. Per-car velocity is insufficient because a car mid-consist
can read near-zero during slack action while its train is moving.

**Unverified at spec time:** how to reach a car's consist and its members. `Model.Car` is
known to expose `id`, `velocity`, `IsVisible` and `gameObject`; the consist relationship has
not yet been located in the game assemblies. Implementation must confirm this API before
relying on it. If no usable consist accessor exists, the fallback is to require
`StationarySeconds` across a longer window (10 s) and treat that as the guard, which is
weaker and must be noted in the README.

Ships default off regardless of probe outcome, until its own A/B says otherwise.

Feature columns: `slept`.

## Safety model

Inherited from the existing implementation, which got this right:

- Downgrades require sustained calm; restores are immediate and unconditional.
- Everything is handed back on toggle-off, on unload, on `SetActive(false)`, and when a
  car leaves the world.
- All physics changes are runtime-only and never persist to the save.
- A throwing tick disables the mod and restores everything rather than continuing.

One hazard is specific to sleep and does not apply to solver LOD: a forced-asleep car on a
grade **will not roll away when it physically should**. Railroader simulates rollaway.
Mitigations: the `> SleepMinDistanceMeters` gate keeps it far from the player, `WakeUp()`
is unconditional on any state change, and the feature ships off. This hazard is called out
in the README and in the settings panel next to the toggle.

## Settings and GUI

`Settings` gains a per-feature block. Each feature exposes `Enabled`, plus its own tuning
values, and draws its own GUI section. The panel renders one collapsible group per
feature: toggle, an `[experimental]` tag where applicable, and the feature's settings
greyed out when the toggle is off.

Core settings stay global: `MinDistanceMeters`, `MovingSpeedThreshold`,
`RefreshIntervalSeconds`, `EvaluateIntervalSeconds`, and the telemetry block
(`RunExperiment`, `ExperimentWindowSeconds`, `ExperimentTarget`).

Defaults on first install: discovery on (core, not toggleable), `SleepHeadroomProbe` on,
`SolverLodFeature` off, `StationarySleepFeature` off.

## Attribution

Railroader has an active modding community and several people have looked at rolling-stock
performance. The README credits `RailroaderStockOptimizer` by thebikwirm among the prior
work in this area, plainly and without commentary on its implementation.

Highball grows out of the `StockPhysicsLOD` code already in this repo, which was written
against a gap that mod leaves by design — it never touches moving stock. Where the two
converge is on `CarCuller._records` for discovery, which every mod in this space has to
use because the game exposes no public equivalent.

Highball's license is chosen at repo creation.

## Build and deploy

Unchanged in approach. No `dotnet` SDK and no .NET Framework targeting packs are
installed, so `build.ps1` drives Roslyn from VS 2019 Build Tools directly against the Mono
BCL that Railroader ships — which is also the runtime mods actually execute on.

```powershell
.\src\Highball\build.ps1 -Deploy
```

Game assemblies are referenced from the local install and never committed.

## Verification

1. Build succeeds via `build.ps1`.
2. In-game: UMM panel lists Highball with its feature toggles; log reports
   `Discovery: 519 culler records -> 519 tracked (rb on root: 0, rb in children: 519, no rigidbody: 0)`.
   Non-zero tracked with zero on-root is the specific evidence the discovery fix works.
3. `Highball.csv` accumulates rows with the probe columns populated.
4. Toggling each feature off restores every car it touched — verified by watching
   `solver_downgraded` and `slept` return to 0.
5. Back up the save before any run that enables a mutating feature. The existing
   `WCR_2.20260811-preLOD.bak` precedent applies.

## Threads carried forward from STATE.md

Unchanged by this spec, still open:

1. The ≥4 minute solver-LOD A/B, now run with `ExperimentTarget = SolverLod`.
2. Rendering-CPU as the leading remaining suspect if solver LOD fails — the community
   workaround of zooming fully in (2 fps → 15–20) changes *rendering* work, not physics.
3. Better-AE as a correctness mod: re-plan triggers and switch contention. Known cheap.
4. Tree optimizer: full density near camera, aggressive LOD at distance.
