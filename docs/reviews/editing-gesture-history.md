# Editing gestures and immediate history commands

## Evidence and reproduction

Application run `38286078681` tested PR #11 head `6a711cee89a7f95d69327f20eac5d87cbe995d7f`.
Its preserved results show 17/18 editing cases passing. The remaining case failed
before its first drag: the chart had a zoomed viewport and a navigation-history
entry even though the preceding marker click made no source change. The native
surface routes `DoubleTapped` as `ChartInputKind.DoubleClick`; after a completed
edit the editor did not consume that input, so the ordinary zoom-extents modifier
could run between rapid point gestures.

A new core regression sends Down, Up and the native-style DoubleClick (without
an original pointer ID). It fails on the prior implementation because X changes
from `[0, 2]` to `[-0.1, 2.1]`. Editing mode now consumes plot double-clicks without
changing source data, ranges or history. The editor-disabled case continues to
exercise ordinary zoom-extents behavior. Browser coverage deliberately delivers a
native double-click, requires a managed `doubletap` observation, verifies unchanged
source coordinates/history, and then performs another real edit and undo/redo.
No additional delay is used to avoid the double-tap path.

The same saved run reports six legend failures. The first attempted to move to
Undo immediately after changing a checkbox: history already allowed Undo, but
button enablement was updated only by a 200 ms diagnostic timer. A move over the
still-disabled button could not be acknowledged, and subsequent cases inherited
the interrupted state. The series model itself had correctly changed visibility.

## Fix and ownership

The editing and legend pages observe their current `UndoManager.Changed` directly.
Undo/Redo enablement updates in the history notification rather than on a timer.
The subscriptions move with model replacement and detach on page unload.
Unchanged diagnostic ticks only publish observations and no longer refresh every
toolbar command. The existing native buttons remain native buttons; input tests
are not replaced with JavaScript commands.

Browser diagnostics now expose the actual `undoEnabled` and `redoEnabled` button
properties beside `canUndo`/`canRedo`. Existing real-input tests assert those native
properties immediately after completing edits and changing visibility, before
clicking the commands. The bounded opt-in input trace also observes native double
taps; it does not change focus, intercept events or collect typed text.

## Validation scope

The recovered pending data-validation commit was verified against source tree
`75fced3a5b8d6844f18367793e7c181f921fefad` and its 712 core tests passed locally.
The new double-click regression first reproduced the defect (713/714 passing),
and the corrected implementation passes all 714 core tests. Python suites also
pass syntax compilation. Compiled Uno desktop/browser, all browser suites,
GL/GLES rendering and isolated package-only consumers remain mandatory CI gates.

The restored local SDK is 10.0.401 and uses recovered package versions. Offline
restore disabled auditing, package pruning and downloading absent targeting packs
only for that local invocation; repository dependency versions, workflows and test
assertions were not weakened. Local browser navigation remains unavailable under
its administrative restriction and is not used as validation evidence.

This change removes a command-enablement race and avoids redundant periodic UI
refreshes. It does not establish complete reference UI or hardware performance
parity. The parent point-editing implementation retains its documented Cartesian
Y-editing and optimistic-conflict boundaries.
