# Native annotation drafts

The inspector and modal text editor are two independent native text inputs. The
previous modal completion path always copied the model's text into the inspector,
even after Cancel, Escape or hiding the dialog. That discarded an unapplied
inspector draft without a user commit or explicit revert.

Modal Apply continues to use the common text submission and undo path. Cancel,
Escape, unload and a no-op Apply no longer assign the inspector text. A new native
**Revert draft** command explicitly discards only unapplied inspector changes. It
never executes a chart history command and therefore does not erase the existing
redo branch. A themed status label distinguishes unapplied changes from committed
text. Apply is highlighted for a pending draft; Revert is disabled when the native
text is equivalent to the model, including CR/LF representation differences.

Draft state refreshes on native TextChanged and existing model/history refreshes,
not a new timer or chart redraw loop. The page also consumes the frame's direct
identity lookup instead of rescanning annotation regions for diagnostics.

Two added real-input browser scenarios cover draft preservation through styling,
Undo and modal cancellation, and explicit Revert without losing a previous redo.
The existing multiline Apply/Undo/Redo, no-op submissions, dialog Apply, responsive
layout and idle-frame assertions remain enabled. Final-head application CI is
required; this source review alone is not a native browser pass or complete WPF
editor/visual equivalence.
