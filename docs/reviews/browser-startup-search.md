# Browser startup and repeated-search review

## Cold-start frame scheduling

The preserved PR #5 run at `44661969b76c3ac558214b0d6d83096a2a8b9c01` contained a genuine no-input presentation failure. Managed stacked-chart diagnostics reported a valid frame, while repeated plot screenshots were uniformly RGB(243,243,243). Pointer input subsequently made both toolbar and chart visible. Waiting longer or accepting managed `ready` is not a rendering fix.

A separate no-input probe of that exact saved binary (run `38172515062`, artifact `11714486754`) observed the full-window `#loading.uno-persistent-loader` above the canvas in three of four independent starts. The fourth dismissed it without input. This distinguishes a reproducible scheduling race from a missing chart or toolbar implementation.

Pinned Uno 6.7.135 calls the browser wrapper's `RequestRender` after font preloading. That requests presentation without setting the composition target's full-frame recording state. The upstream correction replaces it with `ICompositionTarget.RequestNewFrame`:

- https://github.com/unoplatform/uno/pull/24260
- https://github.com/unoplatform/uno/issues/23586
- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI.Runtime.Skia.WebAssembly.Browser/UI/Xaml/Window/WebAssemblyWindowWrapper.cs
- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI/UI/Xaml/Media/CompositionTarget.Rendering.skia.cs

`UnoBrowserStartup.RequestInitialFrameAsync` is a version-qualified compatibility adapter, not an independently claimed replacement compositor. It waits for the exact typed internal `Application.FontPreloadTask` getter using a trimming-compatible `UnsafeAccessor`, then uses the pinned public `CompositionTarget.Rendering` event's initial subscription behavior to enqueue `RequestNewFrame`. The handler is removed immediately: the queued frame remains, with no timer, continuous animation handler, artificial pointer event, DOM splash removal, or chart data/style mutation. An existing Rendering subscriber already schedules complete frames. Detached roots are ignored, dispatcher failures and missing runtime contracts are reported. The application invokes this once after its root loads.

Requalify/remove this adapter when changing Uno. Desktop is a no-op. Browser publish and package-only builds, plus actual cold-start execution, must validate the getter binding and composition behavior; source inspection alone is insufficient.

The no-input startup suite opens every gallery in a fresh context and additionally delays real font downloads by 250 and 700 milliseconds for the stacked gallery. It requires Uno itself to remove the persistent loader before evaluating consecutive visible-pixel screenshots; the loader's colored progress bar cannot satisfy a chart assertion. It verifies a single startup request and that the static stacked view settles rather than continuously redrawing. Existing per-gallery geometry, pixel and input tests remain separate gates.

## Repeated search activation and allocation

Selecting a result hides the navigation pane. Assigning the same query does not raise TextChanged, so the old page could leave retained results inaccessible. Search focus, pointer activation and Ctrl+F now explicitly reveal existing results. Immutable result identity still skips rebuilding row controls, and ItemClick handles reactivation of an already-current example without constructing another model.

`ExampleSearchIndex` validates the 4096-character bound before examining whitespace or touching cache state. Oversized whitespace can no longer bypass the bound. A last-input value comparison under the same cache lock avoids normalization and scanning for identical queries. The existing sixteen-result cache, all-term substring behavior, order and read-only result contract are retained.

The core fixture reports 10,000 warmed identical queries separately from initial construction/normalization. It requires zero additional examined entries, the same result reference and zero measured managed allocations. `artifacts/performance/example-search.json` records elapsed CPU time as evidence, not a timing threshold or GPU/FPS claim. Tests also cover value-equal distinct strings, null/empty transitions, eviction, concurrent readers, and rejection without altering valid cache state.

Temporary workflows used to inspect preserved failures and repair one exact header-parent statement were removed after execution. Evidence remains on `review/pr5-446619-layout-evidence`, not in release packages. The original dispatch-only diagnostics workflow remains available.

This review does not establish complete SciChart WPF API/pixel parity or physical-GPU performance. All examples, native GL/GLES, browser builds and isolated package-consumer gates remain required.
