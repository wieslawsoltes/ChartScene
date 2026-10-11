# Data integrity, hit testing and graphics review

This continuation starts from PR #3's merged implementation. It reviews the shared data path, its consumers in resampling/rendering/interaction, and graphics-resource ownership. It is **not a claim that every SciChart WPF API, pixel, platform or performance boundary is complete**. Existing overview, color-legend, linked-chart and 3D functionality remains in place.

## Corrected data behavior

The ring buffer now tracks the exact number of decreasing adjacent X pairs. A point edit changes at most two such edges instead of scanning the entire X column. FIFO eviction subtracts the removed edges, so sortedness recovers when an old inversion leaves the buffer. Removing a prefix from sorted data does not scan the remaining series. Public snapshots reject invalid finite-value or sortedness metadata before binary search can rely on it.

Ring copies use at most two contiguous spans per channel. Typed XY, XYY, XYZ, vector, OHLC and box-plot appends pass stack-backed values rather than allocating one `params` array per sample. Bulk append validates the submitted batch and copies only the retained FIFO suffix when the incoming batch is larger than capacity. Snapshots still own their full column arrays: this is **not a zero-copy or incrementally paged snapshot implementation**.

OHLC and box-plot validation is dispatched from every mutation entry point, including calls made through `DataSeries`. Previously only the convenience append methods applied their structural checks. Invalid batches are validated completely before writes, and do not change the revision, publish an event or invalidate the cached snapshot. This library's explicit financial-gap policy accepts an entire NaN price/statistics tuple; it rejects partially missing tuples. That policy is tested here, not certified against every SciChart version.

Full-column snapshot extents are cached by channel and positive-only policy, including empty results. A bounded X-range query still computes its own result, and a new mutation creates a new snapshot/cache generation. Positive-only singleton extents remain strictly positive even for subnormal values; the old generic minimum padding could introduce zero/negative values into a logarithmic extent.

## New reusable mutation APIs

`DataSeries.UpdateRange(index, columns)` replaces an existing multi-channel interval without inserting or changing its length. `UpdateChannelRange(index, channel, values)` changes one channel while preserving the others. Both validate row invariants and X ordering before any write. `ReplaceAll(columns)` validates and atomically publishes a replacement rather than exposing an intermediate clear state. Replacement ordering is checked within the new data, not against the old final X value. FIFO replacement retains the newest capacity samples.

```csharp
var data = new XyDataSeries();
data.AppendRange(new[] { 0.0, 1.0, 2.0 }, new[] { 10.0, 20.0, 30.0 });

// Preserve X while replacing Y for two existing samples.
data.UpdateChannelRange(1, 1, new[] { 21.0, 31.0 });

// Replace all channels over an existing interval.
((DataSeries)data).UpdateRange(1,
    new[] { 1.0, 2.0 }, new[] { 22.0, 32.0 });

// One publication; readers never see a temporary empty series.
data.ReplaceAll(new[] { 0.0, 1.0 }, new[] { 100.0, 200.0 });
```

`XyDataSeries.Update(index, y)` and `UpdateRange(index, yValues)` provide Y-only convenience operations. Zero-length batches do not publish changes. `SuspendUpdates()` coalesces notifications; it does not convert a sequence of separate method calls into a rollback transaction. Notifications run after the storage lock is released, permitting reentrant snapshot reads or subsequent writes.

Input arrays/spans are caller-owned and must not be mutated concurrently before the synchronous operation returns. The API does not retain the input buffers. Snapshots are stable copies, and their read-only memory must not be mutated through unsafe casts.

## Hit-testing corrections and performance

For continuous series, non-interpolated queries select original data points; interpolated queries may hit segments. Digital lines/bands use their horizontal-then-vertical step geometry, rather than a phantom diagonal. Exact endpoints keep their point values. Cartesian line interpolation uses projected screen-space coordinates so logarithmic, rotated and flipped axes produce a hit on the rendered segment. Stacked rollover uses cumulative screen positions while reporting the raw contribution.

Duplicate-X groups are searched in full, choosing the closest screen point when X distance ties. Hidden and removed series are excluded, and stale frame bounds are not paired with edited or replaced data. Nonpositive values in logarithmic heatmap color scales are transparent and no longer remain hover targets. Pie totals that cannot produce a finite rendered geometry are not hit-tested as visible slices.

The spatial index is now lazy. Setting a rendered frame or requesting nearest-X rollover does not build thousands of screen buckets that the operation will never use. Bounds queries build the current frame's index once. Large query radii switch to a bounded scan of existing regions instead of enumerating billions of empty cells. Radius, position and region bounds are validated, and cell-span arithmetic is widened before subtraction/multiplication.

These changes do not complete every series-specific hit mode. Spline, polar, band-interior, custom-series and some stacked interpolation details still need a shared, reference-qualified geometry contract. Current selection APIs should not be described as complete SciChart hit-test equivalence.

## Graphics lifecycle and validation

The Skia font/shaper cache is capped between frames, alongside the existing paint, image and text budgets. Text blobs are cleared before their font entries. Resource lookups reject use after disposal, preventing a disposed cache from silently allocating fresh native objects. Image loading occurs before saving the host canvas state; image/text draws restore canvas state in `finally` blocks. Font/stroke values must survive conversion to finite positive native sizes.

`SkiaResources.CacheStatistics` exposes counts without returning native handles. These are between-frame retention budgets, not a guarantee that a single frame cannot request many distinct fonts or images.

Immutable 3D meshes compute `HasTransparency` once rather than scanning every vertex during camera frames. Normalized mesh values that overflow single-precision GPU attributes are rejected before upload. Both 2D heatmap and 3D surface indexers validate each coordinate independently, so an out-of-range column cannot alias a valid sample in the following row. Uniform factories reject invalid dimensions, nonfinite steps and null callbacks.

## Regression and benchmark evidence

The required core suite includes three new files:

| Test group | Coverage |
| --- | --- |
| `DataIntegrityTests` | 5,000 seeded mutations against a scalar list model; FIFO wrap/eviction; append/insert/update/remove/replacement; ordering recovery; financial invariants through all APIs; invalid-batch atomicity; concurrent snapshot consistency; typed allocation budgets; cached extents |
| `HitTestIntegrityTests` | Original points versus segments, digital steps in all orientations, logarithmic and stacked interpolation, duplicate-X ties, gaps, stale/hidden/removed targets, lazy indices and huge-radius bounds |
| `GraphicsIntegrityTests` | Native font-cache churn, disposal guards, balanced canvas state on failure, input validation, surface-coordinate bounds and immutable GPU metadata |

The existing full suite, native GL/GLES fixtures, all five compiled Uno browser galleries, twelve NuGet packages and both isolated package consumers remain required. No screenshot, tolerance, error assertion or deployment gate is disabled for this work.

The workflow retains measured JSON under `artifacts/performance/`:

- `data-mutations.json` compares the prior modulo/full-order-scan reference with the revised mutation path, verifies equal resulting columns, and reports CPU times and allocations separately.
- `hit-test-handoff.json` measures only frame handoff into the hit tester, with no eager spatial index construction.
- `mesh-metadata.json` measures immutable transparency-property reads, excluding buffer construction and rendering.

The legacy mutation comparator intentionally isolates the previous ring/order-scan operations; it is not a complete old-application benchmark. Cold construction, immutable snapshot copies, rendering, driver submission and presentation are outside those timings unless explicitly identified in the JSON. Typed-allocation budgets and query-work properties are deterministic checks; elapsed times are environment-dependent and are not gating universal speed promises.

## Remaining review priorities

A complete reference API/pixel comparison, versioned chart documents, data-point drag transactions, incremental FIFO extrema/snapshots, all series-specific hit geometry, broader accessibility, cross-browser/native-device testing and physical-GPU throughput remain open. Skia image creation still copies pixels, and the pinned Uno GL host still performs framebuffer readback. No dedicated WebGPU or zero-copy presentation path is introduced by this PR. Shared GL-state isolation, device/context-loss stress and long-duration mixed-workload memory behavior need broader qualification.

See `docs/parity-status.md`, `docs/performance.md` and `docs/validation.md` for the established baseline and evidence limits. A successful package or publish step alone is not a successful application run or deployment; inspect the final commit's full workflow and public deployment proof.
