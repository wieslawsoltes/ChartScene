# Shared stacked geometry and observable configuration review

This continuation extends the data-integrity review in PR #4. It adds a shared immutable stack engine, notification batching, repairs missing property notifications, and validates these paths in a compiled Uno gallery. It does not establish complete SciChart WPF API/pixel parity.

## Shared stack generations

`StackCalculator.Capture(model)` returns an immutable `StackedFrame`, keyed by the ordered visible stack members, their source snapshots, axis IDs, group IDs and stacking modes. The rendering, range and hit-testing engines consume this same generation. Each entry includes the exact data snapshot used to build its cumulative arrays; a newer data count is never combined with older cumulative buffers.

Warm captures inspect series identities and cached snapshots, not every data point. Title, theme, color, label and viewport changes reuse the cumulative arrays. Full-column stack extents are also cached; a bounded X query still filters the exact snapshot. Adding, removing, reordering or hiding stack members, changing group/mode/axes, or changing a source snapshot rebuilds the output.

The cache is weakly owned by the chart model and explicitly released on model disposal. An immutable `StackedFrame` held by a consumer retains that generation by design. Legacy `Calculate(model)` remains available and returns fresh writable copies so existing callers cannot mutate cached internal arrays. Model collection/property writes remain an owning-UI-thread operation; captures do not create an atomic transaction across independent data-series writers.

Percent totals are accumulated relative to the largest signed magnitude before dividing, avoiding overflow of `100 * value` or a raw sum. Equal finite values near `double.MaxValue`, and subnormal values, retain their expected 50/100-percent contributions. Positive and negative stacks remain separate, with original X grouping and NaN gaps preserved. Overflow of an absolute cumulative sum is explicitly diagnosed; a failed build does not publish a partial generation.

## Notification batching and configuration fixes

`ChartModel.SuspendUpdates()` coalesces `Changed` notifications across a model batch while `Revision` and property values stay current. Nested and out-of-order scope disposal, reentrant callbacks, concurrent data notifications and disposal with an outstanding batch are covered by tests. This is notification coalescing, not rollback or cross-series storage isolation.

```csharp
using (model.SuspendUpdates())
{
    foreach (var series in model.RenderableSeries)
        series.StackMode = StackMode.Percent;
    model.YAxis.AxisTitle = "Contribution (%)";
    model.YAxis.AutoRange = AutoRange.Always;
}
```

Stacking mode, vector color/length/scale and arrow styling, resampling, polar connection mode, donut ratio, contour levels, point labels, palette and data-label providers now notify owning charts instead of silently changing without a redraw. Model aspect configuration and polar-angle units also notify. Value validation precedes changes. Contour/point-label lists copy their input and expose read-only views, so caller array edits cannot bypass invalidation.

This does not claim that every axis/annotation/model property has been audited. The review is scoped to the listed settings and their dependent caches.

## Reproducible evidence

`StackCacheTests` exercises shared output across rendering/ranges/rollover, all cache-key mutations, source-snapshot and writable-copy isolation, seeded scalar accumulation, extreme percent values, absolute overflow recovery, concurrent readers, weak ownership, range caching, batched notifications and observable configuration.

`artifacts/performance/stack-cache.json` reports cold construction separately from 1,000 warm captures of three 100,000-point series. Deterministic assertions require one shared generation, zero source-sample visits during warm capture, and a small allocation budget. The output-array payload is 4,800,000 bytes; this excludes original source snapshots, dictionaries and object metadata. CPU elapsed times depend on the machine and are not GPU, FPS, rendering or presentation benchmarks.

The **Stacked Data** gallery (`?view=stacks`) demonstrates runtime absolute/percent switching, 64-sample Y edits, whole-data replacement, visibility, themes, navigation and tab reattachment. Its browser tests use actual pointer input, observe immutable diagnostics, assert visible pixels, and verify generation/revision/notification counts. Switching value units restores Y auto-range while retaining X navigation; a previous zoom must not leave percent data clipped inside an absolute-value range.

The complete application workflow still requires all existing browser galleries, the new stacked-data suite, core tests, native GL/GLES fixtures, twelve packages and both isolated package-only Uno builds before deployment. See `docs/validation.md` and `docs/reviews/data-integrity-performance.md` for broader review boundaries. Incremental FIFO stack maintenance, stacked-series resampling, complete series-specific interpolation, chart persistence and dedicated WebGPU rendering remain outside this change.
