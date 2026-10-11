# Browser startup and repeated-search review

## Cold-start frame scheduling

The preserved PR #5 run at `44661969b76c3ac558214b0d6d83096a2a8b9c01` contained a genuine no-input presentation failure. Managed stacked-chart diagnostics reported a valid frame, while repeated screenshots were uniformly RGB(243,243,243). Pointer input subsequently made both toolbar and chart visible. Waiting longer or accepting a managed `ready` flag is not a rendering fix.

Pinned Uno 6.7.135 calls the browser wrapper's `RequestRender` after font preloading. That requests presentation without setting the composition target's full-frame recording state. The upstream correction replaces it with `ICompositionTarget.RequestNewFrame`:

- https://github.com/unoplatform/uno/pull/24260
- https://github.com/unoplatform/uno/issues/23586
- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI.Runtime.Skia.WebAssembly.Browser/UI/Xaml/Window/WebAssemblyWindowWrapper.cs
- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI/UI/Xaml/Media/CompositionTarget.Rendering.skia.cs

`UnoBrowserStartup.RequestInitialFrameAsync` is a version-qualified compatibility adapter, not an independently claimed replacement compositor. It waits for the exact typed internal `Application.FontPreloadTask` getter using a trimming-compatible `UnsafeAccessor`, then uses the pinned public `CompositionTarget.Rendering` event's initial subscription behavior to enqueue `RequestNewFrame`. The handler is removed immediately: the queued frame remains, with no timer, continuous animation handler, artificial pointer event, DOM splash removal, or chart data/style mutation. An existing Rendering subscriber already schedules complete frames. Detached roots are ignored, dispatcher failures and missing runtime contracts are reported. The application invokes this once after its root loads.

Requalify/remove this adapter when changing Uno. Desktop is a no-op. Browser publish and package-only builds, plus actual cold-start execution, must validate the getter binding and composition behavior; source inspection alone is insufficient.

## Repeated search activation

Selecting a result hides the navigation pane. Assigning the same query does not raise TextChanged, so the old page could leave its retained results inaccessible. Search focus, pointer activation and Ctrl+F now explicitly reveal existing results. The immutable result identity still skips rebuilding row controls, and ItemClick continues to handle reactivation of an already-current example without constructing another model.

The temporary workflows used to inspect preserved failures and repair one exact header-parent statement were removed after execution. Saved evidence remains on the separate `review/pr5-446619-layout-evidence` branch, not in release packages. The original dispatch-only diagnostics workflow remains available.

The existing pixel thresholds, input assertions, all examples, native GL/GLES and package-consumer gates remain required. This review does not establish complete SciChart WPF API/pixel parity or hardware GPU performance.
