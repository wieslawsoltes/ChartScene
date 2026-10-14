# Lossless viewport drafts and native example properties

This continuation starts from PR #16, merged as
`29bc479bc8e8cfe2c80b3d967be88e39bb494160`. Its main Application run
`38466730716` completed deployment and public build-identity verification.
The previously blocked general history-command follow-up is excluded and has
not been retried. The implementation here is a separate, range-specific editor;
the generic history implementation is unchanged.

## Defects and reusable contract

The main example inspector formerly formatted ranges with `G8` and refreshed
only when loading an example. Applying untouched fields could round a viewport,
or restore an obsolete range after pan, zoom or streaming. A combined application
could also change the X range before a later invalid logarithmic Y range threw.

`ViewportRangeEditor` owns independent `AxisRangeDraft` objects for the current
primary X/Y axes. They format numerical endpoints with invariant round-trip `R`,
cache parsed results and retain incomplete input across unrelated renders. Input
is bounded to 128 UTF-16 code units per field. Parsing allows signs, a decimal
point, exponents and surrounding whitespace, but not grouping commas. Finite
endpoints, positive finite spans and positive logarithmic domains are checked
before either participating axis is written.

Clean axes follow the live viewport. Dirty axes keep their exact input and report
conflicts when their captured range, automatic policy, scale or logarithm base
changes. A clean X axis can therefore keep following a stream while an unrelated
Y draft remains editable. Revert adopts the current viewport without touching
chart history. Formatting-only edits are not range edits and cannot disable
AutoRange, create an undo entry or clear a redo branch.

Only semantically changed axes enter one range-specific command. Manual changes
select AutoRange.Never on those axes; undo restores their original ranges and
automatic policies. Untouched axes are not written during application or undo.
Every participating state is validated before the first write and after each
setter callback, and once more after the model notification batch is released.
A superseding callback stops subsequent writes; rollback changes only fields
whose current value still matches this command's assignment. Old commands refuse
to write through disposed editors or replaced primary-axis identities.

The host calls Refresh on its UI thread and disposes the editor when replacing
the model. Unloading a reusable page is not model replacement. Draft observation
has no data-series subscriptions and reads no source snapshots.

## Native workbench

The main inspector now has a pinned EXAMPLE PROPERTIES header and Close button,
with a native ScrollViewer for the existing options and range controls. Wide and
compact visibility preferences are independent. At widths below 1,150 logical
pixels the same pane is an overlay rather than a column, so opening it does not
resize the chart. No duplicate inspector, draft or model is constructed on toggle.

Native range fields have accessible names, bounded input, inline error/conflict
messages, explicit Apply/Revert enablement and displayed scale/automatic policies.
Enter applies valid changes; Escape in a range field reverts its draft without
bubbling into pane dismissal. Clean fields refresh after navigation while equal
strings are not reassigned, and dirty input is not replaced during a redraw,
source-code view, module reattachment or properties-pane toggle. Property updates
are skipped when both draft versions remain unchanged.

Browser diagnostics observe actual native TextBox strings, enabled buttons and
layout rectangles. Added cases use real mouse, keyboard and native scrolling;
no application-state assignment, synthetic control events or DOM scrolling is
used. The prior workbench cases remain registered with the same assertions,
timeouts and presentation thresholds.

## Performance and verification scope

Two deterministic warmed fixtures record three 10,000-query windows: unchanged
Refresh with an invalid pending draft, and unchanged Apply. They assert zero
managed allocation; Refresh also asserts zero source-snapshot reads and model
notifications, and Apply asserts no history notifications. The edit-only command
assembly uses a static helper and is reached only after the no-op/invalid/conflict
checks. These fixtures do not measure changed-range parsing/formatting, history
creation, control layout, shaping, rendering, GPU execution or presentation.
There is no whole-application zero-allocation or FPS claim.

Local source/tree checks and Python compilation are available. This session has
no local .NET SDK; use final-head GitHub Validate/Application results for C#/Skia,
Uno desktop/browser, all browser suites, native GL/GLES, twelve packages and both
isolated package-only consumers. The PR must remain draft until those gates pass.
No existing test, numeric/pixel tolerance, timeout or workflow gate is waived.

## Boundaries

This edits the primary axes' numerical model units, not arbitrary secondary-axis
selection, locale-specific dates/durations or complete WPF dependency-property
bindings/templates. Date/time values remain the model's millisecond units and
category values remain numerical positions; this is not typed SciChart range
interchange. General transaction isolation, concurrent writers, document epochs
and ABA detection are not implemented. A stale history command is a no-op under
the existing history contract rather than removing itself from the history stack.
Physical touch, screen-reader and cross-platform font/device qualification remain
separate. Full SciChart API, pixel-identical UI and hardware-performance parity
have not been established.

## Public references

- SciChart axis ranging and automatic policies:
  https://www.scichart.com/documentation/win/current/Axis%20Ranging%20-%20AutoRange%20and%20VisibleRange.html
- Microsoft standard numeric round-trip format:
  https://learn.microsoft.com/en-us/dotnet/standard/base-types/standard-numeric-format-strings#round-trip-format-specifier-r
- Microsoft Double.TryParse overloads and NumberStyles:
  https://learn.microsoft.com/en-us/dotnet/api/system.double.tryparse?view=net-10.0

These are public behavioral references, not copied proprietary assets or proof
of unexecuted reference equivalence.
