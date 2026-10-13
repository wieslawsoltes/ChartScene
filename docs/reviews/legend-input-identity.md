# Native checkbox identity during filtered layout

The preserved Application artifact for run 38353191163 reports 310/312 passing
browser cases. All 18 text-annotation cases passed. The first failure was in the
legend's three-cycle empty-filter recovery; reattachment then inherited that
interrupted filtered state. The saved state has Sine and Envelope hidden while
Cosine and Samples remain visible. The intended next toggle did not reach its
expected state.

The old helper acknowledged input on the entire legend host. That proves neither
which native checkbox received the input nor that a retained row's nonempty
rectangle belongs to the latest filtered layout. Removing a visible row shifts
its survivors. The observed pattern is consistent with a click using the preceding
layout and reaching a different checkbox; the old host-level trace cannot prove
which checkbox received it.

Each native checkbox, including those supplied by a compiled custom row template,
now has its own opt-in routed-input observation keyed by stable entry identity.
No checkbox state, selection, focus or layout is set by the observation. The
existing weak registration attaches handlers once per native object and releases
detached controls normally. Snapshot construction locates the row and checkbox
once instead of traversing the row twice.

Browser toggles require a Move acknowledged by that exact native checkbox before
one Down/Up pair. If filtering changed its published rectangle, the helper
repositions without repeating the click. When a new row occupies the pointer's
unchanged screen coordinate, one real in-bounds approach and return generates a
fresh routed move; no JavaScript event or application command is synthesized.
Every toggle also asserts that other series' visibility remains unchanged.

The recovery test retains all three cycles, stable entry/row assertions and its
original timeout. Reattachment explicitly requires All mode and all source series
visible, rather than a vacuous all-visible assertion over filtered entries. No
numerical tolerance, pixel threshold, test or deployment gate is relaxed. Final
head native browser validation is still required before merge.
