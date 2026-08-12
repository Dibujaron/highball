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

### RollingStock Optimizer is a no-op — confirmed

The installed build is byte-identical to the latest release (MD5 `9346b04d…`), and its
settings were tuned well past defaults. It still does nothing, because it looks for the
car's `Rigidbody` on the root GameObject:

```csharp
Rigidbody rb = go != null ? go.GetComponent<Rigidbody>() : null;
```

Our diagnostic proves **0 of 519 cars** have a rigidbody on the root; all 519 have it in a
child. Every action path in that mod is gated behind `state.Rigidbody == null` (lines 299,
552, 570), so it discovers 519 cars, logs "tracking 519 cars", and acts on none.

**Open action:** a one-line upstream fix to `thebikwirm/RailroaderStockOptimizer` would fix
rolling-stock performance for every user. User was asked about opening a PR; **not yet
authorized** — do not open anything under their name without a clear go-ahead.

## Current work: `src/StockPhysicsLOD`

Reduces PhysX `solverIterations` (6 → 2) on cars that are >500 m from camera **and** have
been under 0.5 m/s² for 3 continuous seconds. Targets the gap RollingStock Optimizer leaves
by design: it never touches *moving* cars, and Discord consensus is that moving stock is
the dominant cost.

Safety model is restore-biased: downgrades require sustained calm, restores are immediate
and unconditional, and everything is handed back on toggle-off and unload. Solver settings
are runtime-only and never persist to the save.

Ships with an A/B harness that alternates BASELINE/ACTIVE every 30 s, discarding 2 s after
each switch, logging to `%LOCALLOW%\Giraffe Lab LLC\Railroader\StockPhysicsLOD.csv`.

### Results so far — INCONCLUSIVE, do not treat as a win

Session 1 (2026-08-11 21:53) was **invalid**: `tracked=0`, the discovery bug. Six windows
of nothing. Useful only as a **noise floor** — with the mod provably inert, BASELINE/ACTIVE
still differed by up to **9 fps** (54.6 vs 45.1) from noise alone.

Session 2 (22:02), after the fix — mod genuinely active:

| Mode | fps | tracked | moving | downgraded |
|---|---|---|---|---|
| BASELINE | 52.06 | 519 | 74 | 0 |
| ACTIVE | **47.46** | 519 | 86 | **430** |

The mechanism works — 430 of 519 cars downgraded. But **ACTIVE was 4.6 fps slower**, and
there is only **one window per arm**. That difference sits well inside the ±9 fps noise
floor measured above, and the two windows weren't even comparable workloads (74 vs 86 cars
moving). This is not evidence of benefit, and not yet evidence of harm.

**Next step: a proper run.** ≥4 minutes, giving ~4 windows per arm, then compare
distributions rather than single samples.

## Decisions and constraints

- **Unity Mod Manager, not Railloader** — user cites "politics" around Railloader.
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
.\src\StockPhysicsLOD\build.ps1 -Deploy
```

Car discovery pattern (no public API exists):
`FindObjectOfType<RollingStock.CarCuller>()` → private field `_records` (IList) → each
record's public `Car` field → `car.gameObject` → **`GetComponentInChildren<Rigidbody>()`**.
The child search is essential; root-only returns null for every car.

## Backups

`WCR_2.20260811-preLOD.bak` in the Saves folder, taken before the mod first touched physics.

## Open threads

1. Run the real A/B on `StockPhysicsLOD` (≥4 min) and decide whether solver LOD helps.
2. If it doesn't: **rendering-CPU is the leading remaining suspect.** The community's most
   effective workaround is zooming the camera fully in (2 fps → 15–20). Camera zoom changes
   *rendering* work, not physics work. With 519 cars, MSLDecalPack and three livery packs,
   and Giraffe Lab's own release note about "adaptive decal culling… with many nearby train
   cars", this fits the evidence well.
3. Upstream PR to RollingStock Optimizer (needs authorization).
4. "Better AE" as a *correctness* mod: re-plan triggers, and switch contention between
   trains. Known affordable.
5. Tree optimizer.
