# Frame Budget

A single-scene Unity benchmark that simulates a crowd of simple steering agents and measures what they cost. Over one week it gains a series of optimisation techniques, each toggleable at runtime and each measured against the same deliberately naive baseline. The measuring instrument shipped first and the improvements come after, so every number that appears in the results table below is a measurement taken by this repository, of this repository.

## What is here

- **The instrument.** Frame time is measured two ways, neither of which is `Time.deltaTime`: a `Stopwatch` interval between consecutive frame starts, and the profiler's main-thread counter. Simulation time is a `Stopwatch` and a `ProfilerMarker` around the fixed-timestep step, kept separate from total frame time. GC allocated per frame, draw calls and SetPass calls come from `ProfilerRecorder` counters whose names are verified to resolve at start-up; a counter that fails to resolve is reported as *n/a*, never as zero. Everything is reported as a median and a tail value (the ninety-fifth percentile) over a rolling window. There are no means anywhere: means hide spikes, and spikes are the point.
- **The naive baseline.** Agent state lives in parallel arrays of structs, not in per-agent MonoBehaviours. In benchmark mode the simulation runs exactly one fixed-timestep step per frame, so frame cost and step cost describe the same work in every row. Everything else is deliberately unoptimised and labelled as such in the code: an all-pairs neighbour query, one GameObject per agent, `GetComponent` inside the per-agent loop, LINQ in the hot path, and HUD text rebuilt by string concatenation every frame. Each of these is a control condition that a later technique removes.
- **The techniques.** Each is a runtime flag measured against its own control inside a single sweep, interleaved with it, so a comparison never spans a thermal ramp. A technique that alters the simulation's arithmetic must produce a **bit-identical** `state_hash` to its control at the same seed and step count: a faster neighbour query that returns a different set of neighbours is not an optimisation but a different simulation, and it fails flatteringly, so timing alone cannot be the acceptance test. Implemented so far: **spatialHash**, a uniform grid replacing the all-pairs scan. Still to come: zeroAlloc, tickBudget, gpuInstancing, burstJobs.
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

| Agents | Techniques | Runs | Frame ms (median) | Frame ms (run spread) | Frame ms (p95) | Sim step ms (median) | GC collections / frame | GC alloc / frame | Draw calls | SetPass calls |
|-------:|------------|-----:|------------------:|:----------------------|---------------:|---------------------:|-----------------------:|-----------------:|-----------:|--------------:|
| 1,000 | spatialHash+zeroAlloc | 5 | 1.95 | 1.70 – 2.06 | 3.58 | 0.14 | 0.05 | 56.0 KB | 1,009 | 18 |
| 2,000 | spatialHash+zeroAlloc | 5 | 2.41 | 2.26 – 2.46 | 4.06 | 0.37 | 0.06 | 64.0 KB | 2,003 | 28 |
| 5,000 | spatialHash+zeroAlloc | 5 | 4.98 | 4.86 – 5.26 | 7.65 | 1.48 | 0.06 | 64.0 KB | 4,981 | 58 |
| 10,000 | spatialHash+zeroAlloc | 5 | 11.61 | 10.24 – 14.45 | 19.73 | 4.77 | 0.06 | 64.0 KB | 9,940 | 108 |
| 1,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 1.36 | 1.24 – 1.53 | 2.33 | 0.14 | 0.06 | 56.0 KB | 21 | 9 |
| 2,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 1.68 | 1.34 – 1.80 | 2.67 | 0.37 | 0.07 | 60.0 KB | 21 | 9 |
| 5,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 2.80 | 2.73 – 2.93 | 4.60 | 1.46 | 0.07 | 64.0 KB | 21 | 9 |
| 10,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 6.15 | 6.12 – 6.22 | 8.78 | 4.69 | 0.06 | 68.0 KB | 21 | 9 |

*Generated by `tools/update_readme_table.py` from `results/benchmark_InstancingSweep_20260917_103138.csv` — do not edit by hand.*

<!-- RESULTS_TABLE:END -->

"Median" is the median of the per-run medians and "run spread" is the minimum and maximum of those same per-run medians, so run-to-run variation is visible rather than averaged away. Machine, Unity version, scripting backend, render pipeline and the achieved frame-pacing settings for every row are recorded in the CSV alongside the numbers.

Three columns deserve a note.

**Allocation is measured as managed heap growth, not as cumulative bytes allocated.** The profiler's per-frame allocation counter does not exist in a release player, so `AllocationProbe` uses `GC.GetTotalMemory` instead, verified against a known allocation on every run. Unity's collector reclaims only when it collects, so between collections a rise in heap size is exactly the bytes allocated — and across a collection it is not, since the heap can shrink while a great deal was allocated. Frames in which a collection ran are therefore excluded from **GC alloc / frame**, which is why that cell is blank wherever allocation is heavy enough to collect in most frames. Blank means *not measurable in that row*, never zero.

**That is why GC collections / frame is the primary allocation metric here.** It is measured in every row regardless of how heavy the allocation is, and it counts the thing that actually costs frame time — a collection is a pause. Bytes per frame are the supporting detail, reported where they can be.

**Frame cost and step cost describe the same work in every row.** Benchmark mode runs exactly one fixed-timestep step per frame, so a row's frame time always includes exactly one simulation step plus presentation and rendering. (Interactive play still paces itself against real time; those rows are marked `realtime-accumulator-capped` in `stepping_mode` and are not results.)

### Where the frame budget is crossed

<!-- BUDGET_CROSSING:BEGIN -->

**spatialHash+zeroAlloc**

- **16.7 ms (60 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 11.61 ms.
- **33.3 ms (30 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 11.61 ms.

**spatialHash+zeroAlloc+gpuInstancing**

- **16.7 ms (60 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 6.15 ms.
- **33.3 ms (30 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 6.15 ms.

<!-- BUDGET_CROSSING:END -->

## Not in scope

No pathfinding (steering and goals only), no full DOTS/Entities conversion, no gameplay, no menus, no art, no custom shaders, no second scene, and no test suite beyond the single smoke test that proves the harness runs.

## License

MIT. See `LICENSE`.
