# Project state — 2026-08-11

Resume point for the Railroader performance investigation. Written at end of session 1.

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

Reduces PhysX `solverIterations` (6 → 2) on cars that are >500 m from camera **and** have
been under 0.5 m/s² for 3 continuous seconds. Distant, steadily-moving rolling stock is
otherwise never reduced in fidelity, and Discord consensus is that moving stock is the
dominant cost.

Safety model is restore-biased: downgrades require sustained calm, restores are immediate
and unconditional, and everything is handed back on toggle-off and unload. Solver settings
are runtime-only and never persist to the save.

Ships with an A/B harness that alternates BASELINE/ACTIVE every 30 s, discarding 2 s after
each switch, logging to `%LOCALLOW%\Giraffe Lab LLC\Railroader\Highball-<timestamp>.csv`.

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
dead.

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
`StationarySleepFeature` will not be built. `SleepMinDistanceMeters` and
`RequiredStationarySeconds` are now dead settings and should be removed.

### Discovery — confirmed working

```
Discovery: 519 culler records -> 519 tracked (rb on root: 0, rb in children: 519, no rigidbody: 0)
```

0 of 519 cars carry a rigidbody on the root; all 519 carry one in a child, exactly as
predicted. The child-search fallback is what makes the mod able to act at all.

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

**Physics is not the problem.** Three physics hypotheses are now dead with evidence: AE
planning (0.23% of wall time vs a 2% threshold), stationary sleep (0.31% addressable vs a
10% threshold), and solver LOD (−0.18 fps over 5+5 windows). By elimination, rendering-CPU
is the leading remaining suspect.

1. **Tree LOD — the current lead.** Spec at
   `docs/superpowers/specs/2026-08-13-tree-lod-design.md`. Shorten the distance at which a
   tree becomes a batched billboard, and cap how many render at full 3D LOD, without
   touching density. Unity draws 3D terrain trees individually but batches billboards, so
   this is primarily a draw-call reduction — a CPU saving, which is the side the evidence
   points at.
2. **Rendering-CPU more broadly.** The community's most effective workaround is zooming the
   camera fully in (2 fps → 15–20). Camera zoom changes *rendering* work, not physics work.
   With 519 cars, MSLDecalPack and three livery packs, and Giraffe Lab's own release note
   about "adaptive decal culling… with many nearby train cars", this fits the evidence well.
3. "Better AE" as a *correctness* mod: re-plan triggers, and switch contention between
   trains. Known affordable.
4. `detailObjectDistance` for grass and ground detail, as a sibling of the tree work.

## Cleanups owed

- Remove `SleepMinDistanceMeters` and `RequiredStationarySeconds` — dead, see above.
- Log lines read `[Highball] [Highball]`; UMM already adds the prefix, so drop ours.
- `ExperimentTarget` is a free-text field wanting a technical id (`solver_lod`) that the
  panel gives the player no way to discover. Make it a dropdown.
- Settings are only reachable from the main menu in this game, so changing features
  mid-session means a trip out. Worth knowing when planning a measurement run.
