# Text width grips and responsive annotation properties

This continuation starts from PR #15, merged as
`16941e93db87f5e5ba2cf20e0913ca008fee1288`. The previously blocked general
history-command follow-up is excluded and has not been retried.

## Reusable rendering and interaction

A selected, editable wrapped annotation now has a round width grip. Its
`AnnotationWidthGrip` position and width-per-pixel factor belong to the immutable
`TextAnnotationLayout`, including its rotation and clipping. NoWrap and read-only
text have no width grip. Existing square corner indicators remain selection
indicators, not newly implemented arbitrary corner/height/font-scale handles.

`TextAnnotationResizeModifier` is in the default controller pipeline before text
body editing and annotation dragging. A current frame is required to begin a
resize; the last-painted selected grip consumes a native double tap so it does
not accidentally open the text editor or trigger zoom extents. Body double-click
editing away from that grip remains enabled.

The X1/Y1 anchor, font size, source text, padding and rotation are unchanged by
width dragging. Pointer motion is projected through the captured inverse
rotation, using the original press position rather than accumulating deltas.
Left anchoring uses the right edge, center anchoring changes the requested width
by twice the right-edge motion, and right anchoring uses the left edge with an
inverted delta. Width is in logical pixels regardless of data/relative/pixel
anchor coordinates or axis orientation/scaling. In WrapWithOverflow the handle
represents the **requested** width, not the potentially wider indivisible word.

The modifier defaults to widths 16–4096 and an 8-pixel hit radius; finite positive
limits can be configured. A single stationary press or return to the original
press point preserves the exact original width, including a programmatically
assigned width outside those gesture limits. A single completed drag records
one undo entry; a stationary gesture does not discard an existing redo branch.

## Ownership and cancellation

Only the captured mouse/pen pointer may update, finish or dispatch another
modifier during a gesture. The existing two-touch pinch handoff runs before that
guard: a second touch first rolls back the width preview, then starts normal
pinch navigation. A captured resize owns the cursor and avoids unrelated
rollover/snapshot work.

Escape, cancellation, disabling, changed interaction mode or changed plot
geometry restore only the modifier's still-owned preview. Annotation edits,
axis changes and collection changes invalidate the session without rolling back
the external state. Observers are detached before rollback notifications.
Synchronous subscriber edits/cancellation cannot revive a released session.
Undo/redo checks current membership, editability, wrapping mode and the expected
width; a mismatch permanently invalidates that width command. These are
value-ownership checks, not general document revisions, ABA detection or a
replacement for the deferred general history work.

## Native properties pane

The text workbench adds explicit Properties and Selection commands, a pinned
properties header, a native close button and a vertically scrollable body.
Desktop users can hide or restore the docked pane. At compact widths the pane
starts closed but can open as a right-aligned overlay without changing the
chart's dimensions. The editor controls are not reparented, so their identities
and unapplied drafts survive pane transitions. Close restores focus to the
Properties command; Escape closes the compact pane from its editor.

Drag-created custom widths are shown in the actual native width selector. The
selector retains its five presets and at most one custom value, rather than
accumulating every preview width. Unchanged refreshes reuse the list; an open
native popup is not reset by refresh. The footer distinguishes NoWrap from an
available width grip.

## Validation and performance scope

Core regressions cover rotated/anchored geometry, all coordinate modes, actual
Skia grip pixels, scene reuse, exact undo/redo, stale frames, clipped handles,
invalid limits, reentrancy, external edits, disposal, double taps, foreign-pointer
isolation and touch-to-pinch handoff. Browser cases use actual routed mouse and
keyboard input; pointer-coordinate diagnostics are read-only. The resize oracle
uses the delivered down/up coordinates to account for native coordinate
quantization while keeping the existing numerical tolerance unchanged.

The million-point fixture counts rollover snapshot reads during 100 captured
moves without rendering new frames. Three warm windows check allocations for
10,000 cached grip-hit queries each. These fixtures do not measure shaping,
preview relayout, native UI layout, GPU time, FPS or presentation. Starting and
completing a gesture and creating its history command do allocate resources.

This session has no local .NET SDK. Local source/diff and Python syntax checks
are not C# or browser execution. The final-head Validate and complete Application
workflows remain mandatory before merge: core/Skia, both Uno targets, all
browser suites, native GL/GLES, all twelve packages and isolated package-only
consumers. No test, timeout, numerical/pixel tolerance or deployment gate is
waived. The PR records the actual final revision and results.

## Remaining boundaries and references

This is an independently implemented width grip and native Uno pane, not exact
SciChart dependency-property, style/template, adorner or pixel equivalence. It
does not implement arbitrary annotation resizing, font scaling, rich-text runs,
complete Unicode paragraph breaking, arbitrary WPF templates, screen-reader
resize automation or physical-device/hardware performance qualification.

Public behavior consulted:

- https://www.scichart.com/documentation/win/current/Templating%20Annotation%20Resize%20Grips.html
- https://www.scichart.com/documentation/win/current/Editing%20and%20Interacting%20with%20Annotations.html
- https://www.scichart.com/documentation/win/current/The%20TextAnnotation%20Type.html

These establish the comparison target (selection grips, editable annotations,
text anchors and native text properties), not identical width-resize semantics.
No proprietary binaries or graphic assets were copied.
