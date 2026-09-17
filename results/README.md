# results

Committed measurements. Every file here was written by `BenchmarkRunner` from a run of this
repository; nothing in this directory is edited by hand. The runner writes here when it is given
`-frameBudgetOutput results`, and falls back to `Application.persistentDataPath` otherwise, so
ad-hoc runs do not land in the repository by accident.

The measurement protocol these files follow is `docs/METHOD.md`. Read it before quoting a number.

## Superseded files

**`benchmark_BaselineSweep_20260917_044713*` and `benchmark_CrossingSweep_20260917_051816*` are
superseded and must not be combined with anything newer.** They were taken under the day-2 stepping
rule, where the simulation stepped only when an accumulator said real time had elapsed. Under that
rule a light configuration ran no step in most frames, so its frame time is not the cost of
simulating those agents, and runs executed differing step counts. Day 3 changed benchmark mode to
step exactly once per frame (`docs/METHOD.md` §3), which makes frame cost and step cost describe the
same work in every row.

They are kept because deleting measurements because a later method is better is how a results
directory stops being evidence. Every row carries `stepping_mode`, so the two generations are
distinguishable without reading this file, and the table generator refuses to mix rows that disagree
on the columns that make them comparable.

Each run produces two files that share a base name, `benchmark_<config>_<UTC timestamp>`:

## `benchmark_<config>_<stamp>.csv` — the summary

One row per **(agent count, technique combination, run)**. This is the file the README results
table is generated from.

A row is self-describing: it carries the machine and the build alongside the numbers, so it can be
read without this repository.

- **When and where**: `timestamp_utc`, `unity_version`, `scripting_backend`, `render_pipeline`,
  `build_type` (Player or Editor), `is_batchmode`, `is_development_build`, `platform`,
  `operating_system`, `device_model`, `cpu`, `cpu_cores`, `cpu_frequency_mhz`, `system_memory_mb`,
  `gpu`, `gpu_api`, `gpu_memory_mb`, `screen`.
- **Proof the clamps were off**: `vsync_count` (0), `target_frame_rate` (-1), `run_in_background`
  (1), `display_refresh_hz`. A run that cannot achieve these aborts instead of writing a row.
- **What was run**: `config`, `agent_count`, `run`, `techniques` and one column per technique flag,
  `seed`, `fixed_timestep_s`, `max_steps_per_frame`, `warmup_frames`, `measured_frames`.
- **What the simulation actually did**: `steps_in_window` (simulation steps the step statistics rest
  on), `sim_steps_total` (steps since the spawn), `capped_frames` and `dropped_sim_seconds` (how far
  the simulation fell behind real time when it could not keep up).
- **The measurements**, each as a median and a 95th percentile over the measured frames:
  `frame_ms`, `main_thread_ms`, `sim_ms_per_frame`, `step_ms`, `present_ms`, `other_ms`,
  `gc_alloc_bytes`, `draw_calls`, `setpass_calls`.
- **Integrity**: `state_hash` (hash of the final agent state — two rows with the same seed and the
  same `sim_steps_total` must match) and `invalid_counters` (any profiler counter that failed to
  resolve; its columns are left empty rather than written as zero).

Two columns are easy to misread. `step_ms_*` is the cost of **one** simulation step and is what
answers "what does a step of N agents cost". `sim_ms_per_frame_*` is simulation time **inside a
frame**; when frames outrun the fixed timestep most frames run no step at all and its median is
legitimately `0`.

## `benchmark_<config>_<stamp>_frames.csv` — every measured frame

One row per measured frame, so the summary can be audited rather than trusted: `config`,
`agent_count`, `run`, `frame` (index within the run), `frame_ms`, `main_thread_ms`, `sim_ms`,
`steps`, `step_cap_hit`, `present_ms`, `other_ms`, `gc_alloc_bytes`, `draw_calls`,
`setpass_calls`. Warm-up frames are not in this file — they are discarded before measurement
begins.

Empty cells mean "not measured", never zero.
