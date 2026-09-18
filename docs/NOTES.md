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
quantities subtracted from it. In its favor: across all 16,500 measured frames in today's two
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

That leaves roughly 4% unexplained by temperature. The likely cause is that extracting the neighbor
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

The textbook claim quietly assumes `k`, the number of agents in the searched neighborhood, is
constant. Here it cannot be. The world is a fixed 200×200 square, so doubling the agent count
doubles the density, and a neighborhood of fixed radius therefore holds twice as many agents.
`k` is proportional to `n`, which makes `O(n·k)` quadratic too. The grid is not changing the
complexity class at all — it is winning a large constant factor, by testing the agents in nine cells
instead of all of them, and the ratio of those two areas is what the speedup is really measuring.

What growth there is comes from the opposite direction: per-query fixed costs. Allocating a `List`,
sorting it, and rebuilding the grid once per step are all charged whether the query finds one
neighbor or twenty, so at 1,000 agents — where a neighborhood holds barely one other agent — they
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
58× improvement, that check is doing real work — an index that silently dropped neighbors would
look similar in the timing and would have failed here immediately.

**Collections roughly halved but did not disappear, which is the techniques staying separable.**
Gen-0 collections over 300 measured frames fall from 375 to 145 at 1,000 agents and from 4,448 to
2,014 at 10,000. The grid finds fewer neighbors so the `List` it returns is smaller, but it still
allocates one per query, and the brute-force control still runs its LINQ chain. That is deliberate:
if the grid had returned a pooled buffer, this sweep would have measured the spatial hash and the
allocation fix together and neither could have been attributed. Removing the allocation is day 4's
job, and the interaction between the two is worth more than either alone.

### Day 4: zeroAlloc, instancing, and a technique that was allocating

**The two techniques overlap almost completely, and the arithmetic is worth seeing.** At 10,000
agents the baseline frame is 670.95 ms. The spatial hash alone saves 654.27 ms; removing allocation
alone saves 599.51 ms. Those sum to 1,253.78 ms of saving against a frame that only contains 670.95,
and the two together actually save 661.11. Almost 593 ms — ninety-five per cent of the smaller win —
is the same milliseconds counted twice.

This is not a disappointment, it is the expected shape, and it is the reason the matrix was worth
running rather than assuming the deltas add. Both techniques attack the same quantity: the per-agent
neighbor query. The spatial hash makes the query examine a handful of candidates instead of ten
thousand; removing allocation makes each examination cheaper and stops the collector running. Once
the hash has cut the work by fifty-fold there is very little allocation left to remove, and once the
allocation is gone the scan is much cheaper to do exhaustively. Neither is worth much *after* the
other, and reporting "32× from one and 9× from the other" as though they compose would be a
straightforward lie about a 109× result.

The honest framing is that the techniques are substitutes for most of their value and complements
only at the margin — the combined 9.84 ms beats the better of the two alone (16.68 ms) by a real but
much smaller amount than either headline delta suggests.

**The matrix caught the zeroAlloc technique allocating.** The first run showed the brute-force path
holding 0.08 collections per frame at every agent count, and the grid path rising from 0.17 at 1,000
agents to 1.67 at 10,000, with bytes per frame scaling 102 KB to 725 KB. An allocation proportional
to the number of queries, sitting inside the technique whose entire claim is that it does not
allocate — and it would have shipped, because the configuration still looked fast and the collection
count was still far below the baseline's 11.

The two paths differ in one line: the grid sorts its gathered candidates, the brute-force scan is
already ascending. `Array.Sort` allocates on every call on this runtime. Replacing it with an
insertion sort over the buffer range removed the allocation *and* made the configuration
substantially faster — 10,000 agents went from 12.57 ms to 9.84 ms, and the step from 7.49 ms to
4.63 ms. A general-purpose sort was doing partitioning work on lists of about a dozen items.

What makes this the day's most useful lesson: the bug was invisible to every check except the one
that compared two cells of a matrix which differed by a single flag. Timing alone said the
configuration was fast. The equivalence tests passed, because the sort was correct. Only running the
full cross-product and noticing that one cell allocated where its neighbor did not exposed it.

**Instancing confirmed day 3's SetPass model rather than merely agreeing with it.** Day 3 concluded
SetPass counts renderer batch groups rather than draw calls, from an experiment that changed batching
without changing SetPass. Instancing is the converse experiment: collapse the batch groups and
SetPass must collapse too. It does — 9,472 batches to 21, and 108 SetPass calls to 9 — and, more
tellingly, it stops growing with agent count. Draw calls are 21 at every agent count from 1,000 to
24,000. A constant is a different kind of number from a small one.

> **Superseded, 18 September — see "Day 6: the instanced agents were never rasterized" below.** These
> counts come from a build whose `INSTANCING_ON` shader variant was stripped, so the agents were
> submitted and never drawn. The constant 21 was the tell, and I read it as the result. Draw calls do
> not stay constant: they rise with agent count, 36 at 1,000 agents to 104 at 24,000. What the
> paragraph concludes still holds — instancing collapses draw calls by two orders of magnitude,
> 9,950 to 63 at 10,000 agents, and SetPass stops tracking object count — but these numbers are wrong.

**The presentation cost that instancing removed was mostly not the position writes.** Present time
fell from 0.509 ms to 0.100 ms at 10,000 agents, so only about 0.4 ms of the 2.5 ms the technique
saved was in the loop it replaced. The rest was what the engine did afterwards with ten thousand
renderers: culling them, sorting them, and submitting them one at a time.

> **Re-measured, 18 September.** This one survives almost unchanged: 0.505 ms to 0.105 ms at 10,000
> agents on the fixed build, against 0.509 and 0.100 here. Present time is CPU-side work on the
> submitting thread, which a missing shader variant does not touch.

**Two sweeps of the same configuration disagreed by eighteen per cent, and the reason was heat.**
spatialHash+zeroAlloc at 10,000 agents measures 9.84 ms in the technique matrix and 11.61 ms in the
instancing sweep, which ran immediately after it on a machine that had been at full load for
thirty-five minutes. Within each sweep the interleaving makes the comparison sound; across sweeps it
does not. That is why the README headline states two measured numbers and their sources rather than
dividing them into a speed-up ratio — a ratio spanning those two sweeps would carry the thermal
difference silently inside it.

### Ship day: what the two p95 outliers turned out to be

Two rows had tails far out of line with everything else — `baseline` at 5,000 agents (median 174 ms,
p95 ~504 ms) and `zeroAlloc` at 10,000 agents (median 71 ms, p95 ~493 ms) — where every other row's
p95 sits within about 1.3× of its median. The per-frame CSVs are committed, so this was answerable
rather than guessable.

**It is not warm-up leaking past the discard count.** That was the hypothesis worth eliminating
first, because it would have been a harness bug requiring a re-run. The affected frames are at
indices like 73, 215, 225, 269 and 7, 23, 39, 69, 99 — scattered across the window, with exactly one
instance at index 0 across ten runs. Warm-up leakage would cluster at the start.

**It is not in our code.** Decomposing a stalled frame: simulation time is normal (171.7 ms against
a 171.9 ms median; 64.5 against 63.2), presentation is normal (0.57 ms against 0.44), and the entire
excess — 347 ms and 459 ms respectively — sits in `other`, the residual between frame time and the
two things we measure directly. `main_thread_ms` tracks `frame_ms`, so the profiler sees the same
stall on the same thread. GPU time is normal. It is not garbage collection either: the zeroAlloc
spike frames report ordinary byte deltas with no collection at all.

**It is periodic, and it pads rather than adds.** The interval between consecutive stalls has a
median of 1,564 ms in one configuration and 1,554 ms in the other — essentially identical despite
workloads differing by a factor of two and a half. And a stalled frame lands at a near-constant
total of ~500 ms in both cases: 174 + 335, or 71 + 436. It is not adding a fixed cost, it is
extending the frame to a fixed deadline.

**The distribution across configurations is the tell.** Stalls per second of measured time:
0.49 for baseline/5,000, 0.54 for zeroAlloc/10,000, and exactly zero everywhere else — including
25 seconds of spatialHash/10,000 and 15 seconds of the final configuration, where a 1.5-second
period should have produced ten or more. And zero for baseline/10,000 across 1,036 seconds of
measurement, where frames already take 670 ms and a pad to 500 ms would be invisible. So the
phenomenon only manifests in the band where frames are long enough to be caught but short enough to
be extended.

I have not identified the mechanism, and I am not going to name one I cannot demonstrate. What is
established is that it is environmental, outside the simulation and presentation, periodic in
wall-clock time, and invisible to the median. The data stays as measured; the README says so under
the table. This is also the clearest practical argument for the day-1 decision to report medians and
percentiles rather than means — a mean over baseline/5,000 would have been dragged upward by roughly
thirty per cent by an event that has nothing to do with the code.

**A gap this investigation exposed:** the per-frame CSV recorded `config` and `agent_count` but not
which technique combination a row belonged to, so in a sweep with four combinations its rows were
ambiguous. I could only separate them by knowing the interleave order and checking each block's
median against the summary. The column is now written; the files already committed predate it, and
results/README.md explains how to disambiguate them.

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


### Day 6: the instanced agents were never rasterized in a player build

The symptom was a screenshot. At 10,000 agents with all three techniques on, the world view was
black — not one agent — while the panel beside it reported 29 draw calls. Draws were going out and
nothing was coming back.

The A/B that isolated it took one build. In the **editor**, gpuInstancing on draws the agents
correctly: 26 draw calls against 1,017 for the naive path, cyan dots filling the viewport. In a
1280x720 **IL2CPP player**, the same scene, the same flags and the same material draw nothing at
all, and the counter still reports 29. Same code, opposite result, and the only difference is the
build.

That difference is the cause. A player build compiles only the shader variants its built-in
materials ask for, and everything else is stripped. The instanced material was being made at run
time —

    instancedMaterial = new Material(agentMaterial) { enableInstancing = true };

— which asks for `INSTANCING_ON` long after the build has decided what to compile. No material in
the build carried the flag, so the variant was never compiled, and `Graphics.RenderMeshInstanced`
submitted draws the player had no variant to execute. **The editor cannot show this**, because it
compiles variants on demand and strips nothing; the run-time flag is satisfied the moment it is set.
Every mechanism that would normally catch a rendering fault is absent here: the material is not
null, the mesh is not null, the bounds are correct, and nothing logs a warning. The fix is an asset,
`Assets/FrameBudget/Resources/AgentInstanced.mat`, carrying the flag at build time, since `Resources/`
is always included.

**Every counter this project records said the technique was working.** Draw calls moved, batches
moved, SetPass moved, frame time moved. A measurement harness built specifically to catch optimistic
claims reported a clean win for a configuration that was drawing nothing, for two days, and the only
thing that exposed it was looking at the screen.

#### What it did to the numbers

The honest summary: the published instancing figures were taken on a build that was not instancing,
and the CPU numbers barely moved when it was fixed.

| agents | frame ms before | after | gpu ms before | after | draws before | after |
|-------:|----------------:|------:|--------------:|------:|-------------:|------:|
|  1,000 | 1.36 | 1.21 | 0.48 | 0.28 | 21 | 36 |
| 10,000 | 6.15 | **5.95** | 0.99 | 0.85 | 21 | 63 |
| 18,000 | 14.21 | **14.61** | 0.27 | 1.62 | 21 | 87 |
| 24,000 | 23.46 | 23.88 | 0.31 | 2.06 | 21 | 104 |

The headline figure got *faster*, 6.15 ms to 5.95 ms, and the crossing points got slightly slower.
Both differences are inside the thermal band this project already documents between sweeps run at
different times, and neither is a consequence of drawing: day 3 established that this workload is
CPU-bound in the simulation by an enormous margin, so not drawing was never saving the CPU anything
worth measuring. The budget crossing is unchanged where it counts — 18,000 agents at 0/5 runs over
budget, 20,000 at 5/5 — so the headline survived re-measurement rather than being rescued by it.

The draw-call figure is the one that was properly wrong. "21 at every agent count from 1,000 to
24,000" was in the day-4 notes as the strongest single piece of evidence for the technique, and a
constant *is* a different kind of number from a small one — it was just a constant produced by
drawing nothing but the HUD. The real figure rises with agent count, 36 to 104, because Unity
re-batches instanced draws well below the 1023-instance API limit. Against 9,950 for the naive path
at 10,000 agents, 63 is still two orders of magnitude, so the claim was right and the number was not.

#### The tell was already in results/

Before the fix, GPU time for the instanced configuration was **flat at 0.27–0.31 ms from 12,000 to
24,000 agents, and lower than the 0.48 ms recorded at 1,000**. Doubling the instance count changed
nothing, because the only GPU work in those frames was the HUD. A counter that does not respond to
the quantity it measures is the signature, and it was sitting in a committed CSV for two days while I
read the frame-time column beside it.

After the fix it rises monotonically with agent count — 0.28, 0.31, 0.42, 0.85, 1.18, 1.47, 1.78,
2.06 ms from 1,000 to 24,000 — and stays between 9% and 23% of frame time throughout.

#### Does this change the day-3 conclusion about GPU clocks?

No, and it is worth being precise about why, because "the GPU column is believable now" invites the
wrong inference.

Day 3 flagged the baseline's 20.5 ms of GPU time as not credible for rendering work that the spatial
hash does in 4.0 ms, and concluded the counter reports an interval spanning the whole frame rather
than GPU busy time — the GPU idling, and clocking down, through the 95% of a 670 ms frame where the
CPU is still stepping the simulation. METHOD §9 turned that into a rule: never quote a GPU delta
between configurations whose CPU frame times differ by an order of magnitude.

That evidence is **untouched by this bug**. It comes from `baseline`, `spatialHash`, `zeroAlloc` and
`spatialHash+zeroAlloc` — none of which use instancing, none of which were ever stripped. At 10,000
agents they still read 20.32, 4.04, 4.04 and 4.00 ms of GPU time for identical rendering work. The
anomaly is exactly where it was and the rule still applies.

What the fix does is **remove a piece of corroboration that turned out to have a different cause**.
A flat, implausibly low GPU column in the instancing rows looked like more of the same
counter-is-unreliable story, and it was not: it was the counter faithfully reporting that nothing was
being drawn. Two unrelated faults were being read as one. The corrected instancing rows do not
re-test the clock theory either, because they never enter the regime that produces it — the longest
instanced frame measured is 23.88 ms, where the GPU is idle 91% of the time but the frame is two
orders of magnitude shorter than the baseline's. So: one fewer piece of evidence, the same
conclusion, and a GPU column that is now usable for the configuration that ships.

#### One caveat in today's table

`spatialHash+zeroAlloc` at each matrix agent count now shows **10 runs**, because the non-superseded
17 September technique matrix and the 18 September instancing sweep both measure that configuration
and the generator merges them. Those two sweeps ran on different builds — the second includes the
technique panel, which draws every frame in every configuration — and the generator's comparability
guard does not check the build, only CPU, GPU, backend, pipeline, stepping mode and window length.
The merge is visible as a wider spread on that row and nowhere else. The right fix is to re-run the
matrix on the current build so the whole table describes one program; it is not done yet.

#### What I would do differently

Every other claim in this repository is guarded. Determinism has `state_hash`, frame-rate clamps have
`RunGuard`, mixing incomparable rows has the generator's column check, and the allocation claim has a
self-test that runs in the player. The one claim with no guard at all was "the agents are on screen",
because that felt like something you would obviously notice — and it is not, when the instrument is
on the left and the world is on the right and you are reading the instrument. A check that sampled
the world viewport and asserted some minimum number of non-background pixels would have failed on the
first instancing run, in the player, where the editor could never have told me.
