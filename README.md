# Highball

A performance mod for [Railroader](https://store.steampowered.com/app/1683150/Railroader/),
built on [Unity Mod Manager](https://www.nexusmods.com/site/mods/21).

*Highball* is a railroad signal and radio call meaning "clear track ahead, proceed at
maximum authorized speed." The motivating problem is framerate: running 12+ concurrent AI
consists degrades framerate badly, on hardware that should be nowhere near its limits.

## Measurement first

Every feature in this mod exists because a measurement justified it, and every feature
ships with its own off switch. That discipline exists because it was learned the hard
way: `docs/STATE.md` records two performance hypotheses — Auto Engineer planning cost, and
solver-iteration LOD on distant rolling stock — that looked plausible going in and turned
out to be non-issues (or unproven, at best) once actually measured. Nothing here ships on
"seems like it should help." A feature either carries its own evidence, or it stays behind
a read-only probe until it does.

Solver LOD's one A/B run so far showed no benefit, so it ships **off**, kept for further
experimentation. The sleep headroom probe mutates nothing, so it ships **on**.

## Features

| Feature | Id | Status | What it does |
| --- | --- | --- | --- |
| Solver iteration LOD | `solver_lod` | Experimental, **off** by default | Lowers PhysX solver iterations on rolling stock that is far from the camera and has been mechanically steady for a while. Its one measurement so far showed no benefit; it stays available for further A/B runs, not as a recommended setting. |
| Sleep headroom probe | `sleep_headroom` | Read-only, **on** by default | Counts how many tracked cars are stationary but not asleep in PhysX. Answers, before any code is written that would act on it, whether forcing sleep on parked cars is a lever worth pulling at all. Mutates nothing. |

**The mod modifies PhysX state only at runtime and never persists any change to the save
file.** Everything a feature claims is handed back on toggle-off and on mod unload.

### Not built yet: stationary sleep

A feature that forces distant, stationary cars to sleep is gated behind the headroom
probe's result (see below) and is deliberately not implemented yet — building it before
measuring would repeat the same mistake `docs/STATE.md` already records twice.
`SleepMinDistanceMeters` and `RequiredStationarySeconds` exist in the settings panel as
placeholders for it and currently do nothing.

## Settings

All settings are drawn by Unity Mod Manager directly from the mod's `[Draw]` attributes —
there is no custom settings window. Open the Highball panel in the UMM mod list to see and
change them; tooltips on each field explain what it does.

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

17 assertions, no game required. Everything downstream of `Decisions` — car discovery,
telemetry, the settings panel — touches Unity APIs that only exist inside the running game,
so that code is verified by compiling cleanly and by reading in-game telemetry instead.

## Telemetry

When the A/B experiment is running, Highball writes one CSV file **per session** to

```
%USERPROFILE%\AppData\LocalLow\Giraffe Lab LLC\Railroader\
```

named `Highball-<yyyyMMdd-HHmmssfff>.csv`. This replaces an earlier design that appended
every session to a single `Highball.csv`; that file no longer exists.

Each file starts with exactly one `#`-prefixed banner line recording the session
timestamp, the Ids of every enabled feature, and `target=<ExperimentTarget>` (the one
feature the A/B harness alternates between baseline and active windows — every other
enabled feature holds a steady state across both arms, so any fps delta can be attributed
to the target alone). Exactly one header line follows the banner. If the set of enabled
features changes mid-session, the writer does not re-emit a header into the same file —
it rolls over to a new file instead (`...-2.csv`, `...-3.csv`, and so on), so every file
on disk has one banner and one header and can be read as ordinary CSV:

```python
import pandas as pd
df = pd.read_csv(path, comment="#")
```

If `ExperimentTarget` names a feature that's missing, disabled, or has an inert `Active`
setter, Highball logs a loud warning at startup — an A/B whose two arms are actually
identical would otherwise look, in the CSV, exactly like a genuine null result.

### Reading the headroom probe's verdict

The sleep headroom probe's columns end in `asleep,stationary,stationary_awake`. Its verdict
— also shown live in the UMM panel — classifies the share of tracked cars that are
stationary but still awake, using a decision rule agreed before any data was collected:

| Stationary-and-awake share | Verdict | Meaning |
| --- | --- | --- |
| < 10% | `none` | PhysX is already putting parked cars to sleep on its own; forcing it is not a real lever. |
| 10–30% | `marginal` | Some headroom exists, but building a sleep feature is a judgment call, not an obvious win. |
| > 30% | `real` | A meaningful share of parked cars sit awake; a stationary-sleep feature is worth building. |

## Prior art

`RailroaderStockOptimizer` by thebikwirm is prior work in this same area, addressing
rolling-stock performance in Railroader.

## Requirements

- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)
- Railroader
