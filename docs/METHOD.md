# Measurement method

This document is the protocol. It was written and committed **before the baseline sweep ran**, so
that the method could not be shaped around results that already existed. If a later day changes the
protocol, the change lands in its own commit, before the run it affects, with the reason stated —
never retroactively.

Everything below describes what the code in this repository actually does. The relevant types are
`BenchmarkRunner`, `FrameMetrics`, `RunGuard`, `RunEnvironment`, `FrameBudgetDriver` and
`NaiveSimulationStep`.

---

## 1. Where the numbers come from

**Results come from a release Windows x64 player build**, produced by
`Assets/Editor/BuildBenchmark.cs` with development build, deep profiling and profiler auto-connect
all off. Nothing else counts as a result.

**Editor numbers are excluded.** The editor is a different program from the player: it runs the
script assemblies under the editor's own Mono domain rather than the player's scripting backend, it
keeps the asset database, inspector and scene view alive alongside the game loop, it re-serialises
objects the player never touches, and its Game view renders through an editor-owned surface. In
practice that shows up as a per-frame floor — a cost that exists in every editor frame no matter
how little the simulation is doing — plus extra variance from editor housekeeping that has nothing
to do with the code under test. Editor runs remain useful for *developing* the harness and for
relative sanity checks, and every row records `build_type` so an editor row can never be mistaken
for a result, but no editor row is quoted as a measurement of the agent simulation.

**`-batchmode` rows measure simulation only and must never be quoted as frame time.** In batch mode
there is no Game view and nothing is rendered: `draw_calls` and `setpass_calls` read `0`, and the
frame interval excludes all rendering and presentation work. The runner logs a warning when it
detects batch mode, and `is_batchmode` is recorded in every row. A batch-mode row answers "what does
one simulation step of N agents cost"; it does not answer "does this hit 16.7 ms", because the
question includes rendering. The baseline sweep therefore runs the player **without** `-batchmode`.

Similarly `-nographics` is never used, for the same reason and more so.

## 2. Frame-rate clamps are removed, and the removal is proved

This is the single most important control in the harness, because a clamp does not look like an
error — it looks like a healthy benchmark that happens to report a round number.

With vsync on, a frame that finishes in 4 ms and a frame that finishes in 15 ms are both presented
on the next refresh and both measure about 16.7 ms on a 60 Hz display. Every agent count below the
clamp reports the same frame time, the curve is flat until it suddenly is not, and the benchmark has
measured the monitor. A positive `Application.targetFrameRate` does the same thing at a different
number. A window that is allowed to throttle when unfocused does the same thing intermittently,
which is worse because it is not even consistent.

`RunGuard.ApplyAndVerify()` therefore sets, at the start of every benchmark run:

- `QualitySettings.vSyncCount = 0`
- `Application.targetFrameRate = -1`
- `Application.runInBackground = true`

and then **reads all three back**. If any of them did not take — vsync non-zero, a positive frame
cap, or background throttling still enabled — `BenchmarkRunner.Start` aborts the run with an
explanatory error and a non-zero exit code, and writes no CSV. No numbers at all is the correct
outcome; numbers that describe the display are not.

The project's quality levels ship with `vSyncCount: 0` in every level, and
`Assets/Editor/BuildBenchmark.cs` sets `PlayerSettings.runInBackground` and a windowed default
resolution at build time, so the guard is confirming a configuration rather than fighting one.

The achieved values are written into every CSV row as `vsync_count`, `target_frame_rate`,
`run_in_background` and `display_refresh_hz`. A reader can confirm the clamps were off without
trusting this document: if frame times were pinned near `1000 / display_refresh_hz`, that would be
the signature of a clamp that escaped the guard.

## 3. Fixed timestep: the workload does not scale with the frame rate

The simulation advances in fixed increments of `SimConfig.fixedTimestep` (1/60 s). Each frame,
`FrameBudgetDriver.Update` adds `Time.unscaledDeltaTime` to an accumulator and runs whole steps
while the accumulator allows. Frame time decides **how many** steps run; it never decides how much
work a step does.

**A benchmark stepped on `deltaTime` measures itself.** If the per-step work were sized by the
frame's own duration, then a slow frame would produce a larger step, which costs more, which makes
the next frame slower — or, in the other direction, a fast machine would do less work per step and
look even faster. The workload becomes a function of the result, the feedback loop is positive, and
the measurement no longer has a fixed quantity to report. Worse, "agents simulated per second"
silently changes between configurations, so the before/after comparison the whole project exists to
make would be comparing two different amounts of work. Fixing the timestep pins the work per step
so that *only* the cost of doing it can vary.

`Time.unscaledDeltaTime` rather than `Time.deltaTime`, so that `Time.timeScale` cannot scale the
workload.

**Benchmark stepping: exactly one step per frame.** *(Changed on day 3. Results taken before that
change are not comparable with results taken after it, and `stepping_mode` in every row says which
rule produced it.)*

In benchmark mode the simulation runs **exactly one fixed-timestep step per frame,
unconditionally**, with no reference to wall-clock time. A benchmark wants a fixed amount of work
per frame so that frame cost and step cost describe the same thing in every row. Keeping up with
real time is a game's concern, and while it was in the measured path it distorted the results three
ways:

- Below roughly 1,500 agents the simulation kept up, so most frames ran **no step at all** and
  `frame_ms_median` described a frame that did no simulation work. The statistic tracked the ratio
  of stepping to non-stepping frames rather than the cost of the work.
- Above that, the accumulator's cap discarded simulated time by the minute, because the simulation
  could not keep pace with real time and was never going to.
- Runs of the same configuration executed **different numbers of steps**, so `state_hash` was
  comparable only where the cap saturated and pinned the count — that is, only where the simulation
  was slowest. Determinism went unverified exactly where it was cheapest to verify.

With one step per frame, a run of `measured_frames` frames executes exactly that many steps at every
agent count, so `state_hash` is comparable everywhere and `capped_frames` and `dropped_sim_seconds`
are always zero in benchmark rows.

**Interactive play keeps the accumulator**, because there it is the right behaviour: frame time
decides how many steps run, `SimConfig.maxStepsPerFrame` (default 1) bounds them so a slow frame
cannot spiral into an ever-longer one, and the simulated time the cap drops is counted into
`capped_frames` and `dropped_sim_seconds` rather than hidden. Those two columns remain meaningful
for interactive rows and are structurally zero for benchmark rows.

## 4. Warm-up

The first frames of any measured point are discarded: `SimConfig.warmupFrameCount` frames, of which
at least one is always discarded so that the spawn frame itself is never measured. The runner
enforces the minimum and warns if the configured value was raised.

What the warm-up is absorbing:

- **Code warm-up.** On Mono, methods are JIT-compiled on first call. On IL2CPP the code is
  ahead-of-time compiled, but the first execution still pays instruction-cache and branch-predictor
  cold costs, and generic and interface dispatch paths settle after a few calls.
- **Shader and pipeline warm-up.** The first frames that draw with a material compile or fetch
  shader variants and create pipeline state objects; that cost is real but it is a startup cost, not
  a per-frame cost, and charging it to the steady-state measurement would misrepresent both.
- **Allocator settling.** The managed heap grows to the working set the configuration needs during
  the first frames. Measuring then would attribute heap growth to steady-state allocation.
- **Spawn cost.** Every point respawns the world and rebuilds one GameObject per agent. That is a
  large one-off cost which has nothing to do with per-frame simulation.
- **Editor domain reload**, when the harness is being developed in the editor rather than measured
  in a player.

Each point additionally forces a full `GC.Collect()` before its warm-up begins, so that garbage from
the previous point is not charged to this one.

## 5. Statistics: median and 95th percentile, never a mean

Every reported figure is a **median** and a **95th percentile** over the measured window, computed
by nearest rank on the sorted samples — so a reported value is always an actual observed frame, not
an interpolation between two.

**No means appear anywhere in this project.** A mean is the wrong summary for frame time for two
reasons. First, frame-time distributions are asymmetric and heavy-tailed: a handful of very slow
frames drag the mean away from the value that describes typical behaviour, so the mean describes
neither the typical frame nor the bad frame. Second, and more importantly here, the spikes *are the
subject*. One of the later techniques exists specifically to remove garbage-collection spikes; a
mean would quietly absorb exactly the thing that technique is supposed to fix, and the before/after
comparison would understate it. The median says what a normal frame costs; the 95th percentile says
what the bad frames cost; together they say whether a change helped typical frames, tail frames, or
both.

**Five runs per data point.** One run of a configuration is an anecdote — it cannot distinguish the
configuration's cost from whatever else the machine was doing during those few seconds.

**Both the central value and the spread are reported.** The results table gives the **median of the
five run medians**, and alongside it the **minimum and maximum of those five run medians**. Hiding
run-to-run variation behind a single number would make noise look like signal; on day 3 onwards, a
technique whose improvement is smaller than the spread of the baseline it is compared against has
not been shown to do anything, and that has to be visible in the table rather than discoverable only
in the raw CSV.

Per-frame samples for every measured frame are written to the `_frames.csv` companion file, so the
summary can be audited instead of trusted.

## 6. Thermal drift, and the fact that this is a laptop

These measurements are taken on a laptop. That is worth stating plainly rather than hiding, because
a laptop is a thermally constrained machine: sustained load raises package temperature, and the CPU
and GPU reduce clocks to stay inside their power and thermal limits. A benchmark that runs for
several minutes will therefore tend to get slower as it proceeds, for reasons that have nothing to
do with the code being measured.

**Run conditions**, held constant for every measured sweep:

- Mains power connected, not battery.
- Windows power plan set to high performance.
- No other applications running; in particular no browser, video playback or game.
- The player runs windowed at a fixed 1280x720 so that the rendering cost is the same across runs
  and the resolution is recorded in the row.

**Interleaving is the part that actually defends against thermal drift, and it is implemented in the
runner, not merely described here.** If each configuration were measured five times in a row —
A A A A A, then B B B B B — then the machine would be coldest during A and hottest during E, and the
thermal ramp would map directly onto the configuration axis. Configuration E would look slower than
it is, by an amount nobody could separate from its real cost.

`BenchmarkRunner` therefore advances the agent count on every point and the run index only after a
full pass, producing

```
A B C D   A B C D   A B C D   A B C D   A B C D
```

rather than `A A A A A  B B B B B  ...`. Thermal drift then spreads across all configurations
roughly equally, and — because the five runs of a configuration are spaced far apart in time rather
than adjacent — any drift that remains shows up as *spread between the runs of a configuration*,
which §5 reports, instead of as a false difference *between configurations*.

This does not eliminate thermal effects; nothing short of a thermally controlled desktop would. It
converts them from a systematic bias into visible noise, which is the honest thing to do with an
error source that cannot be removed.

## 7. Reproducibility

The simulation is deterministic given its seed. `SimConfig.seed` (recorded in every row) drives a
small xorshift generator (`DeterministicRng`) rather than `System.Random`, so the sequence does not
depend on the platform or the runtime. Every agent's spawn position, initial velocity and goal comes
from that seed, and each agent carries its own stream so that goal re-rolls stay independent of
iteration order. The step itself is two-phase — steer from the previous step's state, then integrate
— so the result does not depend on the order agents are visited in, which keeps today's
single-threaded baseline comparable with the parallel versions arriving later in the week.

`AgentWorld.StateHash()` is an FNV-1a hash over the exact float bits of every agent's position and
velocity. It is recorded in each row as `state_hash`, next to `sim_steps_total` (steps executed since
the spawn).

**The check:** two rows with the same `seed`, the same `agent_count` and the same `sim_steps_total`
must have identical `state_hash`. If they do not, the simulation is not deterministic and every
before/after comparison in this repository is meaningless, because the configurations would not be
doing the same work.

Note what the check cannot be: rows are *not* expected to agree when `sim_steps_total` differs. How
many steps a run executes depends on how much wall-clock time elapsed during its measured frames, so
two runs of the same configuration ordinarily advance the simulation by different amounts. The check
is therefore applied within groups of equal step count, and the number of comparable pairs is
reported alongside the result rather than assumed.

## 8. The render pipeline is the built-in one, on purpose

This project uses Unity's **built-in render pipeline**, not URP. This was a deliberate choice made
before any measurement, and it is a measurement decision rather than a rendering one.

The naive baseline deliberately draws one `GameObject` with its own `MeshRenderer` per agent, and
writes each agent's position into its own `Transform` every frame. That is a control condition: one
of the week's techniques replaces it with instanced drawing straight from the position array, and
the point is to measure what that replacement is worth. URP's SRP Batcher would already be absorbing
a large part of that cost — it exists precisely to make many renderers sharing a shader cheap to
submit — so the "before" side of the comparison would arrive partly optimised by the engine, and the
measured improvement would understate the technique while overstating how naive the starting point
was. The built-in pipeline with dynamic batching disabled gives a baseline whose draw-call cost is
attributable to the code, which is what makes the before/after difference mean something.

`render_pipeline` is recorded in every row, so these numbers can never be silently compared against
numbers taken under a different pipeline.

**How the numbers would likely differ under URP:** the draw-call and SetPass counts for the naive
path would be substantially lower for the same agent count, because the SRP Batcher would merge the
per-renderer setup that the built-in pipeline issues individually — so the baseline's rendering cost
would look better, and the later GPU-instancing technique would appear to win less. The simulation
cost per step would be essentially unchanged, since it is CPU-side work that never touches the
pipeline.

This is not an oversight to be corrected later; the pipeline is not being switched mid-project,
because doing so would invalidate every comparison made before the switch.

## 9. What this benchmark does not measure

Stating the boundary is part of the method. This harness does **not** measure:

- **GPU time.** Everything reported is CPU-side: the frame interval, main-thread time, simulation
  time, and counts of draw and SetPass calls submitted. A `GPU Frame Time` counter exists on this
  Unity version but is not collected, and no claim about GPU cost is made anywhere in this
  repository.
- **Memory over time.** `gc_alloc_bytes` is allocation *per frame*; it is not heap size, not peak
  working set, and says nothing about fragmentation or about whether memory grows over a long
  session.
- **Load time or startup cost.** Scene load, shader warm-up and agent spawn are deliberately
  discarded as warm-up (§4). They are real costs and they are simply not what is being reported.
- **Mobile or thermally sustained behaviour.** A measured point lasts seconds, not hours. Nothing
  here describes how the workload behaves after ten minutes of sustained load, on a phone, or under
  a different thermal envelope.
- **Anything about a different machine.** Every row names the machine that produced it. The numbers
  are a property of this code *on this hardware*, and the useful quantity is the ratio between
  configurations measured under the same conditions, not the absolute milliseconds.
