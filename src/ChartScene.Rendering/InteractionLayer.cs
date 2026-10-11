using ChartScene.Charting;

namespace ChartScene.Rendering;

public sealed partial class ChartRenderer
{
    /// <summary>Draw the transient cursor/rollover/selection layer over an already rendered scene.</summary>
    public static void RenderInteraction(IChartCanvas canvas, ChartModel model, ChartFrame frame, ChartViewState view)
    {
        ArgumentNullException.ThrowIfNull(canvas); ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(view);
        if (frame.Diagnostics.Count != 0 || frame.Layout.PlotArea.IsEmpty) return;
        DrawInteraction(canvas, model, frame.Layout, view);
    }
}
