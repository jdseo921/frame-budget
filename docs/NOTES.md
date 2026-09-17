# Notes

A running log of what surprised me and what I do not trust, written as the work happens rather
than reconstructed afterwards. Entries are dated and append-only: a claim that later turns out to
be wrong gets a correction underneath it, not a quiet edit.

---

## 2026-09-17 — Day 2: measurement protocol and the baseline sweep

### Surprises in the numbers

**The GC counter does not exist in a release player.** `GC Allocated In Frame` resolved fine in the
editor on day 1 and simply is not there in the IL2CPP release build. The player exposes 44 profiler
counters; the editor exposed 3,265. `GC Used Memory` and `GC Reserved Memory` survive, per-frame
allocation does not, because the instrumentation that produces it is stripped from non-development
builds. Every `gc_alloc_bytes_*` cell in today's results is empty as a result.

This is the day-1 decision to "log loudly rather than silently report zero" paying for itself: had
the harness written `0`, the naive baseline would have appeared allocation-free, which is the exact
opposite of the truth — it runs LINQ with a capturing lambda per agent per step. The whole point of
day 4 is to remove that allocation and measure the difference, so **day 4 is blocked until
allocation can be measured in a release build**. The likely fix is
`GC.GetTotalAllocatedBytes(precise: false)`, which is a runtime API rather than profiler
instrumentation and should survive stripping; sampling it at frame boundaries and differencing gives
per-frame allocation. That needs verifying in a player before it is trusted, and it is a day-4
prerequisite, not a day-2 fix. The fallback — measuring allocation in a development build — costs
comparability with every other number in the table and should be the second choice.

**The frame-time curve is discontinuous, and the discontinuity is mine, not the steering code's.**
I expected frame time to rise smoothly with agent count. It does not: 1,250 agents measures 4.62 ms
and 1,500 agents measures 17.88 ms, a near-quadrupling for a 20% increase in work. The cause is the
step cap of one fixed 60 Hz step per frame. Below the threshold the simulation keeps up, most frames
run no step at all, and the median frame is a frame that did no simulation work; above it every
frame is step-bound and the median is essentially the step cost. The two regimes are different
quantities wearing the same column heading.

Consequence I did not anticipate: **below about 1,500 agents, `frame_ms_median` is a mixture
statistic and should not be read as "what N agents cost"**. At 1,250 agents the five runs executed
83, 131, 135, 140 and 131 steps inside their 300-frame windows, and their frame medians moved with
that ratio (1.85, 5.20, 4.62, 5.45, 3.98 ms) rather than with the cost of the work. The median lands
on whichever side of the bimodal distribution holds more than half the frames, so it reports which
regime dominated, not how expensive the simulation was. `step_ms_median` is the stable and
meaningful column in that range, and it behaves exactly as expected: 6.6, 10.6, 15.5 ms at 1,000,
1,250 and 1,500 agents. The README now says this next to the table.

**1,500 agents sits precisely on the thermal stability boundary.** The most interesting number of
the day. Across the five interleaved runs at 1,500 agents, the count of capped frames went 38, 279,
292, 299, 300 out of 300 — monotonically, in run order — while step time rose 14.68 → 15.25 → 15.53
→ 15.67 ms against a 16.67 ms timestep. The first run, on a cold machine, mostly kept up with real
time. By the fifth it never did. Nothing about the workload changed; the machine got hot, the step
got slower, and it crossed the timestep.

So "the agent count at which the naive path first exceeds 16.7 ms" is not a fixed property of the
code on this machine. It is a property of the code, the machine *and its thermal state*, and near
the boundary the answer moves. The bracketed answer in the README (between 1,250 and 1,500) is
honest, but the 1,500 end of that bracket is the unstable one. This is also the clearest possible
justification for interleaving: had the five runs at 1,500 been taken back to back, the drift would
have been read as a property of 1,500 agents specifically, and any configuration measured later in
the session would have inherited a handicap.

**The release player is faster than the editor, but not uniformly.** Against day 1's editor
measurements: at 2,000 agents the editor reported about 44 ms and the release player about 30 ms; at
10,000 the editor reported about 971 ms and the player 734 ms. Roughly a quarter to a third of the
editor's frame time was editor overhead. Useful as a sanity check on the decision to exclude editor
numbers, and a reminder that the ratio is not constant, so editor numbers cannot be corrected by
scaling.

**SetPass calls grow with agent count and I cannot yet explain it.** Every agent shares one material
and one mesh, so I expected SetPass to be roughly constant. Instead it is 13 at 500 agents and 108
at 10,000 — one SetPass per 40 to 90 draw calls rather than one or two overall. Draw calls
themselves behave exactly as expected (about one per agent plus a handful for the HUD). I have not
investigated this and am not going to guess at it; recording it here so that it is not quietly
discovered later and mistaken for something a technique caused. It needs looking at before the GPU
instancing day, because that day's claim will rest on these two counters.

### Things about the harness I do not trust

**`other_ms` is a residual, not a measurement.** It is computed as
`frame − simulation − present`, so it absorbs rendering, engine work, and any error in the two
quantities subtracted from it. In its favour: across all 16,500 measured frames in today's two
sweeps it was never negative, which it would be if the sub-measurements were overlapping or
double-counting. That is a useful consistency check and I am recording it, but the column should
still be read as "everything else" rather than as a timed quantity.

**The warm-up count was reasoned, never validated.** 60 frames was chosen on day 1 by argument
about JIT, shader compilation and allocator settling, and has never been checked against data.
Comparing the first 30 measured frames with the last 30 of the same window, the heavy configurations
look settled (1,500 agents drifts +3.8% to −3.4%; 10,000 agents drifts +0.1% to +10.9%, which is
thermal rather than warm-up), but the light ones swing wildly (1,250 agents run 2 drifts +346%).
That swing is the bimodal-mixture problem above rather than incomplete warm-up, so I do not think
60 frames is wrong — but I have not demonstrated that it is right, and "the evidence is consistent
with it" is not the same claim. Plotting frame time against frame index across the warm-up window
would settle it.

**The reproducibility check is weakest exactly where the simulation is fastest.** `state_hash` is
only comparable between runs that executed the same number of steps, and the number of steps a run
executes depends on how much wall-clock time its measured window took. Today that gave 65 comparable
pairs, all matching — but every one of them came from configurations heavy enough for the step cap
to saturate and pin the step count. The 500-agent rows contributed zero pairs: all five runs
executed different step counts (32, 34, 37, 41, 42). So determinism is verified for the heavy half
of the range and merely unfalsified for the light half. A fixed-step-count mode would fix this, at
the cost of changing what the measured window measures.

**Two tray applications were running during both sweeps**, against `docs/METHOD.md` §6's "no other
applications": a Figma agent and a hotkey manager, both near-idle (roughly 56 s and 51 s of
cumulative CPU across the whole session). Discord and a VPN client were closed before the runs.
I judge the effect negligible but it is not zero, and the protocol says none, so it is recorded here
rather than quietly tolerated.

**Running the build mutates tracked project state.** `BuildBenchmark` sets the scripting backend,
compiler configuration and default window size, so `ProjectSettings.asset` changes as a side effect
of building. That was deliberate and the change is committed, but it means "run the build" is not a
read-only operation on the repository, and a future build with different settings would silently
rewrite them.

**The frame interval is measured Update-to-Update.** The stopwatch is read at the top of `Update`,
so an interval spans the previous frame's rendering and present as well as this frame's start. That
is the quantity I want — whole-frame cost — but it means the first measured frame after warm-up
inherits the tail of the last warm-up frame. One frame in 300; noted for completeness rather than
concern.

### Not investigated today

The `Render / GPU Frame Time` counter exists in the release player and is not being collected.
Everything reported is CPU-side. Given that draw calls scale one-per-agent and SetPass calls are
behaving unexpectedly, knowing whether the GPU is anywhere near saturated would be worth having
before the instancing day — otherwise a CPU-side improvement could be claimed while the real
ceiling is elsewhere.
