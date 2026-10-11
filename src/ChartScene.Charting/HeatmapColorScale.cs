using System.Runtime.CompilerServices;
using ChartScene.Core;
using ChartScene.Data;

namespace ChartScene.Charting;

public enum HeatmapColorScaleMode { Linear, Logarithmic }

/// <summary>
/// Immutable color/value transform shared by grid rendering, contours and color legends.
/// NaN gaps (and nonpositive logarithmic values) are transparent; finite outliers clamp.
/// Automatic extents are cached weakly per immutable grid, not rescanned by each view.
/// </summary>
public sealed class HeatmapColorScale
{
    private sealed record Extents(double Minimum, double Maximum, double PositiveMinimum,
        double PositiveMaximum, int FiniteCount, int PositiveCount);
    private static readonly ConditionalWeakTable<HeatmapGrid, Extents> ExtentCache = new();
    private readonly double _logSpan;
    public ColorMap ColorMap { get; }
    public DoubleRange Range { get; }
    public HeatmapColorScaleMode Mode { get; }
    public double Opacity { get; }
    public bool HasValues { get; private init; } = true;

    public HeatmapColorScale(ColorMap colorMap, DoubleRange range,
        HeatmapColorScaleMode mode = HeatmapColorScaleMode.Linear, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(colorMap);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!range.IsValid || mode == HeatmapColorScaleMode.Logarithmic && range.Min <= 0)
            throw new ArgumentOutOfRangeException(nameof(range));
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity));
        ColorMap = colorMap; Range = range; Mode = mode; Opacity = opacity;
        _logSpan = mode == HeatmapColorScaleMode.Logarithmic ? LogRatio(range.Max, range.Min) : 0;
    }

    public static HeatmapColorScale Capture(RenderableSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (series.Kind is not (SeriesKind.Heatmap or SeriesKind.Contour))
            throw new ArgumentException("A heatmap color scale requires a heatmap or contour series.", nameof(series));
        bool logarithmic = series.ColorScaleMode == HeatmapColorScaleMode.Logarithmic;
        DoubleRange automatic = logarithmic ? new(1, 10) : DoubleRange.Unit;
        bool hasValues = false;
        if (series.HeatmapGrid is { } grid)
        {
            var extents = ExtentCache.GetValue(grid, Measure);
            hasValues = (logarithmic ? extents.PositiveCount : extents.FiniteCount) != 0;
            if (hasValues && series.ColorMapRange is null)
                automatic = Expand(logarithmic ? extents.PositiveMinimum : extents.Minimum,
                    logarithmic ? extents.PositiveMaximum : extents.Maximum, logarithmic);
        }
        return new(series.ColorMap, series.ColorMapRange ?? automatic, series.ColorScaleMode, series.Opacity)
            { HasValues = hasValues };
    }

    private static Extents Measure(HeatmapGrid grid)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        double positiveMin = double.PositiveInfinity, positiveMax = double.NegativeInfinity;
        int finite = 0, positive = 0;
        foreach (double value in grid.Values.Span)
        {
            if (!double.IsFinite(value)) continue;
            finite++; min = Math.Min(min, value); max = Math.Max(max, value);
            if (value <= 0) continue;
            positive++; positiveMin = Math.Min(positiveMin, value); positiveMax = Math.Max(positiveMax, value);
        }
        return new(min, max, positiveMin, positiveMax, finite, positive);
    }

    private static DoubleRange Expand(double min, double max, bool logarithmic)
    {
        if (!logarithmic || min != max) return DoubleRange.FromExtents(min, max);
        double lo = min * .95, hi = max * 1.05;
        if (lo <= 0 || lo == min) lo = Math.BitDecrement(min) > 0 ? Math.BitDecrement(min) : min;
        if (!double.IsFinite(hi)) hi = max;
        else if (hi == max) hi = double.IsFinite(Math.BitIncrement(max)) ? Math.BitIncrement(max) : max;
        return new(lo, hi);
    }

    // LogP1 retains significance for adjacent large values; subtraction of their logs would not.
    private static double LogRatio(double value, double minimum)
    {
        double relative = (value - minimum) / minimum;
        return relative < .5 ? double.LogP1(relative) : Math.Log(value) - Math.Log(minimum);
    }

    public double Fraction(double value)
    {
        if (!double.IsFinite(value) || Mode == HeatmapColorScaleMode.Logarithmic && value <= 0) return double.NaN;
        if (value <= Range.Min) return 0;
        if (value >= Range.Max) return 1;
        return Mode == HeatmapColorScaleMode.Logarithmic
            ? LogRatio(value, Range.Min) / _logSpan : (value - Range.Min) / Range.Size;
    }

    /// <summary>Maps a finite normalized coordinate to a clamped data value; invalid input returns NaN.</summary>
    public double ValueAt(double fraction)
    {
        if (!double.IsFinite(fraction)) return double.NaN;
        if (fraction <= 0) return Range.Min;
        if (fraction >= 1) return Range.Max;
        double value = Mode == HeatmapColorScaleMode.Linear ? Range.At(fraction)
            : _logSpan < .5 ? Range.Min + Range.Min * double.ExpM1(fraction * _logSpan)
            : Math.Exp(Math.Log(Range.Min) + fraction * _logSpan);
        return Math.Clamp(value, Range.Min, Range.Max);
    }

    public ColorRgba ColorAt(double value) => ColorAtFraction(Fraction(value));
    public ColorRgba ColorAtFraction(double fraction)
    {
        if (!double.IsFinite(fraction)) return ColorRgba.Transparent;
        var color = ColorMap.Sample(fraction);
        return color.WithAlpha((byte)Math.Round(color.A * Opacity));
    }
}
