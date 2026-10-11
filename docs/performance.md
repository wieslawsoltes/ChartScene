# Performance contracts and reproduction

These optimizations preserve chart data and rendering semantics rather than substituting smaller datasets. The target is responsive navigation with bounded work and correctly invalidated caches. Measurements below concern ChartScene's CPU implementation, not SciChart benchmarks, hardware-GPU throughput, FPS, input latency or end-to-end browser presentation.

## Indexed min/max queries

`MinMaxIndex` builds an immutable extrema pyramid in 32-sample blocks over a `SeriesSnapshot`. Every node stores the earliest minimum and maximum indices and whether any non-finite Y samples occur. A query accepts a subtree only when it lies within the requested source interval, is gap-free and projects into one pixel bucket. Otherwise it descends to smaller nodes, scanning only partial leaf blocks. Accepted blocks contribute first/minimum/maximum/last indices in original order. Equal extrema keep the earliest source index; every gap remains present.

Construction is O(N) and auxiliary storage is O(N/32). Queries depend on viewport buckets and gaps; they are not unconditionally O(log N). Dense NaN gaps or frequent bucket boundaries force additional refinement, intentionally preserving correctness. Small/narrow or unsorted inputs avoid index creation; unsorted data remains in insertion order. Logarithmic X uses uniform buckets in log space rather than raw values, with nearby-value logarithms evaluated without subtractive cancellation.

The index cache uses weak snapshot ownership and a lazily constructed per-channel index for concurrent readers. Expired snapshots are collectible; a FIFO/data change creates a new snapshot and therefore a new cache identity. An index does not accelerate its own initial build, and this implementation is not an incremental FIFO index.

### Measured work example

The reproducible one-million-sample, 800-pixel fixture produces 3,119 selected source indices. Its query scans **24,032 original samples**, accepts **4,177 nodes** and visits **9,863 nodes**, rather than scanning all one million samples. The pyramid arrays occupy **589,824 bytes**, excluding the original data snapshot and returned output. Those figures come from `artifacts/performance/resampling.json` and are deterministic for that fixture.

Timing JSON reports the build cost separately, followed by 21 warmed viewport queries compared with the prior production scalar implementation. Both outputs are compared exactly for each query. A sample local Linux .NET 10 run measured 3.18 ms construction, 4.32 ms scalar median and 2.92 ms indexed median; timings vary with runtime/hardware/load. Use the report retained for the tested revision rather than treating this example as a speed guarantee. Returned arrays still allocate about 25 KB per query; index lookup itself does not eliminate output storage.

## Unchanged viewport reuse

Each `ChartRenderer` privately caches selected indices by weak snapshot identity, Y channel, visible X range, X scale and viewport bucket count. It reuses those indices for cursor, palette-independent stroke, and Y-range redraws when the X viewport dimensions remain unchanged. Changing the actual plot width due to axis labels correctly invalidates the bucket-dependent entry. Source replacement is an identity change even if count/revision numbers match.

`ResamplingCacheHits` and `ResamplingScannedPoints` describe the last render. The latter excludes one-time index construction and must not be interpreted as total rendering work. Data projection, clipping, drawing, hit regions, text and presentation still have costs. Cache buffers are not returned to consumers and cannot be modified externally.

## Visible grids and contour reuse

`GridVisibility` binary-searches immutable rectilinear edges and visits only intersecting cells, including touching edge neighbors. The million-cell non-uniform fixture's narrow viewport visits fewer than 50 cells. Uniformity is cached weakly by grid identity. Heatmap color scales retain the separately cached full-data range rather than making colors jump when zooming.

Marching-squares evaluation avoids per-cell heap arrays and skips cells not crossing a level. Gaps remain omitted, and finite extreme-value saddle calculations avoid overflow. The contour cache stores one read-only geometry/level-set per live immutable grid, with a 16 MiB retained-result limit; oversized results are computed but not cached. Equal-value level lists reuse geometry, while changed levels or grids invalidate it. Palette, opacity and viewport changes do not recompute data-space contour segments.

The 160 × 120, ten-level fixture produces 5,151 segments. A warmed capture evaluates zero grid cells and allocates 104 bytes for its defensive level snapshot in the recorded run. The deterministic allocation gate is under 2 KB per warmed capture. Render projection and clipping of those segments are not part of the capture timing and still occur.

## Overview picture reuse

`ChartOverview` shares one Cartesian source series' data with a separately owned full-range preview model. It records the preview as an immutable `SKPicture`; only the selection overlay and grips change during viewport navigation. Source data/style, theme, scale/orientation or dimensions invalidate the picture. It is not a screenshot or replacement dataset, and the picture is replayed by the host Skia canvas.

`PreviewBuildCount` makes this contract observable. The compiled-browser suite asserts unchanged builds and source revisions while dragging the million-sample viewport, then verifies invalidation on replacement/theme/resize. Streaming changes correctly invalidate the preview; no unchanged-data performance claim applies there. Unloading disposes picture/native resources and unsubscribes; reloading rebuilds them. The control currently previews one source, not arbitrary multi-series composites or WPF templates.

## Reproduce and assess

```sh
dotnet run --project tests/ChartScene.Tests -c Release
# artifacts/performance/resampling.json
# artifacts/performance/contours.json

# With the compiled Uno site served at this path:
python3 tests/browser/validate-overview.py --url http://127.0.0.1:8765/ChartScene/
# artifacts/browser-tests-overview/performance.json
```

Correctness, work bounds, weak ownership, isolation and allocation assertions are CI gates. Relative elapsed-time comparisons are retained evidence, not machine-dependent pass thresholds. GL/GLES rendering and package-consumer checks remain independently required. Warm cached results must never be presented as cold startup, GPU execution, streaming throughput or physical-display performance.
