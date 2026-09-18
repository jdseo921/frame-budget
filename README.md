# Frame Budget

A game running at 60 frames per second has **16.7 milliseconds** to do everything in a frame — think, move, draw. That is the budget. This project asks a simple question and answers it with measurements rather than opinions: *how many simulated characters fit inside that budget, and how much does each optimization actually buy you?*

It is a small Unity scene full of agents that steer toward a goal while pushing away from their neighbors — the crowd behavior behind a strategy game's units or a city's pedestrians. It starts from a deliberately slow version, then applies three well-known optimizations one at a time, measuring each one against the slow version it replaces. The point is not that the optimizations work; everyone knows they work. The point is *by how much*, under conditions careful enough that the numbers can be trusted.

**Ten thousand steering agents, simulated and drawn, in 6.15 ms a frame — and the 16.7 ms budget holds to 18,000 agents.** The naive baseline needs 670.95 ms for the same ten thousand. Each optimization is verified to leave the simulation *bit-identical* to the baseline, so what changed is the cost and not the result.

<!-- Media. Uncomment each line once the file exists in docs/media/ — a commented-out image
     never renders as a broken one, and this README is the first thing a stranger sees.
![Ten thousand agents at 6.15 ms, with the instrument panel showing frame time, step time, draw calls and collections](docs/media/hero.png)
![Toggling the spatial hash at runtime: frame time falls from 670 ms to 17 ms](docs/media/toggle.gif)
-->

<!-- HEADLINE_TABLE:BEGIN -->

| At 10,000 agents | `baseline` | `spatialHash+zeroAlloc+gpuInstancing` |
|---|---:|---:|
| Frame time (median) | 670.95 ms | **6.15 ms** |
| Simulation step (median) | 664.30 ms | 4.69 ms |
| Draw calls | 9,818 | 21 |
| GC collections / frame | 10.95 | 0.06 |

<!-- HEADLINE_TABLE:END -->

Every number on this page was measured by this repository, of this repository, on a release build. Nothing in any table is typed by hand — `tools/update_readme_table.py` regenerates them from the CSVs in `results/`.

## What is here

- **The instrument, built before the first optimization.** Frame time comes from a `Stopwatch` between frame starts and the profiler's main-thread counter — never `Time.deltaTime`. Simulation time is timed separately from the rest of the frame. Allocation, draw calls and SetPass calls come from counters verified to resolve at start-up; one that fails is reported as *n/a*, never as zero. Everything is a median and a 95th percentile. There are no means anywhere, because means hide spikes and spikes are the point.
- **The naive baseline.** Agent state lives in parallel arrays of structs rather than per-agent MonoBehaviours, but everything else is deliberately slow and labeled as such in the code: an all-pairs neighbor search, one GameObject per agent, `GetComponent` inside the loop, LINQ in the hot path, and HUD text rebuilt by string concatenation every frame. Each is a control that a later technique removes.
- **The three techniques**, each a runtime flag measured against its own control inside one interleaved sweep: **spatialHash** (a uniform grid instead of the all-pairs scan), **zeroAlloc** (reused neighbor buffers, no LINQ or closures, digits formatted into a reused builder), and **gpuInstancing** (instanced draws straight from the position array instead of a GameObject each). Two more are deliberately absent — see [Future work](#future-work).
- **The acceptance rule.** A technique that changes the simulation's arithmetic must produce a **bit-identical** `state_hash` to its control. A neighbor search that quietly returns fewer neighbors is faster *because* it does less, so timing alone cannot be the test — it would reward the bug.
- **The benchmark mode.** Sweeps agent counts and technique combinations from a config asset, discards warm-up frames, and writes two CSVs: a per-point summary and every measured frame, so the summary can be audited rather than trusted. Configurations are interleaved rather than run in blocks, so thermal drift on a laptop spreads across all of them instead of landing on one. Before measuring it asserts vsync and the frame cap are off, and aborts rather than report numbers that describe the display.

## Running it

Open the project with the Unity version in `ProjectSettings/ProjectVersion.txt`, open `Assets/FrameBudget/Scenes/FrameBudget.unity` and press Play.

Press **1**, **2**, **3** to toggle the three techniques and watch the frame-time graph move — each toggle respawns from the same seed, so before and after simulate the same agents. Arrow and page keys change the agent count, **B** runs a sweep, **R** respawns, **H** hides the HUD (which also shows what the HUD itself costs).

### Producing results

Results come from a release player, never the editor:

```
<Unity.exe> -batchmode -quit -projectPath <this repository> -executeMethod FrameBudget.EditorTools.BuildBenchmark.Build -frameBudgetBuildPath Builds/Windows64/FrameBudget.exe -logFile <log>
```

```
Builds/Windows64/FrameBudget.exe -frameBudgetBenchmark -frameBudgetConfig TechniqueMatrix -frameBudgetOutput results -screen-fullscreen 0 -screen-width 1280 -screen-height 720 -logFile <log>
```

The build needs the Windows IL2CPP module and stops with installation instructions rather than silently producing a Mono player, because Mono and IL2CPP timings are not comparable. CSV paths are printed to the log, after which `tools/update_readme_table.py` regenerates the tables here.

Two things not to misread: **`-batchmode` does not render**, so draw calls read zero and frame time excludes rendering — it measures the simulation, not the frame. And **editor numbers are editor numbers**; every row records which it came from. `results/README.md` explains the columns, and `docs/METHOD.md` is the measurement protocol.

## Results

The measurement protocol is `docs/METHOD.md`; the raw CSVs are in `results/`.

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

"Median" is the median of the per-run medians; "run spread" is the minimum and maximum of those same medians, so run-to-run variation stays visible instead of being averaged away. Machine, Unity version, scripting backend, render pipeline and the achieved frame-pacing settings are recorded in the CSV beside every row.

Three things in that table deserve a note.

**Allocation is measured as heap growth, not cumulative bytes.** The profiler's per-frame allocation counter does not exist in a release player, so the harness uses `GC.GetTotalMemory`, verified against a known allocation on every run. Memory is reclaimed only when the collector runs, so between collections a rise in heap size *is* the bytes allocated — and across a collection it is not. Frames in which a collection ran are therefore excluded from **GC alloc / frame**, which is why that cell is blank wherever allocation is heavy. Blank means *not measurable in that row*, never zero. **GC collections / frame** is the primary metric for that reason: it is measured in every row however heavy the allocation, and it counts the thing that actually costs frame time, because a collection is a pause.

**Two p95 figures are far above their medians, and the cause is not the code.** `baseline` at 5,000 agents and `zeroAlloc` at 10,000 show tails near 500 ms against medians of 174 ms and 71 ms. In the per-frame CSVs the affected frames are scattered through the window rather than clustered at its start, so this is not warm-up leaking past the discard; simulation and presentation times in those frames are normal and the whole excess sits in the unattributed residual. The stalls arrive about every 1.56 seconds of wall-clock in both configurations and each pads its frame to a near-constant ~500 ms, and they never appear in configurations whose frames are under about 50 ms. It is an environmental stall outside the benchmark, left in the data rather than filtered out — and the clearest reason every headline figure here is a median.

**`zeroAlloc` is not literally zero.** It reports 48–72 KB per frame, roughly constant across agent counts, so what remains is fixed per-frame overhead rather than anything per-agent — the known candidate being the one string IMGUI must be handed each time the HUD text changes. Those figures also sit near the resolution of a heap counter that moves in allocator-block steps, so read them as an upper bound. Collections per frame, falling from 10.95 to 0.06, are the firmer evidence.

### Where the frame budget is crossed

<!-- BUDGET_CROSSING:BEGIN -->

**`spatialHash+zeroAlloc+gpuInstancing`** — the configuration this project ships

- **16.7 ms (60 fps)** — crossed between **18,000 agents** (14.21 ms) and **20,000 agents** (16.95 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.
- **33.3 ms (30 fps)** — not crossed at any measured agent count. The largest measured point, 24,000 agents, has a median frame time of 23.46 ms.

**`baseline`** — the naive control, for contrast

- **16.7 ms (60 fps)** — crossed between **1,000 agents** (9.05 ms) and **2,000 agents** (31.78 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.
- **33.3 ms (30 fps)** — crossed between **2,000 agents** (31.78 ms) and **5,000 agents** (174.24 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.

<details>
<summary>All configurations</summary>

**`spatialHash`**

- **16.7 ms (60 fps)** — crossed between **5,000 agents** (6.71 ms) and **10,000 agents** (16.68 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.
- **33.3 ms (30 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 16.68 ms.

**`spatialHash+zeroAlloc`**

- **16.7 ms (60 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 10.24 ms.
- **33.3 ms (30 fps)** — not crossed at any measured agent count. The largest measured point, 10,000 agents, has a median frame time of 10.24 ms.

**`zeroAlloc`**

- **16.7 ms (60 fps)** — crossed between **2,000 agents** (4.48 ms) and **5,000 agents** (19.69 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.
- **33.3 ms (30 fps)** — crossed between **5,000 agents** (19.69 ms) and **10,000 agents** (71.44 ms). The sweep does not sample between those two counts, so the exact crossing point is bracketed, not measured.

</details>

<!-- BUDGET_CROSSING:END -->

## Future work

Two techniques originally sketched for this project are not implemented, and the two reasons are different in kind.

**Tick budgeting** — updating a fraction of the agents each step — cannot satisfy the acceptance rule everything else is held to. Time-slicing deliberately changes what the simulation computes: an agent updated every fourth step follows a different trajectory, and no amount of care makes those floats match. Measuring it honestly would need a different correctness framework — bounding how far trajectories may diverge over how long — which is a larger piece of work than the optimization itself. Adding it under the current rules would have meant either a false equivalence claim or a silent exception to the one rule this project actually enforces.

**Burst parallelisation** is the obvious next step and the groundwork is already there: the packages have been in the manifest since day 1, agent state is already parallel arrays of structs, and the step is already two-phase and order-independent — the shape `IJobParallelFor` wants. The honest reason it is absent is time. It would also be the first technique where bit-identical output is not free, since parallel float reduction depends on partition order.

The measurements say where the remaining cost is. At 18,000 agents the frame is 14.21 ms, of which the simulation step is 12.65 ms and presentation is 0.15 ms. Rendering is no longer worth attacking. The step is — and it is single-threaded on a sixteen-thread machine.

## Not in scope

No pathfinding (steering and goals only), no full DOTS/Entities conversion, no gameplay, menus, art, custom shaders or second scene, and no test suite beyond the equivalence tests that guard the neighbor search.

## Rights

This repository contains personal portfolio code for employment review. All rights are reserved by the author. No permission is granted for commercial reuse, redistribution, or modification.

The reservation covers the original work in this project: the C# under `Assets/`, the Python under `tools/`, the scene, the `SimConfig` assets and material, the documentation under `docs/`, and the measurements committed in `results/`. It does not extend to the Unity Engine or its packages, which are licensed separately by their owners and are not distributed here — nor to Unity's built-in cube mesh and Standard shader, which the scene references at runtime and which remain Unity's.
