# Original-sample point editing

This change adds an independent point editor and a real Uno gallery. It follows
the public SciChart select-and-drag example's Y-editing workflow, not its proprietary
implementation or a claim of complete WPF/API/pixel equivalence.

Reference: https://support.scichart.com/support/solutions/articles/101000519450-custom-chartmodifiers-part-5-select-and-drag-a-data-point

## Reusable integration

```csharp
var editor = new DataPointEditModifier { IsEnabled = true, HitRadius = 14 };
surface.Controller.Modifiers.Insert(0, editor);
```

The modifier defaults to disabled and does not change existing chart behavior.
Use one instance per controller; retain it with the owning view. Insert it before
pan and selection modifiers. Controller cancellation, surface unload and model
replacement release the active gesture and its model/axis subscriptions.

Supported editable shapes are Cartesian two-channel line, digital line, spline,
mountain, spline mountain, scatter, column, bar and impulse series backed by a
mutable `DataSeries`. A grab targets the actual source endpoint, not an interpolated
curve position, resampled index, or arbitrary filled bar interior. X and all other
rows remain unchanged. Value-axis projection handles secondary axes, logarithms,
flipped coordinates and rotated charts. Moving perpendicular to the value axis
does not edit. The initial grab offset is preserved, motion outside the plot is
clamped, and a stationary click or return to the original coordinate preserves
exact original bits and creates no history entry.

A continuously auto-ranging value axis is not editable; set its policy to Never
or allow its initial Once fit to complete. The editor never silently changes the
user's auto-range policy. Stacked, financial/multi-channel, polar, circular,
ternary, custom read-only data sources and X-coordinate dragging are outside this
implementation. Unsupported grabs return `Unsupported`, with no data write or
accidental pan while editing mode is active.

## Atomic data writes and history

`DataSeries.TryUpdateChannelValue` compares the expected revision and bitwise
value under the existing storage lock, then validates the replacement row and
X neighbors before mutation. This includes derived financial row invariants.
Stale revisions return false even when concurrent eviction/clear changed the
index domain. Current invalid indices, channels and values are diagnosed. A
bit-identical value succeeds without notifications, revision changes or snapshot
invalidation. Notifications execute after the lock; the returned revision is the
write's own revision, not a revision read after reentrant subscriber callbacks.

The editor uses the atomic primitive for preview, rollback and history. One
completed drag becomes one undo command, regardless of the number of live moves.
Known editor operations share a revision epoch, allowing consecutive edits to be
undone/redone in order. An external append, replacement, row edit or FIFO eviction
invalidates that epoch. Obsolete history commands report `Conflict` and perform
no write. They are consumed by normal undo/redo traversal rather than throwing
and blocking unrelated older history. Starting a subsequent edit creates a new
epoch; it cannot revive obsolete commands.

External source/axis/model changes supersede captured work. Cancellation rolls
back only when the current state still belongs to the gesture; it does not restore
old data over a newer external update. Previously applied preview values may
remain when a gesture is superseded. This is optimistic editing, not a multi-row
transaction or automatic conflict merge. Model/axis/input changes are owner-thread
operations; concurrent data writers are supported by the storage compare/exchange.

## Native workbench and UI

The **Edit data** module (`?view=editing`) consumes the actual reusable surface,
modifier and external legend. Its compact vector toolbar, navy plot, coordinated
light/dark controls and collapsible point inspector use the existing examples
workbench's visual vocabulary. The inspector shows original index, X/Y, source
revision and editing outcome. At narrower widths it collapses to leave the plot
usable, while toolbar commands remain in a native horizontal scroller.

The gallery cycles line, spline, scatter, column, bar and impulse samples. It
provides explicit Edit/Pan modes, undo/redo, source reset, value-axis log/flip,
axis rotation and theme controls. F6 deliberately performs an ordinary external
source update, so a real keyboard event can exercise conflict handling during
an actual captured pointer gesture. The diagnostic snapshots are read-only
observations, not a JavaScript command bridge.

## Performance scope

The conditional mutation visits only a single row's channels and up to two X
ordering edges. It neither constructs a full source snapshot nor scans the
sample count. At up to 64 channels it uses stack scratch storage. A warmed,
non-tiered core fixture performs 10,000 conditional edits in a one-million-point
series and gates zero managed allocation for the mutation-only interval, without
subscribers or rendering. Evidence is `artifacts/performance/conditional-edits.json`.

This does **not** mean whole-chart dragging is allocation-free or O(1): source
snapshots, hit testing, coordinate projection, geometry construction and rendering
retain their own costs. Presentation still uses the existing Uno Skia host. No
physical-GPU speed, FPS, latency or SciChart performance-equivalence claim is made.
An idle browser check separately verifies that the inspector does not force
continuous chart frames.

## Validation

Core coverage includes snapshot isolation, bit-exact no-ops, ordering and derived
row invariants, FIFO, simultaneous writers, reentrant updates, all nine editable
shapes, eight coordinate combinations, secondary axes, grab offsets, clamping,
foreign pointers, sequential history, cancellation and stale histories.

The dedicated compiled-browser suite drives real mouse/keyboard input and native
toolbar commands. It checks plotted pixels, source changes, undo/redo, Escape,
F6 conflict handling, shape switching, coordinate modes, responsive layout,
themes and unload/reload. The no-input startup suite includes the new module.
Both existing Uno targets, all previous browser suites, native GL/GLES fixtures,
12 packages and isolated package-only consumers remain required before merge.
No new local test run is claimed: the execution environment was unavailable in
this continuation, so validation runs through GitHub Actions on published commits.
