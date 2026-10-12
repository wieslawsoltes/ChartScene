# Interactive legends and navigation reentrancy

This continuation recovers PR #9 from its actual published source artifact, retains its original two commits, and completes dedicated tests, native control integration, lifecycle repairs and an overview interaction review. It does not establish complete SciChart WPF API, pixel or performance parity.

## Reusable legend contracts

`ChartLegendSource` in Charting observes series membership and display metadata without constructing chart data snapshots. `ChartLegendEntry` identities survive renaming, styling, ordering, selection filters and temporary exclusion. Removed entries detach and reject edits. The public collection is read-only and publishes granular changes. Reentrant model/filter/disposal callbacks are reconciled in another pass, rather than mutating a collection during its own notification.

`ChartLegend` in Controls consumes the projection through `ParentSurface` or an independent `Model`. Its default native checkboxes use the parent's undo history, or a separately bound `History`. Font properties, orientation, series filters, markers, checkboxes and row/marker templates are configurable. Default rows survive filtering and metadata updates; actual removal releases a cached row even when it was filtered out and therefore has no collection-removal notification. Unload releases observation and native drawing resources without disposing the user's model.

A custom `ItemTemplate` receives a `ChartLegendEntry`; a `MarkerTemplate` receives a `RenderableSeries`. The supplied compiled XAML example demonstrates live name and two-way visibility bindings. A custom template controls its own command policy: directly binding `IsVisible` does not implicitly record the default checkbox's undo command. Large legends are **not virtualized** and can be wrapped in a ScrollViewer.

`LegendMarkerRenderer` supplies series-specific vector swatches for all currently supported 2D series kinds. The built-in rendered legend now uses these swatches too and respects `IncludeInLegend`; excluding an entry does not hide the plotted series. No proprietary implementation or branding is included.

The **Legends** gallery (`?view=legends`) uses actual reusable controls and compiled templates, with five placements (right, bottom, left, top, inside), horizontal/vertical layouts, all/visible/selected filtering, style edits, ordering, source replacement, native keyboard interaction and shared visibility history. Navy panels, compact vector commands and coordinated light/dark colors follow the existing examples workbench. The Overview gallery now uses the same command styling rather than a separate button vocabulary.

Reference behavior: https://www.scichart.com/documentation/win/current/LegendModifier.html

## Navigator correctness

Twelve new regressions failed against the recovered source before the fix. A blanket internal-writing flag suppressed **external reentrant** axis edits raised inside synchronous property callbacks. An unfinished drag or pan could overwrite a newly chosen viewport, retain a stale capture, or record the callback's edit in the wrong undo command.

`AxisRangeNavigator` now consumes only its expected own notification and uses an operation generation to detect superseding writes. It checks between AutoRange and VisibleRange updates, preserves callback-supplied ranges and policies, abandons stale mouse-up/cancel work, and avoids recording a superseded operation. Tests cover range/auto policy/full-domain/scale/flip/base changes, earlier registered subscribers, cancellation callbacks, nested pan, recenter capture and disposal from notification. Ordinary drag, log-space, no-op and undo behavior remains covered by the original suite.

Separately, the failed main browser assertion for flipped overview dragging sampled minimum and maximum from two different asynchronous snapshots. The browser test now uses one captured state and acknowledges actual routed pointer completion. No tolerance or pixel threshold is loosened and no test code writes application state. The overview publishes its observation after pointer release; this does not change its interaction behavior.

## Work and allocation evidence

The legend projection ignores data-only notifications before touching UI state or membership structures. `LegendTests` measures the same 10,000 source notifications with and without a legend. Both include existing model/series event allocations; the assertion is **zero additional legend allocation**, zero data snapshot reads, zero membership passes and zero entry notifications. Reported output is `artifacts/performance/legend-observation.json`. This is not a claim that the entire chart update allocates nothing.

Initial all-new membership binding avoids repeated linear misses by checking an entry hash set before searching its current collection position. Arbitrary reordering and native layout still have their normal collection costs; no universal constant-time claim is made.

## Validation gates and limitations

Core coverage includes identity/lifecycle/filter invariants, reentrancy, a seeded scalar membership oracle, worker-thread data notifications, all supported marker types and raster output, plus the twelve before/after navigator regressions. The dedicated browser suite drives actual checkboxes, keyboard events, commands, compiled templates, all placements, theme changes, empty filtered results, data append and model replacement. It asserts row reuse and resource release from read-only observations and retains screenshots.

The complete workflow still requires both Uno targets, every original browser suite, no-input startup for every gallery, native GL/GLES fixtures, twelve packages and both isolated package-only consumers. These gates must pass on the final source revision before merge. Core test success alone does not establish native template or browser behavior.

Dependency-property helper annotations are matched to Uno's trim contract rather than suppressing the warning; a 3D focus helper now ignores a detached XamlRoot. Physical-GPU benchmarks, broad browser/platform qualification, all reference templates, legend virtualization, complete document persistence and complete reference-pixel comparisons remain outside this change.
