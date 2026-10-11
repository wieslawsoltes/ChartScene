# Compatibility status

The target is publicly documented SciChart WPF behavior. This is a capability inventory, **not a full-parity certificate**. ChartScene's regression tests validate its own tested behavior, not equivalence to an unexecuted reference application.

| Area | Current implementation | Remaining boundary |
| --- | --- | --- |
| Data and numerics | Columnar mutation, FIFO, snapshots, XY/XYZ/OHLC/statistical/heatmap types; resampling, splines, FFT, statistics and indicators | Complete generic data-series APIs, mutation invariants, filter/transform suite and extreme-value conformance |
| Axes and layout | Numeric/log/UTC date/timespan/category models, multiple axes, flips, grow-by, formatting and aspect constraints | Full category-date semantics, viewport providers, layout alignment, WPF bindings/templates and reference tick/label parity |
| 2D series | 29 kinds across Cartesian, polar, circular and ternary geometry | Additional variants, complete properties/styles, custom rendering contracts, labels and animations |
| Interaction | Pan, wheel/rectangle zoom, axis drag, cursor/rollover, selection, editable annotations, keyboard/touch, undo/redo | Exact curved/stacked/polar hit-test and interpolation coverage, full custom modifier behavior; data-point dragging is not included |
| Synchronization | Canonical-unit X range/cursor groups and reusable Uno lifecycle/model-replacement adapter | Cartesian cursors only; no arbitrary shared WPF mouse-event routing or complete synchronized layout groups |
| Annotations | Lines, boxes, text, arrows, paths, axis markers, retracements, layers and coordinate modes | Complete templating, composites, adornments, custom annotations and transactional editing surface |
| 3D | Normalized data, perspective/orthographic cameras, regular/nonuniform surfaces, meshes, scene caching and BVH picking; 10 rendered example kinds | Complete custom entities/markers/annotations/effects; no order-independent transparency or arbitrary-coordinate exactness claim |
| Controls | Uno 2D/3D surfaces, legend, linked-chart group and desktop/browser gallery | Overview/scrollbars, heatmap color legend, more editors, accessibility and full WPF style/template equivalence |
| Export and persistence | CPU offscreen PNG plus actual browser PNG/CSV downloads | Full chart-document serialization/save/load, XML/XAML interchange, PDF/SVG/XPS and printing |
| Packaging | 12 packages, isolated desktop/browser consumers | Requires successful consumer CI; package artifacts do not imply publication to NuGet.org |
| Rendering and performance | Skia, GL/GLES, viewport resampling, cached resources and persistent GPU geometry | No dedicated WebGPU, hardware benchmark parity, zero-copy compositor or complete extended stress/device-loss qualification |
| Visual fidelity | Independent ChartScene UI and artwork | Pixel-identical SciChart WPF themes, icons, layout and typography have not been established |

## Prioritized next work

Establish a version-pinned reference harness and API inventory; compare render output, axes, mutations and hit tests at multiple DPI settings. Extend missing series, filters, custom-rendering contracts and exact interpolation before declaring equivalence. Add reusable overview/scrollbars, color legends and aligned multi-chart layout. Implement bounded versioned chart documents with validated round trips and custom-provider registration. Expand native Windows/macOS, cross-browser, screen-reader, physical touch, shared-state, context-loss and real-hardware performance testing.

Browser diagnostic DTO serialization is deliberately separate from chart persistence. Successfully serializing observations is not chart save/load support.

## Public reference material

- [SciChart renderable series](https://www.scichart.com/documentation/win/current/What%20is%20a%20RenderableSeries.html)
- [Synchronizing charts](https://www.scichart.com/documentation/win/current/Synchronizing%20Multiple%20Charts.html)
- [ChartModifierBase API](https://www.scichart.com/documentation/win/current/Custom%20Modifiers%20-%20ChartModifierBase%20API.html)
- [Uno GLCanvasElement 6.7.135](https://github.com/unoplatform/uno/blob/6.7.135/src/AddIns/Uno.WinUI.Graphics3DGL/GLCanvasElement.cs)

These describe comparison targets and host contracts, not completed equivalence or copied proprietary implementation.
