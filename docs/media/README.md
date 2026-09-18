# Media

Two files belong here, and **both are generated from the shipped build rather than hand-captured**:

```
pwsh -File tools/capture_readme_shot.ps1     # docs/media/10000-agents.png
pwsh -File tools/capture_readme_clip.ps1     # docs/media/instancing-toggle.gif
```

Each runs the release player with capture flags and lets the app render its own image. That is
deliberate rather than convenient. A hand-captured screenshot has to be re-taken by hand every time
a number moves, nobody can check what state it was really in, and driving the app by synthesized
keystrokes is unreliable — Unity frequently ignores them, and an image that silently recorded the
wrong technique state is worse than no image. Both scripts print the panel figures their capture
logged, so the numbers can be checked against the results table before the image is committed.

Both run a **release player**, never the editor — the HUD prints `(Player)` or `(Editor)` in its
first line and a reader will check.

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

## `instancing-toggle.gif` — 1280 × 720, 60 frames at 10 fps, 6 seconds, generated

```
pwsh -File tools/capture_readme_clip.ps1
```

The player writes a numbered PNG sequence through `-frameBudgetClip` and the script assembles it
with ffmpeg, using the two-pass `palettegen`/`paletteuse` filter — a single pass quantizes to a
generic palette and puts visible banding across the agent field and dither noise through the panel
text. The sequence is an intermediate and is deleted afterwards; only the GIF is committed. If
ffmpeg is not on PATH the script says so, prints `winget install ffmpeg`, and leaves the PNG folder
in place, because ScreenToGif imports an image sequence directly. If the GIF comes out over 8 MB it
retries at 8 fps and then at 960 px wide, and reports which it used.

The clip runs at **10,000 agents with `spatialHash` and `zeroAlloc` already on**, settles 240 frames
so the rolling window is full, then enables `gpuInstancing` at capture frame 20 — two seconds of the
before state, the switch, four seconds after.

**The switch does not respawn.** Instancing changes how the same agents are submitted, so the field
has to be identical either side of the cut; that is what makes the point land. Measured rather than
assumed: the frame-to-frame difference across the cut is no larger than between any other pair of
neighboring frames.

Start from two techniques rather than none. With all three off, 10,000 agents costs about 671 ms a
frame — roughly 1.5 frames per second — and a clip of that does not read as *slow*, it reads as
*broken*: the graph barely updates, the agents jump rather than move, and a viewer assumes the
capture failed. Starting from a configuration that already runs smoothly keeps the before state
believable, and leaves one variable changing on screen.

**The draw-call collapse is the point**, from about 9,955 to 63 while the frame time moves
comparatively little. That asymmetry is the interesting part: it is the clearest single frame of
evidence that this workload is CPU-bound in the simulation rather than in rendering. The technique
panel makes the cause visible — row 3 flips from a dim gray `OFF` to a bright green `ON` — so the
number and the reason for it are on screen together. The counter itself takes a moment to fall,
because the panel reports a median over the last 120 frames rather than an instantaneous count.

## What the numbers in a capture mean

A readback is expensive, so a captured frame is not a representative frame. The frames that pay for
a capture are kept out of the rolling windows entirely (`FrameMetrics.DiscardNextSamples`), and the
capture interval is chosen so they are a small minority — at roughly 5.6 ms a frame the app runs
near 180 fps, so every 18th frame is a 10 fps clip at close to real-time speed and leaves about 94%
of frames untouched. Checked rather than assumed: with the clip running the same configuration
throughout, its final frame reports 7.72 ms against 7.61 ms for an uncaptured run of the same
length, a 1.4% difference.

Two figures will still not match the results table exactly, and both are expected. **Frame time**
drifts with thermal state — the clip's own two-second before-section at ~9.6 ms a frame warms the
machine, so the after-section reads a little above a cold run, and the table's figure is the median
of five interleaved runs. **`GC collect/fr` reads `0.00`** where the table says 0.06, because the
HUD column is the median of per-frame collection counts, which is zero whenever fewer than half the
frames collect; its p95 column showing `1.00` is where the collections are visible, and the table's
figure is a rate over the whole window. Agent count, technique state, steps per frame and draw
calls should match exactly.

## When to uncomment the images

Both files now exist. The two image lines at the top of `README.md` are still inside their HTML
comment; delete the `<!--` and `-->` around them to publish both at once. Nothing else needs
changing.
