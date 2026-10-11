# Search dismissal and responsive focus restoration


The next complete run, `38176006674` at `0ffde9a2`, passed **178/179 browser
checks**. All **24/24 no-input startup cases** completed with the native loader
gone and only one or two composition callbacks per startup session. Both theme
directions and all 13 stacked checks passed. This qualifies the foreground
Chromium startup hand-off in those fixtures, not arbitrary host/device behavior.

The remaining workbench failure was not chart measurement: after Escape and
resize to 800 pixels, the saved screenshot and actual bounds showed navigation
open again at width 235, leaving the chart at width 527. No new search pointer
activation appeared in the saved input trace. Unconditionally treating every
`GotFocus` notification as an instruction to open navigation allowed focus
restoration after dismissal to change layout.

Dismissal now persists across incidental focus notifications. It is set before
Escape clears text or moves focus, and when a result or navigation-close command
dismisses the pane. A new pointer activation, Ctrl+F, a nonempty focused text edit,
Down-arrow, or keyboard Next/Previous focus traversal can explicitly reactivate
search. Focus callbacks also verify the current focused element instead of
trusting a potentially stale routed notification. No resize-specific hiding,
fixed delay, or reduced chart-width requirement is used.

Two added browser regressions exercise Escape/resize and pointer reactivation at
800, 1024 and 1440 pixels, and Shift+Tab reentry followed by a typed query. They
retain current model identity and require dismissal to remain stable on every
observation; a failed observation is not retried. Diagnostic fields expose
`navigationOpen`, `searchDismissed` and `searchFocused` read-only.

## Warm allocation measurement

Run `38177773692` stopped at the unchanged 3D metadata allocation fixture; the
parallel Validate run `38177773699` passed the same 500 tests on the same source.
The failing fixture did not preserve the allocation count before asserting.

The measurement now warms its exact non-inlined, non-tiered helper and measures
only the immutable read loop. Timing conversion and reporting happen outside
the allocation interval. It checks **both opaque and translucent buffers** and
retains the original **less-than-1024-byte guard per 100,000 queries**. Measured
values are written before assertions, including failure cases. The production
getter, data representation, JIT configuration and all rendering code are
unchanged. Results describe warmed metadata reads, not cold calls or application
latency; no timing threshold was added or weakened.
