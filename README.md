# railroader-better-ae

Work toward a "Better Auto Engineer" mod for [Railroader](https://store.steampowered.com/app/1683150/Railroader/).

The motivating problem: running 12+ concurrent AI consists degrades framerate badly, on
hardware that should be nowhere near its limits. A secondary complaint is that the Auto
Engineer often *doesn't* re-plan when conditions change (unless auto-reroute is on), so it
may be paying a recurring planning cost for little benefit.

Built on [Unity Mod Manager](https://www.nexusmods.com/site/mods/21), not Railloader.

## Status

Pre-design. We are measuring before we build.

## `tools/AEProbe`

A throwaway, read-only diagnostic that answers one question: **how much frame time does
`AutoEngineerPlanner.UpdateTargets` actually cost?**

It records nothing about gameplay and writes nothing to game state. It patches two methods
with timing-only prefix/postfix pairs and appends an aggregate row every 2 seconds to
`%USERPROFILE%\AppData\LocalLow\Giraffe Lab LLC\Railroader\AEProbe.csv`.

Columns:

| Column | Meaning |
| --- | --- |
| `wall_clock` | Local time at flush |
| `window_s` | Length of this measurement window |
| `frames`, `avg_frame_ms`, `fps` | Frame budget context |
| `planners` | Distinct `AutoEngineerPlanner` instances seen in the window |
| `ut_calls`, `ut_calls_per_s` | `UpdateTargets` call volume |
| `ut_ms_per_s`, `ut_pct_frame` | Time spent in `UpdateTargets`, absolute and as a share of wall time |
| `tfd_*` | Same, for `GraphRouteSearchExtension.TryFindDistance` (pathfinding) |

`tfd` time is nested *inside* `ut` time. The two percentages overlap and must not be summed.

### Decision rule

Agreed before looking at any data:

| `ut_pct_frame` | Verdict |
| --- | --- |
| < 2% | Planning cost is not the problem. Build for correctness only. |
| 2–10% | Worth throttling, but a secondary feature. |
| > 10% | Distance/visibility LOD on planning is the headline feature. |

### Building

Requires Roslyn from VS 2019 Build Tools. No .NET Framework targeting pack is needed —
we compile directly against the Mono BCL that Railroader ships, which is the runtime the
mod actually runs on.

```powershell
.\tools\AEProbe\build.ps1 -Deploy
```

Override the install location with `-RailroaderDir`. Game assemblies are referenced from
the local install and are never committed to this repository.

`AEProbe.csproj` is kept for IDE use and needs the .NET Framework 4.8 developer pack;
`build.ps1` is the supported path and needs nothing extra.
