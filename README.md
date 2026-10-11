# ChartScene

Independent scientific charting libraries for **Uno Platform**, desktop and WebAssembly. Numerical, chart-model, interaction and rendering packages can be reused independently of the gallery application. The browser application is compiled Uno/C#, not an HTML substitute for the controls.

ChartScene targets publicly documented SciChart WPF behavior, but **is not a drop-in SciChart replacement and does not establish full API, behavioral, visual or performance parity**. It is not affiliated with SciChart and contains no proprietary SciChart implementation or branding. See [compatibility status](docs/parity-status.md).

## Reusable packages

| Package | Responsibility |
| --- | --- |
| `ChartScene.Core` | Double-precision geometry, ranges, colors and observable infrastructure |
| `ChartScene.Data` | Columnar data, FIFO storage, versioned snapshots and XY/XYZ/OHLC/statistical/heatmap data |
| `ChartScene.Numerics` | Indexed resampling, interpolation, FFTs, statistics and financial indicators |
| `ChartScene.Charting` | Axes, series, themes, annotations, coordinate systems and stacking |
| `ChartScene.Rendering` | Drawing contracts, layout, 2D geometry and diagnostics |
| `ChartScene.Rendering.Skia` | Cached Skia rendering, shaped text and CPU offscreen PNG export |
| `ChartScene.Interaction` | Hit testing, modifiers, undo/redo, linked ranges and linked cursors |
| `ChartScene.ThreeD` | 3D data/models, cameras, normalization, mesh generation and spatial picking |
| `ChartScene.Rendering.OpenGL` | OpenGL 3.3 / GLES 3 renderer with persistent geometry buffers |
| `ChartScene.Controls` | Reusable Uno 2D/3D surfaces, legends, overview/scrollbars and synchronization adapter |
| `ChartScene.Samples` | 43 deterministic 2D examples and streaming signals |
| `ChartScene.Samples3D` | 10 deterministic 3D examples |

Controls target `net10.0-desktop` and `net10.0-browserwasm`; non-UI packages target `net10.0`. CI builds the actual application outside this repository using only the generated NuGet packages, with an isolated cache and local-only source mapping for `ChartScene.*`.

## Create a chart

```csharp
using ChartScene.Charting;
using ChartScene.Controls;
using ChartScene.Data;

var data = new XyDataSeries();
data.AppendRange(new[] { 0.0, 1.0, 2.0 }, new[] { 1.0, 3.0, 2.0 });
var model = new ChartModel { Title = "Measurement" };
model.RenderableSeries.Add(new FastLineRenderableSeries
{
    DataSeries = data,
    SeriesName = "Channel A"
});
var surface = new ChartSurface { Model = model };
// Add the surface to your Uno visual tree. Dispose the control when permanently
// closing it, and dispose the model when it is no longer shared by views.
```

For bounded streaming, set `FifoCapacity` and append batches. `SuspendUpdates()` coalesces notifications; `Snapshot()` returns a stable read-only copy cached until the next mutation. Unsorted X input must be explicitly enabled.

## Linked charts

The **Linked Charts** gallery displays seconds and milliseconds on separate surfaces. Both navigation and cursor overlays use canonical data units instead of replaying pixel gestures.

```csharp
var links = new ChartSynchronizationGroup("time");
links.Add(secondsSurface);
links.Add(millisecondsSurface,
    toCanonical: milliseconds => milliseconds / 1000,
    fromCanonical: seconds => seconds * 1000);
// Keep the group with the owning view, then dispose on final close.
// Registrations follow Loaded, Unloaded and Model replacement automatically.
```

`RangeSyncGroup` and `ChartCursorSyncGroup` can also be used without Uno. Linked cursors respect different plot sizes, visible ranges, logarithmic transforms and rotated/flipped axes. The perpendicular cursor coordinate is a plot fraction, not an assertion of equivalent Y units. Non-Cartesian cursor linking is explicitly rejected.

## Overview and independent scrollbars

The **Overview** gallery demonstrates a one-million-sample signal, a logarithmic sweep, a streaming FIFO and a non-uniform heatmap. The main chart and its miniature share source data rather than copying or replacing the dataset when navigating.

```csharp
using ChartScene.Core;
using Microsoft.UI.Xaml.Controls;

var overview = new ChartOverview
{
    ParentSurface = surface,
    Height = 125
};
var yScrollbar = new ChartScrollbar
{
    Axis = model.YAxis,
    FullRange = new DoubleRange(-5, 5),
    Orientation = Orientation.Vertical,
    History = surface.Controller.History
};
// Put these reusable controls beside/below the chart in your own layout.
```

Drag the selection body to pan or either grip to resize it. Shift-drag creates a range; clicking outside recenters it. Wheel and keyboard navigation share the parent's undo history. Logarithmic panning preserves ratios. Escape cancels a gesture, while external range changes supersede a stale gesture. `Source`, `Axis` and `FullRange` can also be bound independently of `ParentSurface`; `ChartScrollbar` uses the same engine without drawing a miniature.

The overview caches an immutable Skia picture. Moving the viewport redraws the selection without regenerating that picture; source, style, scale, theme and size changes invalidate it. An independent scrollbar without source-derived extents captures its initial axis range; set `FullRange` explicitly to choose another navigation domain. The current overview mirrors one Cartesian source series, not arbitrary WPF templates or an entire composite chart. Controls detach subscriptions and graphics resources on unload and rebuild on reload.

## Heatmap color legends

`HeatmapColorLegend` shares a `RenderableSeries` color map, range, opacity and linear/logarithmic color-scale transform with the actual heatmap. It supports horizontal, vertical and flipped layouts, formatted nonoverlapping labels, pointer inspection and source replacement. The **Color Legends** gallery shows two legends bound to one live-configurable heatmap.

```csharp
var legend = new HeatmapColorLegend { Series = heatmapSeries };
// Set heatmapSeries.ColorMapRange for fixed color limits, or null for data-derived limits.
```

## Performance contracts

The resampler uses weakly cached per-snapshot extrema pyramids, then caches unchanged viewport indices privately in each renderer. Duplicate X values, earliest tied extrema, endpoints and every NaN break retain their source indices. Logarithmic axes use logarithmic screen buckets. Non-uniform heatmaps visit only visible cells; contour geometry is cached separately from palette and viewport changes.

[Performance documentation](docs/performance.md) distinguishes index construction, warmed queries, allocations and actual browser cache reuse. Deterministic correctness/work assertions are required; timings are evidence rather than fragile CI speed thresholds. There is no claim that these CPU optimizations establish complete SciChart or hardware-GPU performance parity.

## Rendering

2D controls draw into Uno's Skia canvas; acceleration depends on the host. The 3D renderer issues real GL/GLES commands with depth-tested meshes, point markers and screen-space line quads. Camera movement reuses opaque geometry buffers; transparency sorting may update indices.

The pinned Uno GLCanvas reads the framebuffer into its compositor, so this is **not a zero-copy presentation pipeline**. There is no dedicated WebGPU backend. CPU render timing is not GPU execution time, FPS or end-to-end presentation latency. Mesa/SwiftShader CI validates correctness, not hardware-GPU performance parity.

The version-qualified Uno adapter arranges GL canvases before loading and selects RGBA readback/conversion in browsers. It checks its private-field contract explicitly; requalify the adapter when upgrading Uno. See [validation](docs/validation.md).

## Build and run

SDK and dependency settings are pinned in `global.json` and `Directory.Build.props`.

```sh
dotnet workload install wasm-tools --skip-manifest-update
dotnet run --project tests/ChartScene.Tests -c Release
dotnet build src/ChartScene.App -c Release -f net10.0-desktop
dotnet run --project src/ChartScene.App -c Release -f net10.0-desktop

dotnet publish src/ChartScene.App -c Release -f net10.0-browserwasm \
  -o artifacts/publish -p:WasmShellWebAppBasePath=./
python3 tools/stage-site.py
python3 -m http.server 8765 --directory artifacts/site
```

Open the served root for 2D, `?view=3d` for 3D, `?view=linked` for linked charts, `?view=colors` for color legends, or `?view=overview` for overview navigation. Use HTTP/HTTPS rather than `file://`.

The gallery includes example search, 29 2D series kinds, multiple numeric/log/date axes, annotations, selection, navigation history, a million-sample example, real-time FIFO streaming, themes and actual PNG/CSV downloads. The 3D gallery includes camera orbit/pan/zoom, projections, surface/grid modes, legends, picking and selection.

## Validation and deployment

[Application CI](.github/workflows/application.yml) builds both Uno targets, serves the exact publish output at `/ChartScene/`, tests all five galleries through real browser input, packages all 12 libraries and compiles isolated package-only consumers. A native EGL job renders every 3D fixture through both desktop GL and GLES.

Pages deployment is gated on successful application and GPU jobs on `main`. Artifact existence or a successful publish alone is not deployment or runtime validation. `build-info.json` identifies the staged source commit. See [validation evidence and limits](docs/validation.md) and [remaining compatibility scope](docs/parity-status.md).

## License

MIT; see [LICENSE](LICENSE). Third-party dependencies retain their own licenses. Familiar API names do not imply WPF binary compatibility.
