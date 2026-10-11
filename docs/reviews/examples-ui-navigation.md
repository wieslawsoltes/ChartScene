# Examples UI, navigation and search

This work follows the successfully validated and merged PR #4. It refines the real Uno application toward the structure of the public SciChart WPF examples browser. It is not a claim of pixel-identical WPF styling, copied proprietary controls, or completion of all chart-library parity.

## Reference and implemented layout

Reference material was reviewed on 2026-10-11:

- [Official examples-suite documentation](https://www.scichart.com/documentation/win/current/The%20SciChart%20WPF%20Examples%20Suite.html)
- [Official public ShellControl.xaml](https://github.com/ABTSoftware/SciChart.Wpf.Examples/blob/master/Examples/SciChart.Examples.Demo/ShellControl.xaml)
- [Official multiple-axis screenshot](https://www.scichart.com/wp-content/uploads/2022/12/scichart-wpf-chart-example-multiple-yaxis.jpg)

The reference has a prominent back/home/forward header, global search, a narrow breadcrumb/command strip, an edge-aligned chart toolbar and an information sidebar. ChartScene now follows that hierarchy for its main 2D examples page: compact vector commands, a navy chart surface, header search, back/forward history, a home catalogue, Example/Source Code views and a togglable right information pane. Cross-gallery navigation is compact and scrollable, and still exposes every existing gallery.

All icons are independent vector line drawings using the reusable canvas abstraction. No SciChart logos, proprietary binaries, font files or claims about DirectX acceleration are copied. The status bar identifies the actual Skia host and CPU render duration. Pixel-equivalence with SciChart remains unqualified; the reference's complete thumbnail carousel, WPF control templates, source editor features and export-to-solution workflow are not claimed.

## Reusable functionality

`NavigationJournal<T>` is a bounded, UI-independent back/forward journal in Core. Duplicate navigation is a no-op, branching drops the abandoned future, and callers receive read-only snapshots. Owners serialize mutations on their UI thread.

`ExampleSearchIndex` in Samples precomputes searchable metadata including title, category, description and code snippets. It performs case-insensitive all-term searches and keeps at most sixteen immutable result sets. Equivalent normalized queries return the same result object. No chart factory is invoked during indexing or searching. The UI reuses row controls and skips a rebuild when the result object is unchanged; this avoids repeated row construction during equivalent searches.

`ChartIconRenderer`, `ChartIcon` and `ChartToolButton` provide reusable vector glyph rendering and compact theme-aware commands. The controls use the existing Uno/Skia path. Search/result metadata and icons are independent of chart data and GPU lifetimes.

`ExampleSource.FactoryCode` reads an embedded copy of the actual compiled `SampleCatalog.cs` lazily. Source view and C# download use that exact resource, not a placeholder. It is the shared factory implementation, not a stand-alone exported application. The existing PNG and CSV exports remain available.

## Correctness and lifecycle fixes

The main page no longer reloads the initial example every time its visual tree is reattached. The selected example, chart data, navigation history and viewport remain available when returning from another gallery. A hidden streaming view stops its timer without replacing FIFO data. Resuming streaming is an explicit action.

The scene graph stays attached to the chart control while source/catalogue views change. Source text is loaded only on first access. Search results appear on demand and dismiss after navigation. Visibility diagnostics inspect ancestor visibility, avoiding stale clickable rectangles from collapsed panels. Optional test-input tracing is weakly registered once per control so reusing result rows cannot accumulate duplicate handlers.

Source replacement and range updates continue to use the validated chart/data APIs. Multi-series streaming updates and theme changes use model notification batching.

## Validation

The existing six compiled-browser suites, native GL/GLES fixtures, core regression tests and all twelve isolated package consumers remain required. The workbench suite additionally exercises real search input, source-keyword lookup, result reuse, history, catalogue navigation, real C# downloads, pane toggles, reattachment with retained zoom, streaming pause and responsive command reachability. Tests observe managed input delivery and application state without mutating them. Screenshots are retained under `browser-tests-ui`.

Core tests validate bounded history, search results against a scalar oracle, concurrent immutable search reuse, no chart construction during search, vector output for every glyph, and the exact embedded source resource.

Search and history allocation/work improvements are separate from GPU or end-to-end frame timing. Browser screenshots establish functioning layout and visible output; they do not establish pixel equivalence with an unexecuted reference WPF application. Accessibility, all native platforms, broad cross-browser behavior and the remaining chart APIs require further qualification.
