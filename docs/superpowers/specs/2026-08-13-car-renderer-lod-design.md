# Car renderer LOD — design

Date: 2026-08-13

Cuts the rendering cost of distant rolling stock without making any car disappear or visibly
change. Third and last of the draw-call family: **trees, ground detail, car renderers**.

## Context

Session 3 (see `docs/STATE.md`) killed car *physics* as a cost — solver LOD moved framerate
by −0.18 fps over five windows per arm, and 87% of cars are already asleep. It said nothing
about car *rendering*.

The symptom the owner actually reports is that lag arrives in the **lategame**, not at any
particular place. The thing that grows monotonically through a save is the car count,
currently **519**. Each car carries meshes, a livery (three livery packs installed) and
decals (MSLDecalPack). Giraffe Lab's own release notes mention "adaptive decal culling…
with many nearby train cars".

That is a draw-call-bound CPU cost that scales with progression exactly as described, on
hardware whose GPU is substantially idle.

## Goals

1. Reduce per-car rendering work at distance.
2. Never make a car vanish, shrink, or visibly change.
3. Cost less per frame than it saves — this is a performance mod, so the optimizer itself
   must not walk thousands of renderers every pass.

## Non-goals

- No disabling of car renderers. `Renderer.enabled = false` or `forceRenderingOff` makes
  cars vanish. That is the `treeDistance` mistake in a different costume.
- No mesh replacement, no material or shader changes, no decal removal.
- No changes to physics. That question is settled.

## The levers

| Lever | Used | Rationale |
|---|---|---|
| `Renderer.shadowCastingMode = Off` | **Yes — primary** | A shadow-casting renderer is drawn again per shadow-casting light. Turning it off at distance removes those draw calls. A distant car's own shadow is a few pixels; losing it is invisible in practice. |
| `LODGroup.ForceLOD(n)` | **Yes, if present** | If cars ship LOD groups, forcing a lower level at distance is exactly the intended mechanism. Whether Railroader's cars have `LODGroup` components is unverified — the feature reports what it finds and no-ops if they are absent. |
| `Renderer.enabled` / `forceRenderingOff` | **Never** | Makes cars disappear. |
| Anything on the material | **Never** | Shared materials; a write would leak across every car using it. |

## Architecture

`CarRendererFeature` is an `ICarFeature` (see the tree spec for the `IFeature` /
`ICarFeature` split), so it participates in the existing claim arbitration and gets its
per-car facts — crucially `Distance` — computed once by `Evaluator` like every other
feature.

Priority order becomes `CarRendererFeature` → `SolverLodFeature`. They do not conflict in
practice (one writes renderers, the other writes `solverIterations`), but arbitration means
only one claims a given car, and the renderer feature is the one with a measured rationale
behind it.

### The cost-of-the-optimizer problem

519 cars × roughly 10–30 renderers each is 5,000–15,000 renderers. Walking all of them
every 0.25 s evaluation pass would plausibly cost more than it saves.

So the feature is **edge-triggered, not level-triggered**:

- Each car's renderer array is gathered **once**, lazily, the first time that car crosses
  the threshold — not at discovery, so cars that never go distant cost nothing.
- Per-car state is a single `bool ShadowsSuppressed`.
- On each pass a car is compared against the threshold. **Work happens only on a
  transition** — near→far suppresses, far→near restores. A car sitting far away for ten
  minutes costs one boolean comparison per pass.
- A hysteresis band (suppress beyond `distance`, restore inside `distance - 50 m`) prevents
  a car hovering at the boundary from thrashing thousands of renderer writes.

The hysteresis margin is a pure function of the configured distance and belongs in
`Decisions.cs`, where the console runner can pin it.

## Safety model

Inherits Highball's model, with one addition specific to this feature.

Restores are immediate and unconditional, on: threshold re-entry, toggle-off, feature
disable via the panel, A/B baseline window, car reaping, and unload. Per-feature exception
isolation applies.

**The addition:** a car's renderer array can go stale. Railroader may add or remove child
objects (couplers, loads, decals) after we cached them. A cached `Renderer` whose object
was destroyed reads as null via Unity's overloaded `==`. Every write is therefore
null-checked per renderer, and a car whose array contains any dead entry has its array
re-gathered on the next transition rather than trusted.

Original `shadowCastingMode` is captured per renderer at first suppression — not assumed to
be `On`, since a mod or the game may already have set something else. This is the same
lesson as the `solverIterations` capture: never assume you know the original.

## Settings

| Setting | Default | Range | Note |
|---|---|---|---|
| `EnableCarRendererLod` | `false` | toggle | Experimental until measured. |
| `CarShadowDistanceMeters` | `300` | 50–2000 | Beyond this, a car stops casting shadows. |
| `CarForceLodEnabled` | `false` | toggle | Only meaningful if cars have LOD groups. |
| `CarForceLodDistanceMeters` | `500` | 100–3000 | Beyond this, force the lowest LOD. |

## Telemetry

Three columns: `cars_shadows_off` (cars currently suppressed), `renderers_touched`
(cumulative writes this session — the honest measure of what the optimizer itself is
costing), and `cars_lod_forced`.

`renderers_touched` climbing steadily rather than plateauing is the signal that hysteresis
is not holding and the feature is thrashing. That is the specific way this feature could
make things worse, so it is measured rather than assumed.

## Verification

1. Build clean; decision tests still pass, plus new cases for the hysteresis band.
2. In-game: the log reports how many cars have `LODGroup` components, settling that unknown.
3. `cars_shadows_off` rises as the camera pulls away from a yard and falls as it returns.
4. `renderers_touched` plateaus when the camera is still. If it climbs continuously, the
   hysteresis is wrong.
5. Toggling off restores shadows — visually confirmable by watching a distant yard.
6. No car ever disappears.

## Open question

Whether Railroader's cars carry `LODGroup` components at all is unverified and cannot be
settled without running. If they do not, `CarForceLodEnabled` is inert and the feature is
shadows-only — which is still the larger of the two levers.
