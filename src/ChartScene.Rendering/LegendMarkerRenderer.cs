using ChartScene.Charting;
using ChartScene.Core;

namespace ChartScene.Rendering;

/// <summary>Backend-independent vector swatches; reads style only, never source samples.</summary>
public static class LegendMarkerRenderer
{
    public static void Draw(IChartCanvas canvas, RenderableSeries series, RectD bounds)
    {
        ArgumentNullException.ThrowIfNull(canvas); ArgumentNullException.ThrowIfNull(series);
        if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom)) return;
        ColorRgba Alpha(ColorRgba color) => color.WithAlpha((byte)Math.Clamp(Math.Round(color.A * series.Opacity), 0, 255));
        var stroke = new StrokeStyle(Alpha(series.Stroke), Math.Min(series.StrokeThickness, 4));
        var middle = bounds.Center;
        canvas.Save();
        try
        {
            canvas.Clip(bounds);
            switch (series.Kind)
            {
                case SeriesKind.Column: case SeriesKind.Bar: case SeriesKind.StackedColumn: case SeriesKind.StackedBar:
                case SeriesKind.Mountain: case SeriesKind.SplineMountain: case SeriesKind.StackedMountain:
                case SeriesKind.Band: case SeriesKind.DigitalBand: case SeriesKind.Pie: case SeriesKind.Donut:
                case SeriesKind.Candlestick: case SeriesKind.BoxPlot: case SeriesKind.GroupedBoxPlot:
                    canvas.DrawRectangle(new(bounds.X + 2, bounds.Y + 3, Math.Max(0, bounds.Width - 4), Math.Max(0, bounds.Height - 6)), Alpha(series.Fill), stroke); break;
                case SeriesKind.Heatmap: case SeriesKind.Contour:
                    for (int i = 0; i < 16; i++)
                        canvas.DrawRectangle(new(bounds.X + bounds.Width * i / 16, bounds.Y + 3, bounds.Width / 16 + .1, Math.Max(0, bounds.Height - 6)), Alpha(series.ColorMap.Sample(i / 15.0)));
                    break;
                case SeriesKind.Scatter: case SeriesKind.Bubble: case SeriesKind.TernaryScatter:
                    canvas.DrawMarkers([middle], series.PointMarker == PointMarker.None ? PointMarker.Ellipse : series.PointMarker,
                        Math.Max(0, Math.Min(12, Math.Min(bounds.Height - 2, series.MarkerSize))), Alpha(series.Stroke), stroke); break;
                default:
                    canvas.DrawLine(new(bounds.X + 2, middle.Y), new(bounds.Right - 2, middle.Y), stroke); break;
            }
            if (series.PointMarker != PointMarker.None && series.Kind is not (SeriesKind.Scatter or SeriesKind.Bubble or SeriesKind.TernaryScatter))
                canvas.DrawMarkers([middle], series.PointMarker, Math.Max(0, Math.Min(12, Math.Min(bounds.Height - 2, series.MarkerSize))), Alpha(series.Stroke), stroke);
        }
        finally { canvas.Restore(); }
    }
}
