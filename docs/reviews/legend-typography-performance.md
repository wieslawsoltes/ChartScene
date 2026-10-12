# Legend typography and settled-update performance

This continuation follows the interactive-legend implementation. It closes native
legend styling gaps and removes unnecessary collection and drawing work; it does
not certify complete SciChart WPF API, template, or pixel equivalence.

## Native styling

The default legend labels now consume the control's normal `Foreground` property,
including a local value, binding or style, rather than replacing it with a chart
palette brush. Hidden labels retain that brush at reduced opacity. Theme changes
update the legend's requested native theme without overwriting a consumer brush.
`FontStyle`, `FontStretch` and `CharacterSpacing` are observed and forwarded in
addition to the existing size, family and weight properties. Custom item templates
remain in control of their own visuals and bindings.

```csharp
var legend = new ChartLegend
{
    ParentSurface = surface,
    FontSize = 18,
    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
    FontStyle = Windows.UI.Text.FontStyle.Italic,
    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 155, 75))
};
```

The public reference describes font/brush customization, native legend rows and
arbitrary row templates: [SciChart WPF LegendModifier documentation](https://www.scichart.com/documentation/win/current/LegendModifier.html).
ChartScene uses its own Uno implementation and vector artwork. These changes bring
its configurable legend closer to that behavior, not to WPF binary compatibility.

## One completed projection, not a rebuild per collection event

`ChartLegendSource.Refreshed` reports the settled membership generation after all
queued reentrant model/filter edits. `IsRefreshing` identifies intermediate
collection notifications. The read-only collection continues publishing granular
changes for other consumers. The native `ChartLegend` synchronizes its children
once at the end rather than repeatedly rebuilding the visual ordering after every
insert/remove event. Detached rows are released in that final synchronization;
filtered rows remain available for reuse.

Pruning entries that no longer belong in the result now precedes survivor
alignment. Excluding the first of 2,048 entries emits one Remove, zero Moves and
zero Resets. The previous ordering moved every survivor before removing the stale
entry. Unchanged native row positions use indexed reference checks, and newly
created rows avoid repeated linear lookup misses.

This is not a constant-time projection or a virtualized legend. Snapshotting and
filter evaluation still visit membership; arbitrary moves and list operations
retain their normal costs. A 256-row browser fixture intentionally instantiates
real native controls and tests that replacing the model takes at most two native
synchronization passes, not one per collection insertion.

## Marker redraws

Each default marker caches the style inputs used by `LegendMarkerRenderer`.
Changing a label, font or foreground no longer explicitly invalidates unchanged
vector markers. Changing stroke, fill, opacity, marker shape/size, line thickness
or color map still invalidates them. Custom marker templates retain their normal
binding behavior. `MarkerInvalidationCount` counts explicit requests only; it does
not count or promise to eliminate host composition, initial loading, or drawing
caused by layout/reattachment.

## Validation and evidence

Core tests cover minimal removal notifications, initial binding, unchanged
metadata, reentrant model replacement, disposal during pruning and completion
callbacks. The isolated 2,048-entry fixture writes
`artifacts/performance/legend-filter.json` with collection-operation counts and
CPU elapsed time. Deterministic work assertions are gates; elapsed time is not a
universal speed guarantee.

The Legends gallery exposes typography and 4/256-channel commands. Browser tests
inspect the actual native TextBlock's font/brush properties, visible layout and
screenshots; verify unchanged row identities and marker-invalidation counts after
text styling; and verify bounded native synchronization on a large replacement.
All previous checkbox/history/template/filter, no-input startup, GPU and package
consumer checks remain enabled. Full final-head CI is required before merge.
