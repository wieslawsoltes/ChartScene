# Browser startup and repeated-search review

## Evidence and root cause

The preserved PR #5 build at `44661969b76c3ac558214b0d6d83096a2a8b9c01`
could report valid managed chart frames while a full-window persistent Uno loader
covered the canvas. A separate no-input probe (run `38172515062`, artifact
`11714486754`) observed the loader remaining in three of four independent starts.
Managed `ready`, a longer delay, or counting the loader's colored progress bar is
not proof of a presented chart.

The first adapter at `a4085433` briefly added/removed `CompositionTarget.Rendering`
after awaiting fonts. That was insufficient: the full `c4acc7b2` check in run
`38174038657` passed 160/163 browser checks, but the linked cold start and the
stacked cold start with a 250 ms font delay retained the loader. The third failure
was a separate transient toolbar-state capture described below. The previous
one-frame approach is superseded, not claimed fixed.

In pinned Uno 6.7.135, `ShowCore` awaits `Application.FontPreloadTask` through a
separate continuation, dispatches `InvalidateArrange`, and then installs its
`FrameRendered` listener from a `LayoutUpdated` handler. Font task completion
alone therefore does not establish that the listener exists. The host then calls
`IXamlRootHost.InvalidateRender`, which can present an already-recorded frame
without another record pass. Upstream #24260 changes that call to
`ICompositionTarget.RequestNewFrame` at the exact post-layout point.

Primary host sources:

- https://github.com/unoplatform/uno/pull/24260
- https://github.com/unoplatform/uno/issues/23586
- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI.Runtime.Skia.WebAssembly.Browser/UI/Xaml/Window/WebAssemblyWindowWrapper.cs
- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI/UI/Xaml/Media/CompositionTarget.Rendering.skia.cs

## Scoped startup hand-off

`UnoBrowserStartup.EnsureInitialPresentationAsync` is a **version-qualified
compatibility adapter**, not a patched Uno compositor or a permanent animation
loop. It awaits the typed internal font-preload getter, then temporarily retains
the public `CompositionTarget.Rendering` subscription until Uno removes its own
persistent loader after its post-layout record callback. This keeps full-frame
recording active across the native listener-registration race.

The adapter only **reads** loader existence. It never hides/removes the loader,
changes chart data/styles, synthesizes input, or replaces the host's completion
callback. It handles an already-released loader without subscribing. Unload,
exceptions and a 30-second hand-off deadline all unsubscribe and stop the
deadline timer; that timer is only a cleanup guard and never paints or requests
a frame. Font waiting has a separate two-minute bound before any rendering
subscription is attached. Desktop is a no-op.

One startup **session** may observe multiple composition callbacks. Diagnostics
report `sessions`, `compositionCallbacks`, `active` and `detached`; they do not
call a session one frame or label a managed callback as physical presentation.
Actual visible pixels remain a separate browser assertion. Requalify/remove this
adapter when upgrading Uno. Background-tab, timeout and physical-device behavior
are not certified by the foreground Chromium fixtures.

The startup suite repeats all six galleries and both delayed-font fixtures in
three independent fresh browser contexts each: **24 no-input cases**. The
250/700 ms cases must intercept actual font files. Every case requires native
loader dismissal and two consecutive screenshots meeting the unchanged pixel
threshold. Static stacked pages must settle without continuous chart redraw.
Existing gallery geometry, interaction, data, export and package-consumer gates
remain separate requirements.

## Theme presentation observation

The `c4acc7b2` run captured the middle-series and Fit buttons during a transient
state on returning from Light to Dark. Its subsequent preserved failure
screenshot already had the normal dark controls. Chart-model readiness and a
chart render callback do not fence the native controls' visual-state transition.

`chrome_pixels` now records each control's contrast in successive **real
screenshots**, requiring two consecutive observations that satisfy every
original light/dark threshold. It retains intermediate failures and elapsed time
in JSON, clips samples to the actual image bounds (no off-screen padding), and
fails if the controls do not settle within eight seconds. It does not click
again, disable animations, force a redraw, change application colors, or lower
any readability threshold.

## Repeated search activation and allocation

Selecting a result hides navigation. Assigning identical text does not raise
`TextChanged`; search focus, pointer activation and Ctrl+F explicitly reveal the
retained results. Immutable result identity still skips row reconstruction, and
`ItemClick` handles reactivation of a current example without recreating its
model.

`ExampleSearchIndex` validates the 4096-character bound before whitespace
normalization or cache mutation. Oversized whitespace cannot bypass the bound.
A last-input value comparison under the cache lock bypasses normalization and
scanning for identical queries; the sixteen-result cache, all-term substring
behavior, catalogue order and read-only result contract remain intact.

The core fixture reports 10,000 warmed identical queries separately from initial
construction and normalization. It requires zero additional examined entries,
one retained result identity and zero measured managed allocations. The report
at `artifacts/performance/example-search.json` records CPU timing as evidence,
not a timing threshold, UI-latency result or GPU/FPS claim. Regression cases cover
distinct value-equal strings, empty/null transitions, eviction, concurrent
readers and rejection without altering a valid cache generation.

## Scope

Temporary diagnostic/repair workflows were removed after use. The original
saved evidence remains on `review/pr5-446619-layout-evidence`, not in packages.
Current CI also retains per-attempt startup screenshots and theme observations.

Full SciChart WPF API/pixel parity, physical-GPU benchmarks, complete
accessibility and cross-platform device-loss qualification remain outstanding.
See `parity-status.md` for the capability inventory. Successful packaging means
CI artifacts and isolated consumer builds, not publication to NuGet.org.
