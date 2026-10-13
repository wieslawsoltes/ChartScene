# Annotation typography and text-cache review

This continuation starts from PR #13's verified source, merged as
`857eafcdf98d11a0995c2086557df4c7d538c190`. It does not include or retry the
previously blocked history-command follow-up.

## Public behavior and compatibility

`ChartAnnotation.FontWeight` accepts 1 through 1000; 400 is normal and 700 is
bold. `FontSlant` selects Normal, Italic or Oblique independently. Invalid
assignments fail before changing model state. Changing weight notifies the
legacy `FontBold` property when its value changes. Assigning `FontBold` selects
700 or 400; readback is true for weights at least 700.

The original four-argument `TextStyle` constructor and four-output deconstruction
are retained. Optional init-only `Weight` (zero means the legacy Bold flag) and
`Slant` extend rendering without changing existing call sites. Font and shaped
text caches key the effective weight and slant, not the compatibility flag.
Explicit weight 700 and legacy Bold share resources; text color does not duplicate
shaped glyphs. Published annotation layouts retain their measured typography.

Skia selects the nearest available family/weight/slant. The application does not
bundle or promise installed fonts, exact WPF metrics or identical platform font
fallback. Font weight is a requested face weight, not a variable-font-axis editor.

## Native annotation page

The inspector exposes family, weight and style ComboBoxes. The toolbar has
independent bold/italic and left/center/right multiline vector glyphs. All changes
use the existing chart history; Bold undo restores a preceding custom weight
rather than reducing it to a Boolean. TextBox and dialog typography follow the
selected family, weight and slant. Styling preserves a separate unapplied draft.
Programmatic refresh never moves keyboard focus or changes a navigated popup.

Four added browser cases exercise real popup/keyboard selection, precise weight
and slant undo, and draft preservation. Focus diagnostics observe the actual
native ComboBoxItem; they do not set application state or simulate control events.
All existing browser assertions, timeouts, pixel thresholds and CI gates remain.

## Rendering fixes and measured work

Direct canvas text normalizes CR, LF and CRLF before cache lookup. Equivalent
paragraphs share one blob and have equal pixels/metrics. Empty paragraphs use the
actual face metrics and no ink. Previously an empty blob was replaced by a glyph
at (-100000,-100000), which a host transform could bring back into view. Empty
measurement now also validates its style and disposed resource state.

A captured LINQ lambda in the cache-miss branch caused a display-class allocation
at method entry, even when the warm cache immediately returned. Indexed glyph and
position loops remove that capture. The local warmed, non-tiered measurement
fixture first measured 400000 bytes per 10000 canonical queries; after the fix
three consecutive windows measured zero bytes each. This measures warm CPU text
measurement only, not initial shaping, CR normalization, drawing, GPU time, FPS or
whole-application allocation. Native resource pruning remains between frames.

## Validation and remaining scope

Local Release validation passed 786 core tests (760 existing, 21 focused text
cases, five additional enum-driven glyph cases). Tests include actual Skia raster
differences, empty-ink regression, immutable-layout preservation, scene-cache
invalidation, invalid-input atomicity and warm-allocation evidence. The browser
script passed Python syntax compilation. Desktop/browser application compilation,
real-input browser validation and both isolated Uno package consumers are separate
required remote gates; consult the PR's final-head results before merging.

This is not complete SciChart API, template, pixel or hardware-performance parity.
Rich-text runs, wrapping of annotation bodies, text decorations, font stretch,
variable-font axes, arbitrary WPF templates and resizing adorners remain outside
this continuation. The native editor wraps its input, while the annotation body
continues to use explicit hard line breaks.

## Public references consulted

- SciChart, The TextAnnotation Type: https://www.scichart.com/documentation/win/current/The%20TextAnnotation%20Type.html
- SciChart, RenderContextBase.MeasureText: https://www.scichart.com/documentation/win/current/SciChart.Drawing~SciChart.Drawing.Common.RenderContextBase~MeasureText.html

These describe the public typography/editing surface. ChartScene's implementation
and vector icons are independent; no proprietary binaries or assets are included.
