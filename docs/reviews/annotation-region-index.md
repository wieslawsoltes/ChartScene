# Frame-owned text-region lookup

## Reproduced work and ownership defects

The annotation-drag pipeline examines annotation candidates. Its text distance
helper previously scanned every text region in the frame for each candidate.
A deterministic 512-candidate fixture measured **131,328 region reads** after
frame publication (512 × 513 / 2), even though identity lookup was sufficient.
An externally supplied mutable `TextAnnotations` list was also retained directly:
clearing that list changed an already published frame.

Both regressions failed against the recovered PR #12 implementation. The revised
implementation passes them without changing the actual measured text layout,
rendering clip, candidate ordering or stale-frame rejection.

## Implementation

`ChartFrame.TextAnnotations` now captures a read-only frame-owned snapshot. Its
internal collection indexes annotation objects with reference equality and exposes
`FindTextAnnotation` for lookup. The last region wins when an annotation appears
more than once, matching last-painted geometry. Duplicate application IDs do not
alias object identities. Source lists are not retained. Record copies and Skia
scene replays share an already immutable snapshot instead of rebuilding it.

The renderer passes its temporary region list to the capturing initializer, which
makes one array copy; this replaces its previous array copy/read-only wrapper
rather than adding an additional array copy. Empty frames reuse one empty
collection. Typography or geometry changes still create a new frame as before.
There is no global cache mixing canvases, windows or font metrics.

## Validation and scope

The fixture now records zero repeated source-region reads for the same 512
candidate lookups. Building the per-frame index is O(n), and a full annotation
search still visits candidates; this is not constant-time spatial hit testing.
Tests also cover read-only behavior, null validation, independent view geometry,
record-copy sharing and last-painted duplicate handling. Existing rotation,
clipping, logarithmic anchor, drag/undo and display-list cache tests remain enabled.

`artifacts/performance/annotation-region-lookup.json` reports the deterministic
work counts. These measurements exclude text shaping, initial index construction,
rendering, native UI and GPU/presentation. All browser/desktop/package/GPU gates
are retained. This optimization does not establish full SciChart WPF API/pixel
or hardware-performance equivalence.
