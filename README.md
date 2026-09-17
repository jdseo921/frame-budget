# Frame Budget

A single-scene Unity benchmark that simulates a crowd of simple steering agents and measures what they cost. Over one week it gains a series of optimisation techniques, each toggleable at runtime and each measured against the same deliberately naive baseline. The measuring instrument shipped first and the improvements come after, so every number that appears in the results table below is a measurement taken by this repository, of this repository.

## What is here

- **The instrument.** Frame time is measured two ways, neither of which is `Time.deltaTime`: a `Stopwatch` interval between consecutive frame starts, and the profiler's main-thread counter. Simulation time is a `Stopwatch` and a `ProfilerMarker` around the fixed-timestep step, kept separate from total frame time. GC allocated per frame, draw calls and SetPass calls come from `ProfilerRecorder` counters whose names are verified to resolve at start-up; a counter that fails to resolve is reported as *n/a*, never as zero. Everything is reported as a median and a tail value (the ninety-fifth percentile) over a rolling window. There are no means anywhere: means hide spikes, and spikes are the point.
- **The naive baseline.** Agent state lives in parallel arrays of structs, not in per-agent MonoBehaviours. The simulation steps on a fixed timestep accumulated from frame time, never on frame time directly, so the work done per step does not depend on the frame rate. Everything else is deliberately unoptimised and labelled as such in the code: an all-pairs neighbour query, one GameObject per agent, `GetComponent` inside the per-agent loop, LINQ in the hot path, and HUD text rebuilt by string concatenation every frame. Each of these is a control condition that a later technique removes.
- **The HUD.** IMGUI, sized to be readable in a screen recording, drawn in its own column beside the world view rather than over it: agent count, simulation time, frame time, GC per frame, draw calls, SetPass calls, and a rolling frame-time graph with the sixty-frames-per-second budget drawn across it.
- **The benchmark mode.** Sweeps agent counts from a `SimConfig` asset, discards warm-up frames, records the measured frames and writes two CSVs — a summary with medians and tails per (agent count, technique combination, run), and every measured frame so the summary can be audited. Runs of a configuration are interleaved rather than blocked, so thermal drift on a laptop spreads across configurations instead of landing on one. `-frameBudgetOutput <dir>` sends them into `results/`; without it they go to `Application.persistentDataPath`. Before measuring anything it asserts that vsync and the frame cap are off, and aborts the run rather than reporting numbers that describe the display.

## Running it

Open the project with the Unity version recorded in `ProjectSettings/ProjectVersion.txt`, open `Assets/FrameBudget/Scenes/FrameBudget.unity`, press Play. Change the agent count with the arrow and page keys or the on-screen controls; press **B** to start the benchmark from the assigned config, **R** to respawn, **H** to hide the HUD (which also shows you the HUD's own cost).

### Producing results

Results come from a release player, never the editor. Build one, then run it:

```
<path to Unity.exe> -batchmode -quit -projectPath <this repository> -executeMethod FrameBudget.EditorTools.BuildBenchmark.Build -frameBudgetBuildPath Builds/Windows64/FrameBudget.exe -logFile <log>
```

```
Builds/Windows64/FrameBudget.exe -frameBudgetBenchmark -frameBudgetConfig BaselineSweep -frameBudgetOutput results -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile <log>
```

The build requires the Windows IL2CPP module; it stops with installation instructions rather than silently producing a Mono player, because Mono and IL2CPP timings are not comparable. The CSV paths are printed to the log on completion, after which `tools/update_readme_table.py` regenerates the table above.

The same benchmark can also be run in the editor, which is useful for developing the harness and useless as evidence:

```
<path to Unity.exe> -batchmode -projectPath <this repository> -executeMethod FrameBudget.BenchmarkCli.Run -frameBudgetConfig Benchmark10k -logFile <log>
```

Two honesty notes about unattended runs:

- **`-batchmode` does not render.** There is no Game view, so draw calls and SetPass calls read zero and frame time excludes rendering entirely; the runner warns about this in the log and the CSV records `is_batchmode`. It measures the simulation, not the frame. For rendering-inclusive numbers run the same command **without** `-batchmode` (the editor opens, runs the sweep in Play mode, and exits by itself), or run a Player build with `-frameBudgetBenchmark -frameBudgetConfig <name>`.
- **Editor numbers are editor numbers.** Every row records whether it came from the editor or a player. Editor frames carry editor overhead; treat editor rows as relative, not absolute.

Reading the summary CSV: `step_ms_*` is the cost of one simulation step and is the column that answers "what does a step of N agents cost". `sim_ms_per_frame_*` is the simulation time spent in a frame; at low agent counts frames outrun the fixed timestep, most frames run no step, and its median is legitimately zero. `steps_in_window` tells you how many steps the step statistics rest on. `capped_frames` and `dropped_sim_seconds` are non-zero when the simulation could not keep up with real time. `state_hash` is a hash of the final agent state: two rows with the same seed and the same `sim_steps_total` must have the same hash, which is how the reproducibility claim is checked.

## Results

Every cell below is filled from the benchmark CSV by `tools/update_readme_table.py`. Nothing here is typed in by hand. The measurement protocol is `docs/METHOD.md`; the raw CSVs are in `results/`.

<!-- RESULTS_TABLE:BEGIN -->

| Agents | Techniques | Runs | Frame ms (median) | Frame ms (run spread) | Frame ms (p95) | Sim step ms (median) | Sim step ms (p95) | GC alloc / frame | Draw calls | SetPass calls |
|-------:|------------|-----:|------------------:|:----------------------|---------------:|---------------------:|--------------------:|-----------------:|-----------:|--------------:|
| 500 | baseline | 5 | 3.05 | 2.85 – 3.35 | 4.68 | 1.72 | 2.34 | 120.0 KB | 516 | 13 |
| 2,000 | baseline | 5 | 28.85 | 28.28 – 29.00 | 33.26 | 26.33 | 30.21 |  | 1,992 | 28 |
| 5,000 | baseline | 5 | 168.17 | 164.23 – 171.06 | 400.97 | 163.22 | 171.57 |  | 4,947 | 58 |
| 10,000 | baseline | 5 | 649.48 | 648.19 – 668.51 | 666.72 | 642.77 | 659.98 |  | 9,852 | 108 |

*Generated by `tools/update_readme_table.py` from `results/benchmark_BaselineSweep_20260917_075712.csv` — do not edit by hand.*

<!-- RESULTS_TABLE:END -->

"Median" is the median of the per-run medians and "run spread" is the minimum and maximum of those same per-run medians, so run-to-run variation is visible rather than averaged away. Machine, Unity version, scripting backend, render pipeline and the achieved frame-pacing settings for every row are recorded in the CSV alongside the numbers.

Two columns deserve a warning. **GC alloc / frame is empty**, and that means *not measured*, not zero: the `GC Allocated In Frame` profiler counter does not exist in a release player, which ships without the profiler instrumentation that produces it — the harness detects this at start-up, logs it as an error and leaves the cell blank rather than writing a zero that would be a lie. Measuring allocation in a release build needs a different mechanism, which is a day-4 problem. And **frame time is not a smooth function of agent count**: the simulation runs at most one fixed step per frame, so below roughly 1,500 agents most frames run no step at all and the median describes a frame that did no simulation work, while above it every frame is step-bound. That is the discontinuity visible between 1,250 and 1,500 agents, and it is a property of the step cap rather than of the steering code.

### Where the frame budget is crossed

<!-- BUDGET_CROSSING:BEGIN -->

- **16.7 ms (60 fps)** — crossed between **500 agents** (3.05 ms) and **2,000 agents** (28.85 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.
- **33.3 ms (30 fps)** — crossed between **2,000 agents** (28.85 ms) and **5,000 agents** (168.17 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.

<!-- BUDGET_CROSSING:END -->

## Not in scope

No pathfinding (steering and goals only), no full DOTS/Entities conversion, no gameplay, no menus, no art, no custom shaders, no second scene, and no test suite beyond the single smoke test that proves the harness runs.

## License

MIT. See `LICENSE`.
