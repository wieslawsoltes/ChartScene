# Unclipped annotation interaction

Text with `ClipToPlot = false` can be painted in the surface's axis or header
margins. Its measured hit region must remain interactive there; the original
plot-only annotation guard instead routed a visible text body to an axis drag.

A standalone probe places a right-anchored relative label at the left plot edge.
Its body center is inside the surface but outside the plot. Before the fix,
measured distance is zero but the annotation stays unselected and the controller
captures axis navigation. After the fix, the same input selects and captures the
annotation. The regression then drags it, checks unchanged axis ranges and undoes
the annotation move.

Only measured, visible, editable text can enter the margin path. New annotations
and other annotation kinds still begin inside the plot. The layout's actual clip
rejects pixels outside the surface and clipped portions of plot-only labels.
This does not introduce arbitrary annotation templates or gesture transaction
semantics.
