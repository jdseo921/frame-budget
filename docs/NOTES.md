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

> **Correction, 2026-09-18 (day 3).** Investigated. The expectation in the entry above was simply
> wrong, and the leading hypothesis — that dynamic batching was flushing and costing a SetPass per
> batch group — is also wrong, on two counts.
>
> First, dynamic batching was already disabled, and had been since day 1 (`m_DynamicBatching: 0`).
> So it could not have been causing anything.
>
> Second, the controlled test says SetPass does not depend on it at all. Building the same scene
> with dynamic batching on and off and measuring the same three agent counts:
>
> | Agents | Draw calls off → on | SetPass off → on | Batches off → on | Frame ms off → on |
> |---:|---:|---:|---:|---:|
> | 500 | 507 → 16 | 13 → 13 | 499 → 16 | 2.88 → 2.98 |
> | 2,000 | 1,979 → 31 | 28 → 28 | 1,928 → 31 | 29.36 → 27.58 |
> | 5,000 | 4,917 → 60 | 58 → 58 | 4,727 → 60 | 164.07 → 161.51 |
>
> Draw calls collapse by roughly thirty times and **SetPass does not move by a single call**.
>
> What the numbers do show is that SetPass tracks the number of *batch groups* the renderer forms,
> not the number of draw calls it issues. With batching on, the merged draw count (16 / 31 / 60) is
> almost exactly the SetPass count (13 / 28 / 58) at every size. The renderer forms the same groups
> either way; the batching flag only decides whether each group is submitted as one merged draw or
> as one draw per object, and the pass state is applied once per group in both cases. Since a group
> holds a bounded number of objects, more agents means more groups means more SetPass — a straight
> line, about one SetPass per 98 agents plus a constant of roughly eight, which is the HUD.
>
> What I have *not* established is the rule that bounds a group's size, which is not constant here
> (about 31 objects per group at 500 agents, about 83 at 5,000). It is not the dynamic-batching
> vertex budget, which would give a fixed number. I am leaving that unresolved rather than guessing,
> because the practical question it was blocking is now answered: SetPass is a well-behaved linear
> function of agent count and nothing about it is anomalous.
>
> **The finding that actually matters is the last column.** Collapsing draw calls thirty-fold
> changed frame time by less than two per cent, and the GPU frame time counter added today reads
> 0.32, 0.95 and 2.11 ms at 500, 2,000 and 5,000 agents against CPU frame times of 2.9, 29 and
> 164 ms. Rendering is nowhere near the ceiling; this workload is CPU-bound in the simulation by an
> enormous margin. That is a direct warning about the GPU instancing day: instancing will collapse
> the draw-call count impressively and, on this evidence, may move frame time hardly at all at these
> agent counts. Better to know that now than to discover it while writing that day's claim.

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

### Day 3 addendum: the re-baseline came out faster, and most of it was day 2's thermal drift

Stepping once per frame should not have changed the cost of a step — the work per step is identical,
only the decision of when steps happen changed. It changed anyway, by 6.7% at 2,000 agents and
11.7% at 10,000. That is well past "a few percent", so it needed an explanation before anything else
was built on top of it.

Most of it is not a speed-up at all; it is day 2 having been measured on a hotter machine. The
per-run step medians at 10,000 agents tell the story:

| | run 1 | run 2 | run 3 | run 4 | run 5 | spread |
|---|---:|---:|---:|---:|---:|---:|
| day 2 | 668.7 | 690.2 | 743.6 | 732.8 | 727.6 | 11.2% |
| day 3 | 642.7 | 642.8 | 641.6 | 647.0 | 661.6 | 3.1% |

Day 2's runs climb and then sit high; day 3's are flat. Comparing each sweep's *coldest* run rather
than its median — 668.7 against 641.6 — the gap is 4.1%, not 11.7%.

Why day 2 ran hotter is itself a consequence of the old stepping rule. Under it, light agent counts
did not step on most frames, so 500 agents rendered at about 780 frames per second, burning CPU flat
out between the occasional step. Every light point in the interleaved sweep was therefore a heating
element, and the heavy points that followed inherited the heat. One step per frame drops that to
about 330 frames per second at 500 agents, and the whole sweep runs cooler and flatter. The tighter
spread is a real improvement in the instrument, not a faster simulation.

That leaves roughly 4% unexplained by temperature. The likely cause is that extracting the neighbour
query into `BruteForceIndex.Query` changed the shape of the LINQ closure: the old code's lambda
captured `i` from the enclosing `for` loop and `pos` from the loop body, which needs two chained
compiler-generated display classes per agent per step, while the extracted method captures
everything in one scope and needs one. With gen-0 collections running at 14.5 per frame at 10,000
agents, halving a per-agent allocation is not a small lever. I have not isolated this — doing so
would mean resurrecting the deleted step function for a comparison run — so it is a leading
explanation and not a finding. It does not affect any claim made today, because every number in the
results table and every speed-up measured in half two comes from the current code measured against
itself.

### Day 3, half two: the spatial hash

**The speedup grows with agent count, but nothing like as fast as O(n²) → O(n·k) predicts, and the
reason is the world, not the grid.** Step cost improves 32.1× at 1,000 agents, 30.5× at 2,000,
48.2× at 5,000 and 58.2× at 10,000. Doubling the agent count should double the advantage if the
grid were genuinely linear against a quadratic baseline. It does not: from 1,000 to 2,000 the
speedup actually *falls* slightly (×0.95), and from 5,000 to 10,000 it grows ×1.21 against a
predicted ×2.0.

The textbook claim quietly assumes `k`, the number of agents in the searched neighbourhood, is
constant. Here it cannot be. The world is a fixed 200×200 square, so doubling the agent count
doubles the density, and a neighbourhood of fixed radius therefore holds twice as many agents.
`k` is proportional to `n`, which makes `O(n·k)` quadratic too. The grid is not changing the
complexity class at all — it is winning a large constant factor, by testing the agents in nine cells
instead of all of them, and the ratio of those two areas is what the speedup is really measuring.

What growth there is comes from the opposite direction: per-query fixed costs. Allocating a `List`,
sorting it, and rebuilding the grid once per step are all charged whether the query finds one
neighbour or twenty, so at 1,000 agents — where a neighbourhood holds barely one other agent — they
dominate, and they amortise as density rises. That is why the curve rises from 32× to 58× rather
than staying flat.

This is worth knowing before day 5. If a future technique's claim rests on "the spatial hash made it
linear", that claim is false in this benchmark's geometry. Making it actually linear would mean
growing the world with the agent count, which would be a different experiment, and changing the
workload mid-week to make a number look better is exactly what this project exists not to do.

**Ten thousand agents at sixty frames per second is very nearly reached, and "very nearly" is the
honest word.** With the hash, 10,000 agents measures 16.81 ms — against a 16.67 ms budget. It misses
by 0.14 ms, about one per cent. The temptation to describe that as "10,000 agents at 60 fps" should
be resisted until a later technique actually puts it under the line.

**The bit-identical check passed at every agent count**, which is what makes the speedup claim
usable: 1,000 / 2,000 / 5,000 / 10,000 agents all produce the same `state_hash` from both indexes at
360 steps. The two runs computed the same floating-point state; only the cost differed. Given a
58× improvement, that check is doing real work — an index that silently dropped neighbours would
look similar in the timing and would have failed here immediately.

**Collections roughly halved but did not disappear, which is the techniques staying separable.**
Gen-0 collections over 300 measured frames fall from 375 to 145 at 1,000 agents and from 4,448 to
2,014 at 10,000. The grid finds fewer neighbours so the `List` it returns is smaller, but it still
allocates one per query, and the brute-force control still runs its LINQ chain. That is deliberate:
if the grid had returned a pooled buffer, this sweep would have measured the spatial hash and the
allocation fix together and neither could have been attributed. Removing the allocation is day 4's
job, and the interaction between the two is worth more than either alone.

### Things I do not trust from today

> **Correction, 2026-09-18 (day 4).** "Not credible" was too strong, and the direction of the error
> was wrong. Checking the same sweep at *every* agent count rather than only the largest: at 1,000
> and 2,000 agents the two configurations report near-identical GPU times — 0.51 against 0.70 ms,
> and 0.96 against 0.94 ms — which is exactly what identical rendering should produce. The
> divergence appears only at 5,000 and 10,000 agents, which is precisely where the baseline's CPU
> frame stretches to 166 ms and 657 ms.
>
> So the counter is measuring correctly; what changes is the GPU. Idle for more than ninety-five per
> cent of a 657 ms frame, an integrated GPU drops into a low-power state and has to clock back up
> when work arrives, so identical draw calls genuinely take longer to execute. The number is true
> and the comparison is meaningless. The rule that follows is in METHOD.md section 9: `gpu_ms` may
> be quoted as evidence that the GPU has headroom, never as a delta between configurations whose
> CPU frame times differ by an order of magnitude. Inferred from existing data rather than isolated
> experimentally, but the prohibition holds whichever mechanism it is.

**The GPU frame-time counter is not credible at long frame times.** Rendering is identical between
the two arms — same agents, same material, same draw calls to within noise — so GPU time should be
the same. It is not: at 10,000 agents the baseline reports 20.5 ms of GPU time and the spatial hash
4.0 ms, and at 5,000 agents 11.8 ms against 1.6 ms. A five-fold difference in GPU work that does not
exist. The pattern tracks CPU frame time almost exactly, which suggests the counter is reporting an
interval spanning the whole frame — including the long stretches where the GPU is idle waiting for a
664 ms CPU frame — rather than GPU busy time. The conclusion drawn from it yesterday still stands,
because it was drawn in the regime where it is plausible: at small frame times the GPU reads a
fraction of a millisecond, far below the CPU, and collapsing draw calls thirtyfold moved frame time
by under two per cent. But the absolute numbers should not be quoted, and this needs understanding
before the instancing day leans on them.

**The first point of a sweep executes one step fewer than every later point.** It shows up as
`sim_steps_total` of 359 against 360 everywhere else, in both today's sweeps. Harmless to the timing,
but it means the first point cannot be hash-compared with the same configuration measured elsewhere,
which is why the 500-agent pair in the determinism check came back as "cannot compare" rather than
as a match. I have not tracked down the exact cause — it is somewhere in the ordering of the first
respawn against the first warm-up frame — and it should be fixed rather than documented forever.

### Not investigated today

The `Render / GPU Frame Time` counter exists in the release player and is not being collected.
Everything reported is CPU-side. Given that draw calls scale one-per-agent and SetPass calls are
behaving unexpectedly, knowing whether the GPU is anywhere near saturated would be worth having
before the instancing day — otherwise a CPU-side improvement could be claimed while the real
ceiling is elsewhere.
