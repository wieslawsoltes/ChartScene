# Composite keyboard navigation

Run `38179439010` at `ae1c2255` confirmed that the first real Shift+Tab from
the focused chart left managed focus on the chart; no focus transition was
reported. The improved fixture therefore exposed a genuine missing native
navigation hand-off, not merely an unverified menu adjacency assumption.
All other workbench tests in its early artifact `11716628510` passed.

`KeyboardNavigationScope.IsEnabled` is an opt-in attached property in the reusable
controls package. The gallery shell enables it once. It handles only an
**unhandled Tab** (Shift selects Previous) and delegates to the public
`FocusManager.TryMoveFocus` overload with `SearchRoot = root.XamlRoot.Content`.
The pinned implementation only permits Next/Previous with the public window
content as the search root; passing an arbitrary nested composite would throw.
The attached element owns the routed-input scope, while Uno owns window traversal.
Native tab ordering, disabled/collapsed controls and focus rules remain owned by Uno; there
is no hardcoded search target, synthetic key, test-only focus assignment or
separate list of focusable controls.

Control/Alt combinations and already-handled editor/descendant keys are left
alone. Successful focus movement marks the routed event handled, preventing a
second traversal by an outer host. A detached root does nothing; disabling the
property removes the handler. The root owns the static delegate and no global
collection retains controls. Popups outside this visual scope are not traversed.

Primary pinned host contracts:

- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI/UI/Xaml/Input/FocusManager.cs
- https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI/UI/Input/InputKeyboardSource.cs
- https://github.com/unoplatform/uno/blob/6.7.135/doc/articles/features/focus-management.md

The existing real-keyboard fixture must pass without changing its stall, cycle,
reachability, reopening, typing, Escape or model-retention assertions. All
existing application, native graphics and package-consumer gates remain required.
This addition is not a full accessibility or cross-platform keyboard-parity
certificate.
