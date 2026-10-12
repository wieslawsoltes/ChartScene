# Real catalogue previews and exact label-layout indexing

## Starting revision and scope

This continuation starts from merged/deployed main `ae6874a97bcf8e62ffb143a5737f43ba05e350d1`, tree `fe6af551a69add15079a3e6463733cb55f7e26b8`. There was no uncommitted local checkout; the mounted archives were historical evidence. The current main source archive was recovered and checked against that exact tree, and all 500 baseline core tests ran successfully using the recovered offline .NET SDK/dependencies.

This is an independent implementation, not a claim of complete SciChart WPF API or pixel parity. The public examples-suite documentation remains the comparison reference:
https://www.scichart.com/documentation/win/current/The%20SciChart%20WPF%20Examples%20Suite.html

## Visible UI improvement

The home catalogue now uses actual chart miniatures instead of repeated generic glyphs. Every miniature invokes the same sample factory and ChartRenderer used by the live examples. Titles, subtitles, legends and axis titles are omitted only inside the miniature so the plot remains readable; axes and data geometry still render normally. Cards retain their accessible example names, descriptions, and normal button activation.

The catalogue is lazy: no preview controls, sample factory calls or preview images are created until the catalogue opens. Paging creates at most twelve thumbnail controls at once; all 43 existing examples remain accessible in order. Changing page does not replace the active interactive model or its history. Responsive layout changes the card columns without changing thumbnail resolution or rebuilding cached sample data.

`ChartThumbnail` is a reusable Uno control with bindable Factory, Theme and ContentRevision. It constructs neither a ChartSurface/controller nor a timer. A changed or failed preview does not falsely report readiness. The owning page releases shared image entries when unloaded; a standalone control releases its private cache on unload.

## Image ownership and cache boundaries

`ChartThumbnailCache` is independently reusable in Rendering.Skia. It uses an LRU with both an entry limit and a retained pixel-payload budget (default 32 entries and 8 MiB). Factory identity, theme, pixel dimensions and caller content revision key the cache; in-place theme-palette edits are explicitly checked. Factory output must be a new exclusively owned model, which is disposed after building the thumbnail. Cached entries retain factory keys and SKImage pixels, not the created model, series snapshots or hit-testing geometry. A caller's factory closure can itself retain caller-owned objects.

Images above the retention budget are drawn once without being retained. Clear/Dispose release native images; use after disposal and factory reentrancy are rejected. Host canvas save/clip/transform state is restored and the caller's canvas is never disposed. The pixel budget is width × height × 4, **not a process-memory cap**: native image overhead, transient cold model construction, text resources and application data are outside it. New thumbnails use CPU raster rendering; the Uno host presents the resulting images. No new WebGPU backend or zero-copy claim is made.

## Exact data-label suppression

The old label path compared every candidate to every accepted rectangle. `LabelCollisionIndex` uses 32-pixel spatial cells as a broad phase, followed by the same inclusive rectangle intersection with the same candidate padding. It preserves source order, first-accepted priority, tied/contact edges and finite floating-point inflation arithmetic. Large labels, extreme coordinates or query areas exceeding a fixed cell budget fall back to testing stored rectangles rather than traversing an unbounded grid. An overflowing padded rectangle uses endpoint comparisons to avoid a NaN intersection test.

Each layout pass owns its index; no global structure retains chart data. The pathological worst case remains a linear overlap query, rather than a promised universal O(1) operation. Text formatting/shaping, rendering and label-provider execution are unchanged.

Core regression tests compare seeded rectangle streams and actual rendered label identities with independent scalar oracles. Existing direct/replayed scene pixel tests remain required. In the 6,000-label spaced fixture, exact comparisons decrease from **17,997,000 to 8,003**, with the same 6,000 accepted rectangles. `artifacts/performance/label-collisions.json` records CPU timings separately; those are not text-shaping, GPU, FPS or presentation measurements.

## Evidence

Core tests cover all 43 real thumbnail fixtures, cache hits and invalidation, palette edits, LRU order, entry/byte budgets, factory failures, reentrancy, canvas ownership, disposed-cache guards and collectible created models. Label tests cover randomized scalar equivalence, inclusive boundaries, extreme inputs, rounding, and renderer integration.

`tests/browser/validate-catalogue.py` drives the compiled Uno application through real input. It checks closed-catalogue laziness, page membership, all example previews and their actual pixels, revisited-page reuse, resized layouts, theme changes, navigation and memory-budget counters. It does not write diagnostic objects or inject successful images. Screenshots, result JSON and cache observations are retained alongside the mandatory existing workbench, gallery, cold-start, native GL/GLES and package-consumer gates.

Remaining work still includes chart-document persistence, complete custom series/annotations, exact WPF templates/typography, full source-editor/export-project workflows, broader native/accessibility qualification, incremental FIFO snapshots and physical-GPU performance comparisons. A thumbnail gallery is not a substitute for those APIs.
