using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace ChartScene.Controls;

/// <summary>
/// Opt-in native Tab navigation for a composite control hosted in a Skia/browser root.
/// Uses the host's tab order; it does not create a second focus tree or intercept keys
/// already handled by an editor, popup, modifier or other descendant.
/// </summary>
public static class KeyboardNavigationScope
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(KeyboardNavigationScope), new PropertyMetadata(false, EnabledChanged));

    public static bool GetIsEnabled(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }
    public static void SetIsEnabled(FrameworkElement element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }
    private static void EnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement root)
            throw new ArgumentException("A keyboard navigation scope requires a FrameworkElement.", nameof(sender));
        // Remove before adding makes the property idempotent. The root owns its handler;
        // no static collection retains roots, and disabling the property detaches it.
        root.KeyDown -= OnKeyDown;
        if ((bool)args.NewValue) root.KeyDown += OnKeyDown;
    }
    private static bool Down(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private static void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || args.Key != VirtualKey.Tab || Down(VirtualKey.Control) || Down(VirtualKey.Menu))
            return;
        var root = (FrameworkElement)sender;
        if (!root.IsLoaded || root.XamlRoot?.Content is not { } searchRoot) return;
        var direction = Down(VirtualKey.Shift) ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
        // Next/Previous require the public XamlRoot content, not an arbitrary descendant.
        // The attached element owns the input scope; Uno owns traversal within that window.
        // This also supports attaching the behavior to a nested composite without passing
        // an invalid SearchRoot to the native focus manager.
        // https://github.com/unoplatform/uno/blob/6.7.135/doc/articles/features/focus-management.md
        args.Handled = FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = searchRoot });
    }
}
