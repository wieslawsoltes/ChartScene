# Aligned plots, live cursors and linked-view UI

## Scope and public comparison target

This continuation completes PR #7's natural-margin negotiation and reviews its interaction with existing rendering, cursors and view lifetimes. The comparison target is the publicly documented SciChart grouping behavior: vertically stacked surfaces share left/right axis areas, and side-by-side surfaces share top/bottom areas.

- [SciChart groups for aligning axis sizes](https://www.scichart.com/documentation/win/current/SciChart%20Groups%20for%20aligning%20Axis%20Sizes.html)
- [Synchronizing multiple charts](https://www.scichart.com/documentation/win/current/Synchronizing%20Multiple%20Charts.html)

ChartScene's independent types use explicit physical directions: `PlotAlignment.Horizontal` shares left/right margins, `Vertical` shares top/bottom margins, and `Both` shares all four. This is not SciChart WPF binary or complete visual/API equivalence.

## Reusable layout layers

`PlotLayoutGroup` in Rendering owns only member reports, not charts, models, controls or graphics contexts. Each member reports **natural** margins measured without the group's previous reservations. Shared margins use the per-edge maximum. Reporting an identical value does not aggregate or notify peers, and removing the largest contributor lets the group shrink. Reentrant reports/removals are published in a subsequent pass.

`ChartLayout.Measure` accepts minimum per-view margins and reports `NaturalMargins` separately from its final `PlotArea`. Reservations too large for the view produce an empty in-bounds plot rather than fabricated off-screen geometry or a zero-sized coordinate transform. `ChartRenderer` and `SkiaChartSceneCache` pass these values through; margins are part of the scene-cache identity.

`ChartLayoutGroup` in Controls adapts those reports to sibling Uno `ChartSurface` instances. Registrations follow loading, unloading, direct visibility changes, model replacement and completed frame measurements. A view cannot be claimed by two layout groups simultaneously. Explicit `MinimumPlotMargins` remain distinct from group-derived margins. Dispose the group when its owning view permanently closes; it does not own the chart models or data.

```csharp
using ChartScene.Controls;
using ChartScene.Rendering;

var layout = new ChartLayoutGroup(PlotAlignment.Horizontal);
layout.Add(upperChart);
layout.Add(lowerChart);

// Reserve space in one view; aligned peers also make room for that reservation.
upperChart.MinimumPlotMargins = new PlotMargins(160, 0, 40, 0);
// Later release the explicit reservation without changing any data or axis range.
upperChart.MinimumPlotMargins = default;

// When closing the owning view:
layout.Dispose();
```

Sharing margins aligns both edges only when the corresponding outer sizes and origins agree. The adapter does not resize arbitrary unrelated windows or promise whole-layout alignment for differently sized containers. Current visibility membership observes the surface itself plus load/unload, not a generalized ancestor-visibility tracker.

## Cursor correctness during layout changes

Baseline Application run `38223543454` tested merge `80c85f00bf10e063d0a6c88f3a2aa90180928770`. It passed 232 of 234 browser checks. The two failures were linked-cursor handoff and resize reprojection. Preserved state showed equal plot edges but different canonical source/destination X values: the source pointer was interpreted against the new plot while the peer still used the value saved at the previous pointer event.

`ChartCursorSyncGroup.Refresh` now reads the active pointer through the **current source plot and axis transform**. The physical source cursor stays under the pointer. Its rollover hits are recomputed when layout, model revision or interpolation mode changes; peers receive the corresponding common-unit X and perpendicular plot fraction. This works when either source or destination receives the final layout first, without waiting for a second pointer event.

Source points outside the new plot clear group-owned peer overlays. Reentrant peer removal or group disposal cannot resurrect detached cursors. Membership arrays are rebuilt only for registration changes, not on every refresh. Warm identical refreshes skip hit rebuilding and view notification. The performance fixture checks 10,000 unchanged refreshes for zero managed allocations and zero notifications; recorded CPU elapsed time excludes input hit tests, drawing, GPU work and presentation.

## Linked-view presentation

The linked gallery keeps its actual compiled Uno controls and existing data/range tools. Its commands now use the same reusable vector buttons as the workbench. Both charts share the navy plot treatment in dark mode, switch together to light mode, and update the page background, status text and command styling. Active right-axis, reservation and visibility commands display their state. Collapsed controls are not reported as clickable diagnostic rectangles.

This improves consistency with the examples-workbench hierarchy; it does not assert pixel-identical SciChart themes, icons or typography. No proprietary logos or binaries are included.

## Validation and limits

`CursorLayoutTests` covers current-source reprojection across all combinations of rotated, flipped and logarithmic X axes; source range changes; margin exclusion; reentrant disposal; and the warm-allocation contract. Existing cursor, data, stack, Skia and GPU tests remain enabled.

The original linked-browser test sequence still runs first, unchanged in its coordinate tolerance and gesture timing. Additional `alignment_checks.py` scenarios then verify unequal-label alignment, disabling/re-enabling the group, shrinking labels, adding/removing a right axis, releasing explicit reservations, hiding/reloading a chart, stationary-cursor resizing, model replacement, warm scene reuse and themed screenshots. No diagnostic writes or fabricated pointer events are used to make alignment pass.

Final-head CI must pass core tests, both Uno builds, every existing browser suite, native GL/GLES fixtures, twelve generated packages and both isolated package-only consumers before merge/deployment. In this continuation local execution was unavailable; validation evidence comes from repository workflows. Artifact creation alone is not a passing result. Exact test counts and commit identity belong to the successful final run/PR record, not assumptions in this document.

Complete SciChart API/pixel parity, chart-document persistence, custom-series coverage, advanced data editors, accessibility/native-device qualification, incremental streaming indexes, dedicated WebGPU and zero-copy presentation remain separate work. The pinned Uno GL host still uses framebuffer readback, and CI software graphics drivers are not hardware-performance evidence.
