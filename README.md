# Highball

A performance mod for [Railroader](https://store.steampowered.com/app/1683150/Railroader/),
built on [Unity Mod Manager](https://www.nexusmods.com/site/mods/21).

*Highball* is a railroad signal and radio call meaning "clear track ahead, proceed at
maximum authorized speed." The motivating problem is framerate: running 12+ concurrent AI
consists degrades framerate badly, on hardware that should be nowhere near its limits.

## Measurement first

Every feature in this mod exists because a measurement justified it, and every feature
ships with its own off switch. That discipline exists because it was learned the hard way.
Six hypotheses have now been measured and killed:

| Hypothesis | Measured | Threshold | Verdict |
| --- | --- | --- | --- |
| Auto Engineer planning cost | 0.23% of wall time | 2% | Dead — AE plans at roughly 1 Hz per consist, not every tick. |
| Stationary sleep (forcing parked cars to sleep) | 0.31% of tracked cars addressable | 10% | Dead before it was built — PhysX already sleeps nearly every parked car on its own. |
| Solver iteration LOD on distant, steady rolling stock | −0.18 fps over five 30 s windows per arm (BASELINE vs. ACTIVE) | — | Dead — measurably no benefit, in the wrong direction, against a ±9 fps noise floor. |
| Tree crossfade length (mod was writing 4× the game's own value, so near every visible tree drew twice) | −0.28 fps over 21 vs. 23 windows | — | Null — mechanically sound, invisible in the framerate. |
| Physics tick rate (50 → 40/30 Hz) | +3.06 / +3.81 fps, traffic-controlled, past 2σ | +10 for a fidelity trade | **Worked, and cut anyway** — a knob that coarsens coupler/brake simulation cannot justify +3.6, and the ~45% CPU→frame transfer rate caps the whole approach below +10. |
| GPU instancing flip (`enableInstancing` on 5,268 materials) | `batches == draw_calls`, never diverged | divergence | Null — the shaders lack instancing variants, so the flag is inert. Removed same day; also circumstantially implicated in an app hang. |

The render census that accompanied the last one also closed the passive doors: the SRP
Batcher is already enabled, and the car shader is a lean 14-property URP shader, so
neither "turn the batcher on" nor "swap the shader" has any benefit available. Rendering
gains, if any, must come from drawing *fewer things* — which is what the two LOD features
already target.

Most of those were sound mechanical arguments that simply never showed up in the
framerate. After the fourth, the project built general instrumentation instead of a fifth
guess — and it answered the question in an afternoon:

> **`TrainController.FixedUpdate` is the bottleneck.** One method, once per physics step,
> **2.46 ms per call — 12% of the entire frame**, six times the next largest cost. It scales
> linearly with traffic (r = +0.894, 0.55 ms/s per moving car), which is exactly the
> complaint this project opened with: 12+ concurrent AI consists degrading framerate.

For context, PhysX itself is 9.8% of the frame and the whole `FixedUpdate` phase is 46%.
An earlier reading — "the base game is four-fifths of C# cost, mods one-fifth" — was
**corrected the same day**: it counted only mods' own methods, but a Harmony patch executes
inside the *patched* method's time. Timing the patch methods directly found ~75 ms/s of
mod patch overhead hiding inside game methods, including a Distributed-Power-Control
headlight-sync hook costing ~0.36 ms per physics step inside `TrainController.FixedUpdate`
itself — roughly a quarter of that method's measured cost belongs to the mod, not the
game. Full detail lives in `docs/STATE.md`.

Nothing here ships on "seems like it should help." A feature either carries its own
evidence, or it stays behind an explicit `[experimental]` label until it does. A feature
that gets measured and fails is deleted, not left in the panel as a trap — the solver-LOD
feature and the sleep headroom probe both answered their question and were removed once
they had. Their code is in the git history if the question ever reopens.

## Features

| Feature | Id | Status | What it does |
| --- | --- | --- | --- |
| Tree & ground detail LOD | `terrain_lod` | Experimental, **off** by default | Draws distant trees as batched billboards and shortens ground-detail draw distance. Never changes density — the forest stays as thick as you set it. No measured gain yet, but draw-count reduction is the one rendering lever still open. |
| Car renderer LOD | `car_renderer_lod` | Experimental, **off** by default | Stops distant rolling stock from casting shadows. Cars never disappear or change shape. Same rationale, and same unproven status, as the tree LOD. |
| Frame budget probe | `frame_budget` | Read-only diagnostic, **off** by default | Times Unity's player-loop subsystems to say where the frame actually goes — physics, rendering, scripts, or waiting on the GPU. Changes no game state, but it inserts timing markers into the update loop. See below. |
| Script attribution probe | `script_attrib` | Read-only diagnostic, **off** by default | Splits the C# `FixedUpdate`/`Update` cost into a ranked list of which class, in which assembly, is spending it — so a mod's cost is distinguishable from the base game's without disabling anything. Also times other mods' Harmony patch methods and reports per-owner overhead. Harmony-patches every `MonoBehaviour` in every loaded assembly, which makes it the broadest code here. Reports to the log. |
| Render inventory probe | `render_inventory` | Read-only diagnostic, **off** by default | One-shot census of renderers, unique materials, instancing flags, shaders (with property lists) and the render-pipeline asset, reported to the log. Built to answer why nothing batches; its answers closed the rendering-via-flags avenue. Toggle off and on to re-run. |
| Harmony patch census | `patch_census` | Read-only diagnostic, **off** by default | One-shot census of every Harmony patch in the process: which game methods are patched, by which mod, with what patch kinds — the hot path (`TrainController` / `Car` / air / brake) reported first. Makes mod hooks on the frame path visible without disabling anything. Toggle off and on to re-run. |

**The mod modifies PhysX and renderer state only at runtime and never persists any change
to the save file.** Everything a feature claims is handed back on toggle-off and on mod
unload.

## Settings

Settings are available in two places:

- **Unity Mod Manager's own mod panel.** Drawn directly from the mod's `[Draw]`
  attributes — there is no custom settings window there. Tooltips on each field explain
  what it does.
- **A Highball tab in Railroader's own in-game preferences window**, added via a Harmony
  patch so the most-used settings can be tuned without relaunching. Slider ranges match
  the UMM panel's, and a feature's tuning sliders are only interactable while that
  feature's own toggle is on, the same gating the UMM panel's `VisibleOn` gives. The tab
  is not a full mirror: it does not expose either cadence setting
  (`RefreshIntervalSeconds`, `EvaluateIntervalSeconds`), which still need the UMM panel.
  If the patch fails (e.g. an incompatible game update), it logs why and leaves the
  preferences window untouched — the UMM panel is always the fallback.

  Every control in that tab is built through `UIPanelBuilder.AddField(label, control)`.
  A bare `AddSlider` returns an unlabelled, full-width element that renders with no name
  and overlaps the gutter its section title is drawn in; `AddField` is the wrapper that
  pairs a control with its label and lays it out in the content column. `AddFieldToggle`
  is the same wrapper with a toggle built in, which is why toggles looked right in that
  tab while unwrapped sliders did not.

## Building

Requires Roslyn from VS 2019 Build Tools; no .NET Framework targeting pack is needed, since
the build compiles directly against the Mono BCL that Railroader ships — the runtime the
mod actually runs on.

```powershell
.\src\Highball\build.ps1 -Deploy
```

Override the install location with `-RailroaderDir` if Railroader isn't at
`D:\SteamLibrary\steamapps\common\Railroader`. Game assemblies are referenced from the
local install and are never committed to this repository.

### Tests

`Decisions.cs` holds the mod's pure decision logic (reduction clamping, distance
hysteresis) separately from anything that touches Unity or PhysX, specifically so it can
be tested without the game. Run it with:

```powershell
.\tools\HighballTests\build.ps1
```

16 assertions, no game required — and they cover only that pure decision logic, not car
discovery, telemetry, the settings panel, or the in-game tab. Everything downstream of
`Decisions` touches Unity APIs that only exist inside the running game, so that code is
verified by compiling cleanly and by reading in-game telemetry instead.

## Telemetry

**Off by default.** Recording is a measurement activity, not something normal play should
be doing, so nothing is written until "Record telemetry to CSV" is switched on. Turning it
on starts a new file immediately; turning it off closes the current one.

Files are written to

```
%USERPROFILE%\AppData\LocalLow\Giraffe Lab LLC\Railroader\
```

named `Highball-<yyyyMMdd-HHmmssfff>.csv`.

Each file starts with exactly one `#`-prefixed banner line recording the session timestamp
and the Ids of every enabled feature, followed by one more `#`-prefixed settings line, then
exactly one header line — readable with a plain CSV reader that skips `#` lines:

```python
import pandas as pd
df = pd.read_csv(path, comment="#")
```

Each row averages the frames since the previous one: `wall_clock,window_s,frames,
avg_frame_ms,fps,tracked,moving`, then whichever columns the enabled features contribute.
The file rolls over to a new one (`...-2.csv`, `...-3.csv`, ...) whenever the set of
enabled features or any tunable recorded on the `# SETTINGS` line changes mid-recording,
since either changes what the columns or the numbers themselves mean.

To compare a feature against itself, record with it off, then with it on, and compare the
two files. An earlier version of this mod automated that as an alternating A/B harness; it
was removed once the hypotheses it was built to test had all been answered.

## The frame budget probe

Four hypotheses in a row were killed by building a bespoke instrument for each one. This is
the general instrument that should have come first: it answers *where the frame goes*
rather than *is this one guess right*.

Unity's main-thread frame is a tree of player-loop subsystems, and `PlayerLoop` exposes it.
A native subsystem has no managed delegate to wrap — it's a raw function pointer — so the
probe replaces nothing. It inserts its own marker systems immediately before and after each
subsystem of interest and times the gap: two `Stopwatch` timestamps per measured subsystem
per frame, roughly half a microsecond against a 20 ms frame.

It only ever *inserts* markers, never removes or reorders an existing entry, and never
mutates an existing array in place. On toggle-off it restores by stripping its own markers
out of whatever the loop looks like at that moment, rather than reverting to a saved copy,
so a different mod that changed the loop afterwards doesn't get clobbered.

Its columns are **cumulative**, like `renderers_touched` — difference two rows to get the
ms-per-frame spent in each subsystem over that window:

```python
df = pd.read_csv(path, comment="#")
ms = df.filter(regex="_ms$").diff().div(df.budget_frames.diff(), axis=0)
```

Cumulative rather than per-window averages on purpose: `Telemetry` reads `TelemetryValues`
more than once per row when a file rolls over, so a getter that reset its own accumulators
would report zeros for exactly the row that rolled over.

Buckets are grouped by prefix — `phys_*`, `script_*`, `rend_*`, plus `cull_notify_ms` and
`present_ms`. `present_ms` is separated deliberately: it's where an uncapped but GPU-bound
frame parks itself, and without it, waiting on the GPU would be invisible and the CPU would
look slower than it is. Anything left over after subtracting every bucket from
`avg_frame_ms` is main-thread time the probe doesn't cover, or work that ran on a render
worker thread — this install has `gfx-enable-native-gfx-jobs=1`, so worker-thread render
work is *not* captured here.

The `draw_calls`, `setpass_calls`, `batches` and `triangles` columns come from
`ProfilerRecorder`. Those counters are usually stripped from non-development players, so
they may read `na` — the log records whether they came back valid at startup. On this
install they came back **valid**. One reading note learned the hard way: `batches ==
draw_calls` does *not* mean the SRP Batcher is off — that batcher reduces per-draw cost,
not draw count, so the two columns match either way. The pair only diverges when GPU
instancing or static/dynamic batching actually merges draws.

`fixed_steps` is cumulative like the rest. Everything in the dominant `total_fixed_ms`
bucket runs once per fixed *step*, not once per frame, so steps-per-frame is what says
whether changing the physics tick rate would buy anything.

## The script attribution probe

The frame budget probe can say *that* C# in `FixedUpdate` is a third of the frame, but not
*whose*: Unity invokes every `MonoBehaviour.FixedUpdate` from native code, so the base game
and every mod collapse into one number. They're ordinary managed methods though, so this
probe Harmony-patches each one and times it individually, reporting a ranked list by
declaring assembly and type to the log every telemetry interval:

```
ScriptAttrib: 30.1s window, 213.8 ms/s across 170 patched methods. Top 20:
    70.30 ms/s         50 calls/s  Assembly-CSharp:TrainController.FixedUpdate
    14.81 ms/s      23358 calls/s  Assembly-CSharp:Car.FixedUpdate
```

The assembly name is the point: it tells you whether a cost belongs to the game or to a
specific mod, without disabling anything — which matters when the mods in question are
load-bearing. It's what turned "PassengerHelper is noisy, maybe fork it" into "PassengerHelper
does not appear in the ranking at all".

Two things to keep in mind reading it. The timing adds roughly 75 ns per call, so rows with
huge call counts are inflated by a few percent while rows called 50 times a second are
essentially exact — compare rankings, not absolute totals. And rows are per-class, so a mod
spread across ten components shows as ten rows; aggregate by assembly to judge a mod.

It patches hundreds of methods belonging to other people's code, which makes it the
broadest thing in this repository. It ships off, patches under its own Harmony id so
removing it can't disturb the preferences-window patch, isolates every individual patch so
one unpatchable method can't abort the sweep, and counts calls whose method fails to resolve
rather than silently understating the ranking.

It also times **other mods' Harmony patch methods**. A patch executes inside the patched
method's time, so a mod's hook on a game method otherwise masquerades as base-game cost in
every ranking above. The probe enumerates every foreign prefix/postfix/finalizer in the
process, wraps each with the same timing pair, and appends a "Patch overhead by owner"
section to the report — per-mod ms/s without disabling anything, which matters when the
mods are load-bearing. Read it as a **lower bound**: Harmony's own stub overhead and any
patch call site the JIT inlined are invisible, so a known-hot patch showing 0 calls/s means
the detour didn't take, not that the patch is free.

## Prior art

`RailroaderStockOptimizer` by thebikwirm is prior work in this same area, addressing
rolling-stock performance in Railroader.

## Requirements

- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)
- Railroader

All changes are runtime-only and never persist to the save.
