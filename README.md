# Frame Budget

**Ten thousand steering agents, simulated and drawn, in 6.15 ms a frame — and the 16.7 ms budget holds to 18,000 agents.** The deliberately naive baseline this repository started from needs 670.95 ms for the same ten thousand. Three optimisations close that gap, each measured against its own control inside one interleaved sweep, and each verified to leave the simulation *bit-identical* to the baseline — so what changed is the cost and not the result.

A single-scene Unity benchmark that simulates a crowd of simple steering agents and measures what they cost. The measuring instrument shipped before the first optimisation did, so every number in the results table below is a measurement taken by this repository, of this repository, on a release build. Nothing in the table is typed in by hand.

## What is here

- **The instrument.** Frame time is measured two ways, neither of which is `Time.deltaTime`: a `Stopwatch` interval between consecutive frame starts, and the profiler's main-thread counter. Simulation time is a `Stopwatch` and a `ProfilerMarker` around the fixed-timestep step, kept separate from total frame time. GC allocated per frame, draw calls and SetPass calls come from `ProfilerRecorder` counters whose names are verified to resolve at start-up; a counter that fails to resolve is reported as *n/a*, never as zero. Everything is reported as a median and a tail value (the ninety-fifth percentile) over a rolling window. There are no means anywhere: means hide spikes, and spikes are the point.
- **The naive baseline.** Agent state lives in parallel arrays of structs, not in per-agent MonoBehaviours. In benchmark mode the simulation runs exactly one fixed-timestep step per frame, so frame cost and step cost describe the same work in every row. Everything else is deliberately unoptimised and labelled as such in the code: an all-pairs neighbour query, one GameObject per agent, `GetComponent` inside the per-agent loop, LINQ in the hot path, and HUD text rebuilt by string concatenation every frame. Each of these is a control condition that a later technique removes.
- **The techniques.** Each is a runtime flag measured against its own control inside a single sweep, interleaved with it, so a comparison never spans a thermal ramp. A technique that alters the simulation's arithmetic must produce a **bit-identical** `state_hash` to its control at the same seed and step count: a faster neighbour query that returns a different set of neighbours is not an optimisation but a different simulation, and it fails flatteringly, so timing alone cannot be the acceptance test. Three are implemented: **spatialHash** (a uniform grid replacing the all-pairs scan), **zeroAlloc** (pre-allocated neighbour buffers, no LINQ or closures in the step, and a HUD that formats digits into a reused buffer), and **gpuInstancing** (instanced draws issued straight from the position array instead of a GameObject per agent). Two are deliberately absent — see [Future work](#future-work).
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
| 1,000 | baseline | 5 | 9.05 | 8.17 – 10.19 | 11.52 | 7.29 | 0.72 | 256.0 KB | 1,010 | 18 |
| 2,000 | baseline | 5 | 31.78 | 29.59 – 35.70 | 35.30 | 28.83 | 1.61 |  | 1,983 | 28 |
| 5,000 | baseline | 5 | 174.24 | 171.91 – 179.26 | 504.04 | 168.70 | 4.89 |  | 4,912 | 58 |
| 10,000 | baseline | 5 | 670.95 | 668.96 – 744.32 | 687.63 | 664.30 | 10.95 |  | 9,818 | 108 |
| 1,000 | spatialHash | 5 | 1.76 | 1.74 – 1.81 | 2.33 | 0.22 | 0.36 | 124.0 KB | 1,013 | 18 |
| 2,000 | spatialHash | 5 | 2.89 | 2.79 – 2.90 | 3.30 | 0.88 | 0.79 | 324.0 KB | 2,007 | 28 |
| 5,000 | spatialHash | 5 | 6.71 | 6.55 – 6.83 | 8.47 | 3.24 | 2.29 |  | 4,988 | 58 |
| 10,000 | spatialHash | 5 | 16.68 | 16.02 – 16.84 | 20.98 | 10.94 | 5.71 |  | 9,940 | 108 |
| 1,000 | spatialHash+zeroAlloc | 10 | 1.70 | 1.60 – 2.06 | 2.40 | 0.13 | 0.06 | 52.0 KB | 1,011 | 18 |
| 2,000 | spatialHash+zeroAlloc | 10 | 2.29 | 2.22 – 2.46 | 2.92 | 0.36 | 0.06 | 52.0 KB | 2,003 | 28 |
| 5,000 | spatialHash+zeroAlloc | 10 | 4.86 | 4.49 – 5.26 | 7.11 | 1.45 | 0.06 | 64.0 KB | 4,985 | 58 |
| 10,000 | spatialHash+zeroAlloc | 10 | 10.24 | 9.59 – 14.45 | 14.31 | 4.71 | 0.06 | 64.0 KB | 9,942 | 108 |
| 1,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 1.36 | 1.24 – 1.53 | 2.33 | 0.14 | 0.06 | 56.0 KB | 21 | 9 |
| 2,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 1.68 | 1.34 – 1.80 | 2.67 | 0.37 | 0.07 | 60.0 KB | 21 | 9 |
| 5,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 2.80 | 2.73 – 2.93 | 4.60 | 1.46 | 0.07 | 64.0 KB | 21 | 9 |
| 10,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 6.15 | 6.12 – 6.22 | 8.78 | 4.69 | 0.06 | 68.0 KB | 21 | 9 |
| 12,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 7.18 | 7.08 – 7.52 | 10.13 | 5.88 | 0.08 | 68.0 KB | 21 | 9 |
| 14,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 9.36 | 9.16 – 9.48 | 12.37 | 7.86 | 0.08 | 68.0 KB | 21 | 9 |
| 16,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 11.60 | 11.37 – 11.86 | 14.73 | 10.16 | 0.07 | 68.0 KB | 21 | 9 |
| 18,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 14.21 | 13.99 – 14.38 | 17.68 | 12.65 | 0.07 | 68.0 KB | 21 | 9 |
| 20,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 16.95 | 16.91 – 17.31 | 20.75 | 15.32 | 0.07 | 68.0 KB | 21 | 9 |
| 24,000 | spatialHash+zeroAlloc+gpuInstancing | 5 | 23.46 | 23.12 – 23.77 | 27.30 | 21.62 | 0.07 | 72.0 KB | 21 | 9 |
| 1,000 | zeroAlloc | 5 | 2.13 | 2.10 – 2.15 | 2.73 | 0.65 | 0.08 | 48.0 KB | 1,012 | 18 |
| 2,000 | zeroAlloc | 5 | 4.48 | 4.26 – 4.69 | 8.20 | 2.46 | 0.08 | 48.0 KB | 2,005 | 28 |
| 5,000 | zeroAlloc | 5 | 19.69 | 19.44 – 21.42 | 21.71 | 16.07 | 0.08 | 60.0 KB | 4,946 | 58 |
| 10,000 | zeroAlloc | 5 | 71.44 | 70.56 – 72.27 | 493.49 | 64.74 | 0.08 | 64.0 KB | 9,820 | 108 |

*Generated by `tools/update_readme_table.py` from `results/benchmark_TechniqueMatrix_20260917_095811.csv, results/benchmark_InstancingSweep_20260917_103138.csv, results/benchmark_BudgetCrossing_20260917_103556.csv` — do not edit by hand.*

<!-- RESULTS_TABLE:END -->

"Median" is the median of the per-run medians and "run spread" is the minimum and maximum of those same per-run medians, so run-to-run variation is visible rather than averaged away. Machine, Unity version, scripting backend, render pipeline and the achieved frame-pacing settings for every row are recorded in the CSV alongside the numbers.

Three columns deserve a note.

**Allocation is measured as managed heap growth, not as cumulative bytes allocated.** The profiler's per-frame allocation counter does not exist in a release player, so `AllocationProbe` uses `GC.GetTotalMemory` instead, verified against a known allocation on every run. Unity's collector reclaims only when it collects, so between collections a rise in heap size is exactly the bytes allocated — and across a collection it is not, since the heap can shrink while a great deal was allocated. Frames in which a collection ran are therefore excluded from **GC alloc / frame**, which is why that cell is blank wherever allocation is heavy enough to collect in most frames. Blank means *not measurable in that row*, never zero.

**Two p95 figures are much larger than their medians, and the cause is not the code.** `baseline` at 5,000 agents and `zeroAlloc` at 10,000 agents show tail values around 500 ms against medians of 174 ms and 71 ms. Tracing them in the per-frame CSVs: the affected frames are scattered through the measured window rather than clustered at its start, so this is not warm-up leaking past the discard; simulation and presentation time in those frames are entirely normal, and the whole excess sits in the `other` residual, on the main thread. The stalls arrive about every 1.56 seconds of wall-clock in both configurations, and each one pads its frame to a near-constant ~500 ms. They never appear in any configuration whose frames are shorter than about 50 ms, nor in `baseline` at 10,000 agents, whose frames already exceed 500 ms and so have nothing to pad. It is an environmental stall outside the benchmark — left in the data rather than filtered out, and the reason every headline figure in this repository is a median.

**`zeroAlloc` is not literally zero.** It reports 48–64 KB per frame, roughly constant across agent counts, so whatever remains is fixed per-frame overhead and not in the per-agent path — the known candidate being the one string the HUD must hand to IMGUI each time the panel's text changes, since IMGUI takes a `string` and cannot be given a builder. Those figures sit near the resolution limit of a heap-size counter that moves in allocator-block steps, so treat them as an upper bound rather than a measurement. Collections per frame, which fall from 10.95 to 0.07, are the firmer evidence.

**That is why GC collections / frame is the primary allocation metric here.** It is measured in every row regardless of how heavy the allocation is, and it counts the thing that actually costs frame time — a collection is a pause. Bytes per frame are the supporting detail, reported where they can be.

**Frame cost and step cost describe the same work in every row.** Benchmark mode runs exactly one fixed-timestep step per frame, so a row's frame time always includes exactly one simulation step plus presentation and rendering. (Interactive play still paces itself against real time; those rows are marked `realtime-accumulator-capped` in `stepping_mode` and are not results.)

### Where the frame budget is crossed

<!-- BUDGET_CROSSING:BEGIN -->

**baseline**

- **16.7 ms (60 fps)** — crossed between **1,000 agents** (9.05 ms) and **2,000 agents** (31.78 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.
- **33.3 ms (30 fps)** — crossed between **2,000 agents** (31.78 ms) and **5,000 agents** (174.24 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.

**spatialHash**

- **16.7 ms (60 fps)** — crossed between **5,000 agents** (6.71 ms) and **10,000 agents** (16.68 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.
- **33.3 ms (30 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 16.68 ms.

**spatialHash+zeroAlloc**

- **16.7 ms (60 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 10.24 ms.
- **33.3 ms (30 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 10.24 ms.

**spatialHash+zeroAlloc+gpuInstancing**

- **16.7 ms (60 fps)** — crossed between **18,000 agents** (14.21 ms) and **20,000 agents** (16.95 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.
- **33.3 ms (30 fps)** — not crossed at any measured agent count. The largest measured point, 24,000 agents, has a median frame time of 23.46 ms.

**zeroAlloc**

- **16.7 ms (60 fps)** — crossed between **2,000 agents** (4.48 ms) and **5,000 agents** (19.69 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.
- **33.3 ms (30 fps)** — crossed between **5,000 agents** (19.69 ms) and **10,000 agents** (71.44 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.

<!-- BUDGET_CROSSING:END -->

## Future work

Two techniques this project originally sketched are not implemented. The reasons are worth stating precisely, because "there was no time" and "it would have broken the thing that makes the other numbers trustworthy" are very different admissions.

**Tick budgeting** — updating a fraction of the agents each step rather than all of them — is absent because it cannot satisfy the acceptance criterion everything else here is held to. Every technique has to produce a bit-identical `state_hash` to its control, and that criterion is what turns "it got faster" into "the cost changed and the result did not". Time-slicing deliberately changes what the simulation computes: an agent updated every fourth step follows a different trajectory, and no amount of care makes those floats match. Measuring it honestly would need a different correctness framework altogether — bounding how far trajectories may diverge over how long, and deciding what divergence is acceptable for a crowd — and that framework is a larger piece of work than the optimisation it would license. Adding it under the current rules would have meant either a false equivalence claim or a silent exception to the one rule the project actually enforces.

**Burst parallelisation** is the obvious next step, and the groundwork is already done rather than merely intended: `com.unity.burst`, `com.unity.collections` and `com.unity.mathematics` have been in the manifest since day 1, agent state is already parallel arrays of structs rather than objects, and the step is already two-phase and order-independent — it reads the previous step's state and writes into a separate buffer, which is the shape `IJobParallelFor` wants. The honest reason it is absent is time. It would also be the first technique where bit-identical output does not come for free: parallel reduction of a float sum depends on partition order, so the separation accumulation would need the same ascending-index discipline the spatial hash already follows, applied across threads rather than within one.

The measurements say where the remaining cost is. At 18,000 agents the frame is 14.21 ms, of which the simulation step is 12.65 ms and presentation is 0.10 ms. Rendering is no longer worth attacking. The step is — and it is single-threaded on a sixteen-thread machine.

## Not in scope

No pathfinding (steering and goals only), no full DOTS/Entities conversion, no gameplay, no menus, no art, no custom shaders, no second scene, and no test suite beyond the single smoke test that proves the harness runs.

## License

MIT. See `LICENSE`.
