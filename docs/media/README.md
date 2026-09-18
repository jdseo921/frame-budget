# Media

Two files belong here. Until they exist, the image lines at the top of the root README stay inside
an HTML comment, so the first screen never renders a broken image.

Record both from a **release player**, not the editor — the HUD shows `(Player)` or `(Editor)` in its
first line and a reader will check.

```
Builds/Windows64/FrameBudget.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720
```

## `10000-agents.png` — 1280 × 720

One still, at **10,000 agents with all three techniques on**, showing the instrument panel beside
the agent field. The panel must legibly show frame time, sim step, GC collections per frame and
draw calls, because those are the four numbers the headline table quotes.

Set it up with: `+1000` until the field reads 10000, then the technique toggles, then let the
rolling window fill for a few seconds so the medians settle before capturing.

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

Delete the `<!--` and `-->` around the two image lines at the top of `README.md`. Nothing else
needs changing.
