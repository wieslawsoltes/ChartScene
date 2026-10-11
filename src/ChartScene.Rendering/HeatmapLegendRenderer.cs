using ChartScene.Charting;
using ChartScene.Core;

namespace ChartScene.Rendering;

public enum ColorLegendOrientation { Horizontal, Vertical }
public sealed record HeatmapLegendOptions
{
    public ColorLegendOrientation Orientation { get; init; } = ColorLegendOrientation.Vertical;
    public bool Flipped { get; init; }
    public bool ShowLabels { get; init; } = true;
    public bool ShowCheckerboard { get; init; } = true;
    public string Title { get; init; } = "";
    public string LabelFormatting { get; init; } = "G4";
    public double FontSize { get; init; } = 11;
    public double BarThickness { get; init; } = 22;
    public IAxisLabelProvider? LabelProvider { get; init; }
}
public sealed record ColorLegendTick(double Value, string Label, PointD Baseline, RectD LabelBounds);
public sealed record HeatmapLegendFrame(RectD Bar, HeatmapColorScale Scale, ColorLegendOrientation Orientation,
    bool Flipped, IReadOnlyList<ColorLegendTick> Ticks)
{
    public double Coordinate(double value)
    {
        double fraction = Scale.Fraction(value);
        if (Flipped) fraction = 1 - fraction;
        return Orientation == ColorLegendOrientation.Horizontal ? Bar.X + Bar.Width * fraction : Bar.Bottom - Bar.Height * fraction;
    }
    public double? HitTest(PointD point)
    {
        if (Bar.IsEmpty || !point.IsFinite || !Bar.Contains(point)) return null;
        double fraction = Orientation == ColorLegendOrientation.Horizontal
            ? (point.X - Bar.X) / Bar.Width : (Bar.Bottom - point.Y) / Bar.Height;
        double value = Scale.ValueAt(Flipped ? 1 - fraction : fraction);
        return double.IsFinite(value) ? value : null;
    }
}

/// <summary>Backend-independent color legend. A single cached RGBA ramp is shared across range-only changes.</summary>
public sealed class HeatmapLegendRenderer
{
    private (ColorMap Map, double Opacity, int Length, ColorLegendOrientation Orientation, bool Flipped)? _rampKey;
    private RasterImage? _ramp;
    public HeatmapLegendFrame Render(IChartCanvas canvas, HeatmapColorScale scale, double width, double height,
        HeatmapLegendOptions? options = null, ChartTheme? theme = null, double? highlightedValue = null)
    {
        ArgumentNullException.ThrowIfNull(canvas); ArgumentNullException.ThrowIfNull(scale);
        options ??= new(); theme ??= ChartTheme.Dark;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 0 || height < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!Enum.IsDefined(options.Orientation) || !double.IsFinite(options.FontSize) || options.FontSize is < 6 or > 96
            || !double.IsFinite(options.BarThickness) || options.BarThickness is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(options));
        canvas.Clear(theme.Background);
        var empty = new HeatmapLegendFrame(default, scale, options.Orientation, options.Flipped, []);
        if (width < 36 || height < 36) return empty;
        var style = new TextStyle(theme.Foreground, options.FontSize);
        var axis = new AxisModel { VisibleRange = scale.Range, AutoRange = AutoRange.Never,
            AxisAlignment = options.Orientation == ColorLegendOrientation.Vertical ? AxisAlignment.Right : AxisAlignment.Bottom,
            Scale = scale.Mode == HeatmapColorScaleMode.Logarithmic ? AxisScale.Logarithmic : AxisScale.Numeric,
            TextFormatting = options.LabelFormatting, LabelProvider = options.LabelProvider };
        string firstLabel = axis.Format(scale.Range.Min), lastLabel = axis.Format(scale.Range.Max);
        double titleHeight = string.IsNullOrEmpty(options.Title) ? 0 : options.FontSize * 1.7 + 6;
        bool vertical = options.Orientation == ColorLegendOrientation.Vertical;
        double endpointWidth = Math.Max(canvas.MeasureText(firstLabel, style).Width, canvas.MeasureText(lastLabel, style).Width);
        double margin = options.ShowLabels ? Math.Min(width / 3, endpointWidth / 2 + 4) : 10;
        var bar = vertical
            ? new RectD(10, 10 + titleHeight + options.FontSize / 2,
                Math.Min(options.BarThickness, width - 20), Math.Max(0, height - titleHeight - 24 - options.FontSize))
            : new RectD(Math.Max(10, margin), 10 + titleHeight, Math.Max(0, width - 2 * Math.Max(10, margin)),
                Math.Min(options.BarThickness, Math.Max(0, height - titleHeight - 24 - (options.ShowLabels ? options.FontSize * 1.5 : 0))));
        if (bar.IsEmpty) return empty;
        var frame = empty with { Bar = bar };
        canvas.Save(); canvas.Clip(new(0, 0, width, height));
        try
        {
            if (!string.IsNullOrEmpty(options.Title)) canvas.DrawText(options.Title, new(10, options.FontSize + 4), style with { Bold = true });
            if (options.ShowCheckerboard)
            {
                // Bound checkerboard cost even for exceptionally large export surfaces.
                double tile = Math.Max(8, (vertical ? bar.Height : bar.Width) / 256);
                for (int y = 0; y * tile < bar.Height; y++) for (int x = 0; x * tile < bar.Width; x++)
                    canvas.DrawRectangle(new(bar.X + x * tile, bar.Y + y * tile, Math.Min(tile, bar.Width - x * tile), Math.Min(tile, bar.Height - y * tile)),
                        (x + y) % 2 == 0 ? theme.PlotBackground : theme.GridMajor);
            }
            int length = (int)Math.Clamp(Math.Ceiling(vertical ? bar.Height : bar.Width), 2, 4096);
            var key = (scale.ColorMap, scale.Opacity, length, options.Orientation, options.Flipped);
            if (_ramp is null || _rampKey != key)
            {
                var pixels = new byte[length * 4];
                for (int i = 0; i < length; i++)
                {
                    double fraction = i / (double)(length - 1);
                    if (vertical ^ options.Flipped) fraction = 1 - fraction;
                    var color = scale.ColorAtFraction(fraction);
                    pixels[4 * i] = color.R; pixels[4 * i + 1] = color.G; pixels[4 * i + 2] = color.B; pixels[4 * i + 3] = color.A;
                }
                _ramp = new(vertical ? 1 : length, vertical ? length : 1, pixels); _rampKey = key;
            }
            canvas.DrawImage(_ramp, bar, interpolate: true);
            canvas.DrawRectangle(bar, null, new(theme.Border));
            var ticks = new List<ColorLegendTick>();
            if (options.ShowLabels)
            {
                var values = TickGenerator.Generate(axis, Math.Min(10000, vertical ? bar.Height : bar.Width))
                    .Where(t => t.IsMajor && scale.Range.Contains(t.Value)).Select(t => t.Value)
                    .Append(scale.Range.Min).Append(scale.Range.Max).Distinct().ToArray();
                // Give endpoints priority, then omit overlapping interior labels.
                foreach (double value in new[] { scale.Range.Min, scale.Range.Max }.Concat(values.Where(v => v != scale.Range.Min && v != scale.Range.Max)))
                {
                    double position = frame.Coordinate(value); if (!double.IsFinite(position)) continue;
                    string label = axis.Format(value); var measure = canvas.MeasureText(label, style);
                    var baseline = vertical ? new PointD(bar.Right + 8, position + (measure.Ascent - measure.Descent) / 2)
                        : new PointD(position - measure.Width / 2, bar.Bottom + 7 + measure.Ascent);
                    var bounds = new RectD(baseline.X, baseline.Y - measure.Ascent, measure.Width, measure.Ascent + measure.Descent);
                    if (ticks.Any(t => t.LabelBounds.Inflate(3).Intersects(bounds))) continue;
                    ticks.Add(new(value, label, baseline, bounds));
                    if (vertical) canvas.DrawLine(new(bar.Right, position), new(bar.Right + 4, position), new(theme.Foreground));
                    else canvas.DrawLine(new(position, bar.Bottom), new(position, bar.Bottom + 4), new(theme.Foreground));
                    canvas.DrawText(label, baseline, style);
                }
            }
            if (highlightedValue is { } highlighted && scale.Range.Contains(highlighted))
            {
                double coordinate = frame.Coordinate(highlighted);
                if (double.IsFinite(coordinate))
                {
                    var a = vertical ? new PointD(bar.X, coordinate) : new PointD(coordinate, bar.Y);
                    var b = vertical ? new PointD(bar.Right, coordinate) : new PointD(coordinate, bar.Bottom);
                    canvas.DrawLine(a, b, new(ColorRgba.Black, 3)); canvas.DrawLine(a, b, new(ColorRgba.White));
                }
            }
            return frame with { Ticks = ticks.AsReadOnly() };
        }
        finally { canvas.Restore(); }
    }
}
