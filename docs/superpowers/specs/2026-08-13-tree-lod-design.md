# Tree LOD — design

Date: 2026-08-13

Adds a tree LOD feature to Highball that cuts per-tree rendering cost **without reducing
tree density**, and splits `IFeature` so a feature that does not act on cars is a
first-class citizen rather than a special case.

## Context

Railroader's quality slider bundles tree density together with tree LOD distances, so
turning quality down to gain frames also strips the forest. The project owner's constraint
from `docs/STATE.md` is explicit: reducing graphics "looks like 2005" and removing trees
"looks like an alien planet". Density is not negotiable.

`docs/STATE.md` also records that the GPU is substantially idle and this is a CPU-side
problem, and that the community's most effective workaround is zooming the camera fully in
(2 fps → 15–20) — which changes *rendering* work, not physics work.

That matters for the mechanism here. Unity draws 3D terrain trees individually, but batches
billboards into a single mesh. Converting distant trees from meshes to billboards is
therefore primarily a **draw-call reduction**, which is a CPU saving — the side the
evidence points at.

### What is verified, and what is not

Verified by scanning `Railroader_Data\Managed\Assembly-CSharp.dll`: the game's own code
references `treeBillboardDistance`, `treeDistance`, `treeMaximumFullLODCount`,
`treeCrossFadeLength`, `detailObjectDistance`, `detailObjectDensity`, and its own
`TreeDensity` / `DetailDensity` settings. Those are `UnityEngine.Terrain` and
`QualitySettings` properties, so the game drives Unity Terrain tree rendering.

Not verified, and to be confirmed during implementation:

- That `Terrain.activeTerrains` is non-empty at runtime on the reference save, and that the
  trees the player sees are terrain trees rather than placed prefabs. If they are prefabs,
  none of these levers apply and this design does not survive contact.
- Railroader also ships `Boxophobic.TheVegetationEngine.Runtime.dll`. The Vegetation Engine
  is a shader framework for wind, seasons and interaction; the expectation is that it
  layers on top of terrain tree rendering rather than replacing it, but that is an
  inference from its type list (`TVETerrainSettings`, `TVEInstanced`, `TVEMeshData`), not a
  confirmed fact.

**Implementation must confirm both before building the lever.** A single log line reporting
terrain count and each terrain's current tree settings settles it.

### Measurement posture

Every prior hypothesis in this project was measured before being built, and two of them
died. For this feature the owner chose to skip the pre-measurement and build the lever
directly, on the grounds that the mechanism is well understood and the community evidence
about rendering cost is strong.

That is a reasonable call and this spec follows it. It costs nothing in evidence terms:
`TreeLodFeature` plugs into the existing A/B harness like any other feature, so setting
`ExperimentTarget = "tree_lod"` produces a measured answer whenever one is wanted.

## Goals

1. Cut tree rendering cost at full density.
2. Make a non-car feature fit the architecture without a third bolted-on special case.
3. Never leave the player's terrain settings modified.

## Non-goals

- **Density is never touched.** Not `TreeDensity`, not `DetailDensity`, not
  `detailObjectDensity`. This is the entire point of the feature.
- No changes to grass or detail objects. `detailObjectDistance` is left alone in this
  spec; it is a plausible follow-up but a separate lever with a separate visual cost.
- No custom LOD meshes, no asset replacement, no shader work.

## The lever

Five terrain properties are in scope-adjacent territory. Only three are touched.

| Property | Touched | Rationale |
|---|---|---|
| `treeBillboardDistance` | **Yes — primary** | Distance past which a tree renders as a flat billboard instead of a 3D mesh. Lowering it converts distant meshes to batched billboards. Density is unaffected: every tree is still there. |
| `treeMaximumFullLODCount` | **Yes — hard cap** | Ceiling on how many trees render at full 3D LOD at once, independent of distance. Bounds worst-case cost when the camera faces a dense stand. |
| `treeCrossFadeLength` | **Yes — cosmetic** | Length of the fade between 3D and billboard. Raising it hides the pop that a shortened billboard distance would otherwise make obvious. |
| `treeDistance` | **Never** | This is the *cull* distance. Lowering it makes trees vanish outright — precisely the "alien planet" outcome the owner rejected. |
| `TreeDensity` / `detailObjectDensity` | **Never** | What the quality slider ruins. Pinned wherever the player set it. |

## Architecture

### The `IFeature` / `ICarFeature` split

`IFeature` today is car-shaped: `TryClaim(TrackedCar)` and `Release(TrackedCar)` are core
members. `SleepHeadroomProbe` already does not fit — it returns `false` from `TryClaim`
forever and gets an explicit `Observe()` call wired into `Main.OnUpdate`. A tree feature
does not fit either, and would need its own third hook.

Split the interface:

- **`IFeature`** keeps what every feature needs regardless of what it acts on: `Id`,
  `DisplayName`, `IsExperimental`, `Enabled`, `Active`, `ReleaseAll()`, `TelemetryHeaders`,
  `TelemetryValues`, and a new `Tick(float deltaTime)` for features that act on their own
  schedule rather than per car.
- **`ICarFeature : IFeature`** adds `TryClaim(TrackedCar)` and `Release(TrackedCar)`.

`FeatureHost.Apply(cars)` offers cars only to `ICarFeature`s. `FeatureHost` gains a
`Tick(deltaTime)` that drives feature `Tick`s, which is where `TreeLodFeature` does its
work and where `SleepHeadroomProbe.Observe` moves — removing the existing wart rather than
adding a second one.

`Tick` is called only on features that are both `Enabled` and `Active`. A feature that is
either disabled or inactive gets `ReleaseAll()` instead, on the same pass — so an A/B
baseline window or a runtime toggle-off restores terrain settings immediately rather than
waiting for the next tick. This mirrors what `FeatureHost.Apply` already does per car.

`SolverLodFeature` becomes an `ICarFeature`; its behaviour is unchanged.

The final whole-branch review flagged this seam as needing work before a second feature
type arrived. This is that moment, and it is cheaper now than after.

### TreeLodFeature

Owns no car state. On each `Tick`, at most once per `TreeRefreshIntervalSeconds`:

1. Enumerate `Terrain.activeTerrains`.
2. For each terrain not yet known, capture its current `treeBillboardDistance`,
   `treeMaximumFullLODCount` and `treeCrossFadeLength` as that terrain's originals.
3. Apply the configured values, and remember exactly what was written.
4. Drop terrains that have gone away.

`ReleaseAll()` writes every captured original back to its terrain and clears the table.

### The stale-original hazard

This is the same shape as the `solverIterations` poisoning that a second rolling-stock mod
can cause, and it must be designed against rather than discovered.

If the player changes Railroader's graphics quality while the feature is active, the game
rewrites the terrain properties underneath us. Our captured originals are then stale, and a
later restore would clobber the player's new choice with the old values.

Guard: alongside each terrain's originals, record the exact value this feature last wrote.
On each pass, compare the terrain's current value against that. If they differ, the game
(or another mod) changed it — **re-capture it as the new original** instead of overwriting
it. This makes the feature yield to the player rather than fight them, and keeps restores
honest.

## Settings

All default-off and conservative. The intent is that the owner tunes *down* from a visually
safe starting point until it looks wrong, rather than starting ugly.

| Setting | Default | Range | Note |
|---|---|---|---|
| `EnableTreeLod` | `false` | toggle | Experimental. |
| `TreeBillboardDistanceMeters` | `60` | 10–250 | The main lever. Unity's own stock default is 50; Railroader sets its own value, reported in the first log line. |
| `TreeMaxFullLodCount` | `50` | 0–250 | Unity's stock default is 50. |
| `TreeCrossFadeLengthMeters` | `20` | 0–100 | Raise to hide the billboard pop. |
| `TreeRefreshIntervalSeconds` | `2` | 0.5–10 | How often terrains are re-enumerated and values re-applied. |

Drawn by UMM from `[Draw]` attributes like every other Highball setting, with the tuning
values gated `VisibleOn = "EnableTreeLod|true"`.

**The feature may only ever reduce work.** Railroader sets its own terrain values and we do
not know them until runtime, so a fixed default could easily be *higher* than what the game
already uses — which would turn an optimization into a de-optimization, silently, while the
panel claimed the feature was on.

So the two cost-bearing values are clamped against the captured original:

- `treeBillboardDistance` is set to `min(TreeBillboardDistanceMeters, original)`
- `treeMaximumFullLODCount` is set to `min(TreeMaxFullLodCount, original)`

`treeCrossFadeLength` is cosmetic and is applied as configured, not clamped.

The clamp is computed against the *original*, not the last written value, so repeated
passes cannot ratchet. Both clamped values are pure functions of (configured, original) and
belong in `Decisions.cs`, where the console test runner can pin them — including the case
where the configured value exceeds the original and the original must win.

## Telemetry

`TreeLodFeature` contributes two columns: `terrains` (how many terrains were found and
written) and `tree_billboard_distance` (what was actually applied). A row where `terrains`
is `0` is the signal that the game does not use terrain trees and the whole feature is
inert — the same "prove it is not silently doing nothing" discipline that the discovery
diagnostics exist for.

Because the tuning values are part of the CSV drift key, changing any of them mid-session
rolls over to a new file rather than leaving the banner describing rows it no longer
matches.

## Safety model

Inherits Highball's existing model:

- Restores are immediate and unconditional; everything is handed back on toggle-off,
  unload, feature-disable via the UMM panel, and A/B baseline windows.
- Terrain settings are runtime-only and never persist to the save.
- Per-feature exception isolation: a throw from `TreeLodFeature.ReleaseAll()` must not
  prevent other features from releasing.

One property specific to this feature: it writes to shared global-ish state (terrains)
rather than to per-car state, so unlike `SolverLodFeature` it cannot rely on claim
arbitration for exclusivity. Nothing else in Highball touches terrains, so there is no
conflict today; the stale-original guard above is what protects against outside writers.

## Verification

1. Build via `.\src\Highball\build.ps1`, tests still 17/17.
2. **First in-game check is the existence question:** the log must report a non-zero
   terrain count and each terrain's current tree settings. Zero terrains means the trees
   are not terrain trees and this design needs rethinking before anything else matters.
3. With the feature on, distant trees visibly flatten while near trees stay 3D and the
   forest stays as dense as before.
4. Toggling the feature off restores the original look.
5. Changing Railroader's graphics quality with the feature active must not leave terrain
   settings wrong after a subsequent toggle-off — this exercises the stale-original guard.

## Open threads carried forward

Unchanged by this spec:

1. The ≥4 minute solver-LOD A/B, with `ExperimentTarget = "solver_lod"`.
2. The sleep headroom probe's verdict, and whether `StationarySleepFeature` gets built.
3. Better-AE as a correctness mod: re-plan triggers and switch contention.
4. `detailObjectDistance` for grass and ground detail, as a possible sibling of this
   feature.
