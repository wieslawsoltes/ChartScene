# Browser accessibility and keyboard navigation

## Observed behavior and corrected diagnosis

Runs `38179439010` and `38180308999` confirmed chart focus but observed no managed
focus transition after the first Shift+Tab. The extra public focus-scope handler
did not change this and has been removed, together with its shell attachment.

Inspection of the exact Uno 6.7.135 browser keyboard source explains why: when
the accessibility opt-in button is present and DOM focus is on the document body,
the host deliberately lets the browser process Tab before dispatching a managed
keyboard event. Reverse Tab from that boundary does not mean reverse traversal
from the managed chart. A managed bubbling handler cannot repair an event the
host intentionally does not dispatch.

## Application behavior

The WebAssembly entry point now sets
`Uno.UI.FeatureConfiguration.AutomationPeer.AutoEnableAccessibility = true`
**before building the host**. This is a normal browser application setting,
not an inputTrace/test-only switch. Uno builds its native semantic DOM without
requiring users to find and activate the screen-reader-only enable button.
Keyboard navigation remains entirely in Uno's original implementation.

No application code removes an accessibility button, assigns focus to a test
target, synthesizes keys, builds a parallel focus tree, or overrides handled
editor keys. Desktop startup is unchanged. The ineffective `KeyboardNavigationScope`
prototype was deleted rather than retained as an unnecessary compatibility layer.

The semantic DOM has a memory/CPU cost. ChartScene's existing chart automation
peer summarizes the chart; this setting does not intentionally create one DOM
element per plotted data point. It is not a zero-cost or FPS improvement claim.

## Validation

The existing real Shift+Tab traversal test is unchanged: it establishes chart
focus through a real pointer click, observes every managed transition, rejects
stalls/cycles/missing focus, and requires search reopening, typing, Escape and
unchanged model identity. All original browser, startup, graphics and consumer
checks remain mandatory. Optional traces collect focus identities only, not text.

These fixtures do not certify NVDA, VoiceOver, all accessible names or complete
screen-reader parity. Broader accessibility qualification remains outstanding.

## Primary pinned contracts

- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI.Runtime.Skia.WebAssembly.Browser/ts/Runtime/BrowserKeyboardInputSource.ts
- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI.Runtime.Skia.WebAssembly.Browser/ts/Runtime/Accessibility.ts
- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI/FeatureConfiguration.cs
