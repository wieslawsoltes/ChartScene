# Annotation wrapping and native input layout review

This continuation follows annotation typography PR #14, merged as
`3d0d950b86d2533f86e793dd9ec2b9a848538658`. The previously blocked
history-command follow-up is excluded and has not been retried.

## Reusable behavior

`ChartAnnotation.TextWrapping` offers `NoWrap` (the compatible default), `Wrap`
and `WrapWithOverflow`. `TextWrapWidth` is a positive finite logical-pixel
**content** width, excluding padding; its default is 160 and it is ignored in
NoWrap. Invalid assignments fail before mutation or notifications. These are
independent ChartScene APIs, not a claim of identical WPF dependency properties.

Hard CR/LF/CRLF paragraphs, including blank paragraphs, remain distinct. Soft
wrapping does not rewrite `Text` or create editing history. Whitespace provides
word opportunities except nonbreaking U+00A0/U+2007/U+202F spaces. Whitespace at
an automatic boundary is not painted; explicit paragraph indentation survives.
Wrap breaks long words only at .NET extended grapheme boundaries. A single
oversized grapheme may exceed the requested width; WrapWithOverflow instead
keeps an entire unbroken word and permits that word to exceed the width.

The wrapping content box is at least the requested width and grows only for
actual overflow. Alignment uses that box; anchors, rotation, padding, optional
panels, clipping, rendered baselines and hit testing share the same immutable
layout. Old frames retain their original strings and geometry after mutation.
Width/mode changes invalidate retained scenes through model revision tracking.
Unchanged scene replay does not rewrap the label.

## Review fixes and performance scope

Line fitting uses exponential prefix probes followed by binary search, starting
from the current line. Grapheme offsets are parsed once per hard paragraph.
It does not repeatedly shape the entire unconsumed suffix for every narrow line.
The deterministic 8,192-character narrow-width fixture asserts fewer than 16
measured input characters per source character and fewer than 12 measurement
calls per emitted line. Its JSON artifact records the actual work counts. This
is a cold-layout query-work bound for that fixture, **not timing, allocations,
physical GPU throughput or whole-application performance**.

Prefix fitting assumes normal increasing advances to choose an efficient fit;
it need not find the globally longest fit for pathological non-monotonic custom
metrics. Every emitted substring is measured. Review found that removing a
separator can change contextual shaping, so Wrap falls back to its measured
fitting prefix when the preferred shorter word break becomes wider. Invalid
metrics during any prefix measurement discard the whole layout, not a partial
published body. Regression cases exercise both behaviors.

## Native workbench

The actual Uno inspector has wrapping and content-width ComboBoxes. The width
control is disabled in NoWrap; choices use the existing shared chart undo/redo.
Refresh does not redirect native popup focus or discard an unapplied text draft.
The inspector and double-click dialog now also inherit the selected annotation's
font size and alignment, fixing the previous default-size/left-aligned input.
Native input keeps its own wrapping to remain usable while editing; its viewport
is not a simulated annotation layout and no unsupported TextBox wrapping enum is
assigned.

Read-only browser diagnostics report actual native control properties and popup
focus. Tests use real mouse and keyboard input, including undo/redo, rotation,
word overflow, width changes, independent drafts and dialog typography. No DOM
state injection, synthesized control events, relaxed pixel/numeric thresholds,
longer timeouts or disabled deployment gates were added.

## Validation and limits

Source was recovered from checksum-verified CI artifacts and each pushed tree
was checked against its local commit. This continuation has no local .NET SDK;
local diff checks and Python syntax compilation do not substitute for C# or
browser execution. Consult the PR's final-head Validate and Application runs for
core/Skia, both Uno targets, all browser suites, native GL/GLES, all twelve packages
and the two isolated package-only consumers before merging.

This does not implement complete Unicode UAX #14 breaking, language-specific
hyphenation, bidi paragraph layout, rich-text runs, text decorations, font stretch,
variable axes, arbitrary WPF templates or resize adorners. Installed/available
font fallback still controls actual shaping. Full SciChart API, pixel and
hardware-performance parity remains unestablished.

## Public references

- SciChart TextAnnotation public behavior and default text template:
  https://www.scichart.com/documentation/win/current/The%20TextAnnotation%20Type.html
- Microsoft .NET extended grapheme enumeration:
  https://learn.microsoft.com/en-us/dotnet/core/compatibility/globalization/5.0/uax29-compliant-grapheme-enumeration
- Microsoft StringInfo.ParseCombiningCharacters:
  https://learn.microsoft.com/en-us/dotnet/api/system.globalization.stringinfo.parsecombiningcharacters?view=net-10.0

These are public behavioral references; no proprietary implementation or assets
were copied.
