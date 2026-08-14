# Highball

A performance mod for [Railroader](https://store.steampowered.com/app/1683150/Railroader/),
built on [Unity Mod Manager](https://www.nexusmods.com/site/mods/21).

*Highball* is a railroad signal and radio call meaning "clear track ahead, proceed at
maximum authorized speed." The motivating problem is framerate: running 12+ concurrent AI
consists degrades framerate badly, on hardware that should be nowhere near its limits.

## Measurement first

Every feature in this mod exists because a measurement justified it, and every feature
ships with its own off switch. That discipline exists because it was learned the hard way.
Three physics hypotheses have now been measured and killed:

| Hypothesis | Measured | Threshold | Verdict |
| --- | --- | --- | --- |
| Auto Engineer planning cost | 0.23% of wall time | 2% | Dead — AE plans at roughly 1 Hz per consist, not every tick. |
| Stationary sleep (forcing parked cars to sleep) | 0.31% of tracked cars addressable | 10% | Dead before it was built — PhysX already sleeps nearly every parked car on its own. |
| Solver iteration LOD on distant, steady rolling stock | −0.18 fps over five 30 s windows per arm (BASELINE vs. ACTIVE) | — | Dead — measurably no benefit, in the wrong direction, against a ±9 fps noise floor. |

**Physics is not the bottleneck.** By elimination, rendering-CPU is the leading remaining
suspect — the community's most effective workaround (zooming the camera fully in) changes
rendering work, not physics work. The two LOD features below target that suspect; neither
has been measured in-game yet, so both ship off. Full detail lives in `docs/STATE.md`.

Nothing here ships on "seems like it should help." A feature either carries its own
evidence, or it stays behind a read-only probe (or an explicit `[experimental]` label)
until it does.

## Features

| Feature | Id | Status | What it does |
| --- | --- | --- | --- |
| Tree & ground detail LOD | `terrain_lod` | Experimental, **off** by default | Draws distant trees as batched billboards and shortens ground-detail draw distance. Never changes density — the forest stays as thick as you set it. Unmeasured in-game; the rendering-CPU hypothesis it targets is the current lead but is not yet confirmed. |
| Car renderer LOD | `car_renderer_lod` | Experimental, **off** by default | Stops distant rolling stock from casting shadows. Cars never disappear or change shape. Unmeasured in-game, for the same reason as above. |
| Solver iteration LOD | `solver_lod` | Experimental, **off** by default | Lowers PhysX solver iterations on rolling stock that is far from the camera and has been mechanically steady for a while. **Measured and dead**: its one A/B run showed no benefit (−0.18 fps, wrong direction). Kept available for further experimentation, not as a recommended setting. |
| Sleep headroom probe | `sleep_headroom` | Read-only diagnostic, **on** by default | Counts how many tracked cars are stationary but not asleep in PhysX. Answered, before any code was written to act on it, whether forcing sleep on parked cars was a lever worth pulling. It was not (0.31% addressable) — `StationarySleepFeature` will not be built. Mutates nothing, so it stays on. |

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
  feature's own toggle is on, the same gating the UMM panel's `VisibleOn` gives — but the
  tab is not a full mirror of the UMM panel: it does not expose `LowSolverIterations` or
  either cadence setting (`RefreshIntervalSeconds`, `EvaluateIntervalSeconds`), and its
  label for solver iteration LOD reads "(no measured benefit)" rather than the UMM panel's
  `[experimental]`, since that feature has since been measured and killed. If the patch
  fails (e.g. an incompatible game update), it logs why and leaves the preferences window
  untouched — the UMM panel is always the fallback.

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

`Decisions.cs` holds the mod's pure decision logic (eligibility, headroom classification)
separately from anything that touches Unity or PhysX, specifically so it can be tested
without the game. Run it with:

```powershell
.\tools\HighballTests\build.ps1
```

33 assertions, no game required — and they cover only that pure decision logic, not car
discovery, telemetry, the settings panel, or the in-game tab. Everything downstream of
`Decisions` touches Unity APIs that only exist inside the running game, so that code is
verified by compiling cleanly and by reading in-game telemetry instead.

## Telemetry

Highball writes one CSV file **per session**, regardless of settings, to

```
%USERPROFILE%\AppData\LocalLow\Giraffe Lab LLC\Railroader\
```

named `Highball-<yyyyMMdd-HHmmssfff>.csv`.

Each file starts with exactly one `#`-prefixed banner line recording the session
timestamp, the Ids of every enabled feature, and `target=<ExperimentTarget>`, followed by
one more `#`-prefixed settings line, then exactly one header line — readable with a plain
CSV reader that skips `#` lines:

```python
import pandas as pd
df = pd.read_csv(path, comment="#")
```

The `mode` column reads `LIVE` during normal play, when every feature simply follows its
own toggle. Turning on the optional A/B experiment (off by default) additionally alternates
one named feature (`ExperimentTarget`) between baseline and active windows, stamping each
row `BASELINE` or `ACTIVE` instead, so that feature's effect can be isolated without the
player running a manual protocol. Toggling the A/B experiment mid-session does **not** start
a new file — `LIVE` and `ACTIVE`/`BASELINE` rows can both appear in the same CSV, in
sequence. The file rolls over to a new one (`...-2.csv`, `...-3.csv`, ...) whenever the
set of enabled features, the experiment target, or any tunable recorded on the
`# SETTINGS` line (e.g. moving the car shadow or tree billboard distance sliders
mid-session) actually changes, since any of those changes what the columns, the mode
labels, or the numbers themselves mean.

If `ExperimentTarget` names a feature that's missing, disabled, or has an inert `Active`
setter, Highball logs a loud warning — at startup if the A/B experiment is already
running by then, or the moment "Run A/B experiment" is turned on mid-session otherwise —
since an A/B whose two arms are actually identical would otherwise look, in the CSV,
exactly like a genuine null result.

### Reading the headroom probe's verdict

The sleep headroom probe's columns end in `asleep,stationary,stationary_awake`. Its verdict
— also shown live in the UMM panel — classifies the share of tracked cars that are
stationary but still awake, using a decision rule agreed before any data was collected:

| Stationary-and-awake share | Verdict | Meaning |
| --- | --- | --- |
| < 10% | `none` | PhysX is already putting parked cars to sleep on its own; forcing it is not a real lever. |
| 10–30% | `marginal` | Some headroom exists, but building a sleep feature is a judgment call, not an obvious win. |
| > 30% | `real` | A meaningful share of parked cars sit awake; a stationary-sleep feature is worth building. |

The measured result on the reference save was 0.31% — solidly `none`.

## Prior art

`RailroaderStockOptimizer` by thebikwirm is prior work in this same area, addressing
rolling-stock performance in Railroader.

## Requirements

- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)
- Railroader

All changes are runtime-only and never persist to the save.
