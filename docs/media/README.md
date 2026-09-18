# Media

Two files belong here. Until both exist, the image lines at the top of the root README stay inside
an HTML comment, so the first screen never renders a broken image.

Record from a **release player**, not the editor — the HUD shows `(Player)` or `(Editor)` in its
first line and a reader will check.

```
Builds/Windows64/FrameBudget.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720
```

## `10000-agents.png` — 1280 × 720, generated

**This one is not hand-captured.** The player renders it itself, so it can be regenerated whenever
the numbers move and the figures in it always come from a run anyone can repeat:

```
pwsh -File tools/capture_readme_shot.ps1
```

The script fails loudly if there is no build, and prints the panel figures the capture logged so
they can be checked against the results table in the root README before the image is committed.

It drives the player through `-frameBudgetCapture`, which spawns 10,000 agents from the configured
seed with all three techniques on, runs the **benchmark stepping rule** — one fixed step per frame,
so the panel reads `1 step/frame` and the frame time is the cost of simulating those agents rather
than the idle-mode figure — settles 240 frames so the 120-frame rolling window is genuinely full,
reads the back buffer after `WaitForEndOfFrame` so the IMGUI panel is included, and quits. Keyboard
automation was the obvious alternative and is not reliable: Unity frequently ignores synthesized
input, and a screenshot that silently captured the wrong technique state is worse than none.

What to expect in the figures it prints. Agent count, technique state, steps per frame and **draw
calls** should match the table exactly; draw calls are not thermally sensitive. **Frame time will
sit a little under the table median** — a single capture runs cold, while the table's figure is the
median of five interleaved runs on a machine that has been working. Settling longer does not fix
this, it overshoots: at 900 settle frames the same capture reads about 7 ms. **`GC collect/fr` reads
`0.00` where the table says 0.06**, and that is two different statistics rather than a
disagreement — the HUD column is the median of per-frame collection counts, which is zero whenever
fewer than half the frames collect, and its p95 column showing `1.00` is where the collections are
visible. The table's figure is a rate over the whole window.

## `instancing-toggle.gif` — 1280 × 720, under 10 seconds, under about 8 MB

One continuous take at **10,000 agents with `spatialHash` and `zeroAlloc` already on**, pressing
**3** to toggle `gpuInstancing`. Let the rolling window settle either side of the press, so both
states are readable rather than mid-transition.

Start from two techniques rather than none. With all three off, 10,000 agents costs about 671 ms a
frame — roughly 1.5 frames per second — and a clip of that does not read as *slow*, it reads as
*broken*: the graph barely updates, the agents jump rather than move, and a viewer assumes the
capture failed. Starting from a configuration that already runs smoothly keeps the before state
believable, and leaves one variable changing on screen.

**The draw-call collapse is the point**, from about 9,955 to about 63 while the frame time moves
comparatively little. That asymmetry is the interesting part: it is the clearest single frame of
evidence that this workload is CPU-bound in the simulation rather than in rendering. The technique
panel makes the cause visible — row 3 flips from a dim grey `OFF` to a bright green `ON` — so the
number and the reason for it are on screen together.

Keep it short. A loop that takes ten seconds to make its point will not be watched.

## When both exist

`10000-agents.png` is committed; `instancing-toggle.gif` is not recorded yet, which is why both
image lines are still commented out — uncommenting now would render one image and one broken one.

Once the GIF exists, delete the `<!--` and `-->` around the two image lines at the top of
`README.md`. Nothing else needs changing.
