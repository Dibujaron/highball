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
evidence, or it stays behind an explicit `[experimental]` label until it does. A feature
that gets measured and fails is deleted, not left in the panel as a trap — the solver-LOD
feature and the sleep headroom probe both answered their question and were removed once
they had. Their code is in the git history if the question ever reopens.

## Features

| Feature | Id | Status | What it does |
| --- | --- | --- | --- |
| Tree & ground detail LOD | `terrain_lod` | Experimental, **off** by default | Draws distant trees as batched billboards and shortens ground-detail draw distance. Never changes density — the forest stays as thick as you set it. Unmeasured in-game; the rendering-CPU hypothesis it targets is the current lead but is not yet confirmed. |
| Car renderer LOD | `car_renderer_lod` | Experimental, **off** by default | Stops distant rolling stock from casting shadows. Cars never disappear or change shape. Unmeasured in-game, for the same reason as above. |

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

## Prior art

`RailroaderStockOptimizer` by thebikwirm is prior work in this same area, addressing
rolling-stock performance in Railroader.

## Requirements

- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)
- Railroader

All changes are runtime-only and never persist to the save.
