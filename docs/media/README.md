# Media

Two files belong here. Until they exist, the image lines at the top of the root README stay inside
an HTML comment, so the first screen never renders a broken image.

Record both from a **release player**, not the editor — the HUD shows `(Player)` or `(Editor)` in its
first line and a reader will check.

```
Builds/Windows64/FrameBudget.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720
```

## `hero.png` — 1280 × 720

One still, at **10,000 agents with all three techniques on**, showing the instrument panel beside
the agent field. The panel must legibly show frame time, sim step, GC collections per frame and
draw calls, because those are the four numbers the headline table quotes.

Set it up with: `+1000` until the field reads 10000, then the technique toggles, then let the
rolling window fill for a few seconds so the medians settle before capturing.

## `toggle.gif` — 1280 × 720, under 10 seconds, under about 8 MB

One continuous take at 10,000 agents showing a technique being switched on and the frame-time graph
dropping — the graph redrawing below the 16.7 ms line is the whole story in one shot. The spatial
hash is the most dramatic single toggle (670 ms to about 17 ms); the instancing toggle is the one
where the draw-call counter falls from ~9,900 to 21, which is the more surprising number.

Keep it short. A loop that takes ten seconds to make its point will not be watched.

## When both exist

Delete the `<!--` and `-->` around the two image lines at the top of `README.md`. Nothing else
needs changing.
