using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.WinUI.Graphics3DGL;
using Windows.Foundation;

namespace ChartScene.Controls;

/// <summary>
/// Uno 6.7.135 allocates a framebuffer in Loaded using RenderSize. A stretched child may
/// not have been arranged then. Arrange off-tree before attaching, and unload rather than
/// allocate a zero-sized framebuffer when its host collapses. Normal resizing stays with Uno.
/// Upstream reference: unoplatform/uno tag 6.7.135, GLCanvasElement.OnLoaded/UpdateFramebuffer.
/// </summary>
internal sealed class ArrangedGlHost(FrameworkElement content) : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 0) content.Measure(availableSize);
        return new(double.IsFinite(availableSize.Width) ? availableSize.Width : 0,
            double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!double.IsFinite(finalSize.Width) || !double.IsFinite(finalSize.Height)
            || finalSize.Width < 1 || finalSize.Height < 1)
        {
            Children.Clear();
            return finalSize;
        }
        if (Children.Count == 0)
        {
            content.Measure(finalSize);
            content.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            Children.Add(content);
        }
        else content.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }
}

internal static class BrowserGlReadback
{
    /// <summary>
    /// The pinned host assumes its JS resolver swaps BGRA; native GLES resolution does not.
    /// Select Uno's existing RGBA read + red/blue conversion, valid for either resolver.
    /// This contract is deliberately version-checked and must be requalified on Uno upgrades.
    /// Reference implementation: wieslawsoltes/CadSpace, PortableGlCanvasElement (MIT).
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, typeof(GLCanvasElement))]
    public static void Configure(GLCanvasElement canvas)
    {
        if (!OperatingSystem.IsBrowser()) return;
        var field = typeof(GLCanvasElement).GetField("_readbackAsRgbaWithSwap", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.FieldType != typeof(bool))
            throw new NotSupportedException("The Uno GL readback contract changed. Requalify the RGBA adapter before upgrading Uno.");
        field.SetValue(canvas, true);
        Console.WriteLine("CHARTSCENE_GL_READBACK: RGBA with BGRA conversion");
    }
}
