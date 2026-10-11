# Static scene replay and browser input review

## Reusable display-list rendering

`SkiaChartSceneCache` records static chart content as an `SKPicture`, then replays it into the borrowed host canvas and draws the transient cursor, rollover and selection rectangle independently. This is a vector command list, not a screenshot at a fixed DPI. Data, observable chart configuration, model identity or logical size changes invalidate it. Host scaling can change without losing vector fidelity.

The key includes model revision, palette contents and series identifiers. Custom label/palette providers and explicit point selections bypass retention because their internal state can change without model notifications. A change during recording, including initial automatic range calculation, makes that recording ineligible for reuse. One picture is retained, with command/point-count admission limits; these are not a hard byte budget for referenced native image storage. Unload, model replacement, invalidation and disposal release the retained picture. The host canvas and graphics context are never retained or disposed.

The reusable `ChartRenderer.RenderInteraction` API draws just the interaction layer over a previously rendered frame. `ChartSurface` uses the display-list cache and exposes build/replay counters. Returned CPU timing covers the work actually performed by that call, not the initial frame's old recording duration. Logical command counts continue to describe displayed content, not GPU draw-call counts.

Axis labels and zoom-extents settings now notify owning charts. Annotation coordinate modes, layers, clipping and labels also notify; dash patterns are validated defensive copies. These changes prevent stale display lists when properties previously changed silently. Arbitrary state changes inside custom providers remain outside notification-based caching, hence the explicit bypass.

## Regression and performance evidence

`SceneCacheTests` compares exact pixels between direct rendering and recorded replay for every one of the 43 2D examples, including interaction overlays. A separate fixture compares 1x, 1.5x and 2x host scales. Other tests cover each invalidation input, cursor/selection clearing, custom-provider bypass, defensive data ownership and graphics lifetime.

`artifacts/performance/scene-replay.json` measures a moving-cursor, million-source-sample CPU Skia raster workload. The comparison renderer already uses indexed resampling. Both paths still rasterize the same chart, so allocation reduction need not translate to the same proportion of elapsed-time improvement. The JSON separates managed bytes per frame and CPU median duration. It does not measure physical-GPU execution, FPS or presentation latency. The deterministic regression requires one retained scene and lower managed allocation; universal timing thresholds are not imposed.

## Browser failure investigation

The preceding CI artifacts showed a stacked-chart screenshot captured during the loading presentation, a linked-checkbox state not yet changed after its synthetic click, and 3D popup keyboard sequences that did not establish the expected selection. Successful compilation/core tests alone did not establish browser success.

Optional `inputTrace=1` diagnostics observe managed routed pointer events with target IDs and coordinates. They do not handle events, move focus, record typed text, or alter model state. The last 64 observations stay in the local page only. Normal application URLs leave this disabled.

The test helper refreshes actual target bounds, moves the pointer and waits for observed managed delivery before sending one down/up pair. When an observed layout change relocates the toolbar, it repositions before pressing; it never repeats a click. The subsequent test checks the intended semantic result. A captured failing trace established why this matters: stale cursor-checkbox coordinates routed a move over the Fit button after initial layout changed. `input-actions.json` preserves repositioning and single-press counts. Popup tests observe actual open state and keyboard focus between keys. Initial stacked-chart screenshots are sampled for at most eight seconds and must meet the unchanged visible-geometry threshold twice consecutively, without forced rendering or injected wake-up input. The evidence is retained in `managed-input.json` and `presentation.json`.

Screenshot inspection after a green run also found white-on-white toolbar/status text in the dark Stacked Data page: the page had no explicit themed background. Its chrome now follows the chart background and muted foreground, and browser tests check text contrast in every toolbar button and the status line in both themes.

These changes make failures attributable; final browser workflow results, not the existence of these helpers, establish whether the implementation passes. All six galleries, native GL/GLES fixtures, package builds and isolated consumers remain required before merging/deployment. No failed assertion is downgraded to a warning.

## Remaining scope

This is a correctness/performance continuation, not full SciChart WPF parity. GPU command generation and presentation are still delegated to the existing Skia/Uno or GL hosts; no dedicated WebGPU backend or zero-copy compositor is introduced. Complete persistence, custom-series APIs, all hit-test variants, native/cross-browser accessibility and physical-GPU stress qualification remain open. See the other review documents and `docs/parity-status.md`.
