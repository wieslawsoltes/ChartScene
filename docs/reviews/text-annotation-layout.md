# Measured text annotations

## Scope and reference

This independent implementation follows the anchor and editable-text behavior
in SciChart's public WPF TextAnnotation documentation:
https://www.scichart.com/documentation/win/current/The%20TextAnnotation%20Type.html

The typed `TextAnnotation` defaults to Left/Top. Existing callers constructing
`new ChartAnnotation(AnnotationKind.Text)` retain their baseline placement;
`VerticalAnchorPoint.Baseline` is an explicit ChartScene compatibility extension.
This does not provide arbitrary WPF ControlTemplates, rich-text runs, wrapping,
font fallback policy parity, text resizing adorners, or full reference-pixel/API
compatibility. The current font contract supports a family, size and bold flag,
not every WPF font-weight/style combination.

## Reusable model and rendering

`ChartAnnotation` has observable horizontal/vertical anchors, multiline alignment,
font family/bold, clockwise rotation, padding, optional background and border,
and line spacing. Invalid enums and nonfinite/negative dimensions are rejected
before changes. Rotation is around the X1/Y1 control point, independent of chart
axis rotation or direction. Relative and pixel modes keep their existing screen
coordinate meaning; data mode uses the selected axes.

`TextAnnotationLayout` measures each line using the active `IChartCanvas` font
metrics. Padding, blank lines, CRLF/CR/LF, alignment and anchors contribute to one
local rectangle. Rotation transforms its corners and baselines around the same
anchor. The renderer draws background/border, text and selection grips from that
layout. Hit tests inverse-transform the pointer into the padded body and respect
the actual drawing clip; an axis-aligned bounding box is not sufficient to hit a
rotated label accurately.

`ChartFrame.TextAnnotations` contains the immutable layouts from that rendered
frame. They are not global caches keyed only by annotation: the same model can be
shown in different views without mixing text geometry or font metrics. Text uses
only X1/Y1; invalid, unused X2/Y2 on a logarithmic axis no longer suppress it.

## Input and native UI

The annotation drag pipeline uses measured text regions and moves a text label
as a whole rather than treating its unused second coordinate as a resize grip.
Stationary text selection does not record an empty edit. The default modifier
pipeline routes text double clicks to `AnnotationEditRequested` before ordinary
zoom extents. A pending selection redraw cannot cause a native double tap to
fall through into zoom. Removed, hidden or noneditable labels are not opened.

The `Text labels` gallery uses actual Uno toolbar buttons, a multiline native
TextBox, native ContentDialog editing, the chart surface and existing chart
history. Anchors, line alignment, rotation, size, bold and background are live
options. The inspector collapses at compact widths; native horizontal scrolling
keeps commands accessible. Light/dark themes cover the plot and native controls.
The gallery demonstrates one annotation; general multi-annotation transactions
and reentrant history handling remain separate work.

## Performance and verification

Unchanged cursor frames reuse the existing Skia display list and the exact
measured layout collection. Body hit tests do not measure or shape text again.
The fixture warms a non-tiered measurement helper, then requires zero managed
allocation in each of three 10,000-hit windows. Another assertion requires one
scene build and 40 unchanged replays before a typography edit invalidates it.
These are scoped CPU geometry/cache assertions, not whole-app zero-allocation,
GPU speed, FPS, hardware equivalence or constant-time text layout claims.

Three executable probes ran against the recovered published tree and the changed
source: the old implementation failed all three; the changed implementation
passes them. They cover unused logarithmic endpoints hiding a valid anchor,
selection away from the text origin, and double-click editing instead of zoom.
The core suite covers anchor combinations, rotation, multiline alignment,
validation, clipping, blank labels, frame ownership, dragging/undo on rotated and
flipped charts, native-edit requests and raster/cache behavior. The required
browser suite uses real mouse/keyboard and native UI; read-only diagnostic DTOs
are never used as an application-command bridge.

The previously blocked local history-command follow-up is not included or
re-uploaded. This branch starts from the deployed main tree
`e8df1685f3979cede817038ccc0a1c0cd3487579` and changes only annotation, rendering,
input integration, gallery and validation files. Both Uno builds, every prior
browser suite, native GL/GLES fixtures, 12 packages and isolated package-only
consumers remain required before merge.
