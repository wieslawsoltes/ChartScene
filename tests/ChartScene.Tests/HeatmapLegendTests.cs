using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Data;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using SkiaSharp;

public static class HeatmapLegendTests
{
    public static void Register(TestSuite suite)
    {
        static FastHeatMapRenderableSeries Series(params double[] values) => new()
        {
            HeatmapGrid = HeatmapGrid.Uniform(values.Length, 1, 0, 1, 0, 1, (x, _) => values[x]), ColorMap = ColorMap.Viridis
        };
        suite.Run("heatmap scale automatic extents exclude NaN", () =>
        {
            using var series = Series(-10, 3, double.NaN, 20);
            var scale = HeatmapColorScale.Capture(series);
            TestSuite.Equal(scale.Range, new DoubleRange(-10, 20)); TestSuite.True(scale.HasValues);
            TestSuite.Equal(scale.ColorAt(double.NaN), ColorRgba.Transparent);
        });
        suite.Run("heatmap log extents exclude nonpositive values", () =>
        {
            using var series = Series(-10, 0, 1, 10, 100, double.NaN); series.ColorScaleMode = HeatmapColorScaleMode.Logarithmic;
            var scale = HeatmapColorScale.Capture(series); TestSuite.Equal(scale.Range, new DoubleRange(1, 100));
            TestSuite.Near(scale.Fraction(10), .5); TestSuite.Near(scale.ValueAt(.5), 10);
            TestSuite.Equal(scale.ColorAt(0), ColorRgba.Transparent); TestSuite.Equal(scale.ColorAt(-10), ColorRgba.Transparent);
        });
        suite.Run("heatmap explicit scale overrides automatic extents", () =>
        {
            using var series = Series(0, 100); series.ColorMapRange = new(20, 80);
            var scale = HeatmapColorScale.Capture(series); TestSuite.Equal(scale.Range, new DoubleRange(20, 80));
            TestSuite.Near(scale.Fraction(0), 0); TestSuite.Near(scale.Fraction(100), 1);
            series.ColorMapRange = null; TestSuite.Equal(HeatmapColorScale.Capture(series).Range, new DoubleRange(0, 100));
        });
        suite.Run("heatmap all gaps provide valid empty legend scale", () =>
        {
            using var series = Series(double.NaN, double.NaN);
            TestSuite.True(!HeatmapColorScale.Capture(series).HasValues);
            series.ColorScaleMode = HeatmapColorScaleMode.Logarithmic;
            TestSuite.Equal(HeatmapColorScale.Capture(series).Range, new DoubleRange(1, 10));
            series.ColorMapRange = new(2, 3); TestSuite.Equal(HeatmapColorScale.Capture(series).Range, new DoubleRange(2, 3));
        });
        suite.Run("heatmap constant log scales stay positive and representable", () =>
        {
            foreach (double value in new[] { double.Epsilon, 1e-200, 1.0, 1e200, double.MaxValue })
            {
                using var series = Series(value); series.ColorScaleMode = HeatmapColorScaleMode.Logarithmic;
                var scale = HeatmapColorScale.Capture(series);
                TestSuite.True(scale.Range.Min > 0 && scale.Range.IsValid && scale.Range.Contains(value));
                TestSuite.True(double.IsFinite(scale.Fraction(value)) && double.IsFinite(scale.ValueAt(.5)));
            }
        });
        suite.Run("heatmap logarithms preserve adjacent large values", () =>
        {
            double min = 1e200, mid = Math.BitIncrement(min), max = Math.BitIncrement(mid);
            var scale = new HeatmapColorScale(ColorMap.Viridis, new(min, max), HeatmapColorScaleMode.Logarithmic);
            TestSuite.Near(scale.Fraction(mid), .5, 1e-12); TestSuite.Equal(scale.ValueAt(.5), mid);
        });
        foreach (var mode in Enum.GetValues<HeatmapColorScaleMode>()) suite.Run("heatmap color round trips " + mode, () =>
        {
            var scale = new HeatmapColorScale(ColorMap.Viridis, new(1e-150, 1e150), mode);
            for (int i = 1; i < 100; i++) TestSuite.Near(scale.Fraction(scale.ValueAt(i / 100.0)), i / 100.0, 1e-10);
            TestSuite.Equal(scale.ValueAt(0), scale.Range.Min); TestSuite.Equal(scale.ValueAt(1), scale.Range.Max);
            TestSuite.True(double.IsNaN(scale.Fraction(double.PositiveInfinity)) && double.IsNaN(scale.ValueAt(double.NaN)));
        });
        suite.Run("heatmap captured mappings do not change with source", () =>
        {
            using var series = Series(1, 100); var before = HeatmapColorScale.Capture(series);
            series.ColorMapRange = new(3, 30); series.ColorMap = ColorMap.Diverging; series.Opacity = .2;
            TestSuite.Equal(before.Range, new DoubleRange(1, 100)); TestSuite.Equal(before.Opacity, 1.0);
            TestSuite.True(ReferenceEquals(before.ColorMap, ColorMap.Viridis));
            TestSuite.Equal(HeatmapColorScale.Capture(series).Range, new DoubleRange(3, 30));
        });
        suite.Run("heatmap grid replacement refreshes weakly cached extents", () =>
        {
            using var series = Series(0, 10); _ = HeatmapColorScale.Capture(series);
            series.HeatmapGrid = HeatmapGrid.Uniform(2, 1, 0, 1, 0, 1, (x, _) => 20 + x * 10);
            TestSuite.Equal(HeatmapColorScale.Capture(series).Range, new DoubleRange(20, 30));
        });
        suite.Run("heatmap range and mode changes notify and validate atomically", () =>
        {
            using var series = Series(1, 10); using var model = new ChartModel(); model.RenderableSeries.Add(series);
            long before = model.Revision; series.ColorMapRange = new(1, 10); TestSuite.True(model.Revision > before);
            before = model.Revision; series.ColorMapRange = new(1, 10); TestSuite.Equal(model.Revision, before);
            series.ColorScaleMode = HeatmapColorScaleMode.Logarithmic; TestSuite.True(model.Revision > before);
            before = model.Revision;
            TestSuite.Throws<ArgumentOutOfRangeException>(() => series.ColorMapRange = new(0, 10));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => series.ColorMapRange = default(DoubleRange));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => series.ColorScaleMode = (HeatmapColorScaleMode)12);
            TestSuite.Equal(model.Revision, before); TestSuite.Equal(series.ColorMapRange!.Value, new DoubleRange(1, 10));
        });
        suite.Run("heatmap scale rejects unrelated series and invalid transforms", () =>
        {
            using var line = new FastLineRenderableSeries();
            TestSuite.Throws<ArgumentException>(() => HeatmapColorScale.Capture(line));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => new HeatmapColorScale(ColorMap.Viridis, default));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => new HeatmapColorScale(ColorMap.Viridis, new(0, 10), HeatmapColorScaleMode.Logarithmic));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => new HeatmapColorScale(ColorMap.Viridis, new(1, 10), opacity: double.NaN));
        });
        foreach (var orientation in Enum.GetValues<ColorLegendOrientation>()) foreach (bool flipped in new[] { false, true })
        foreach (var mode in Enum.GetValues<HeatmapColorScaleMode>())
        {
            string name = $"{orientation}-{flipped}-{mode}";
            suite.Run("legend projection labels and hit test " + name, () =>
            {
                var canvas = new RecordingCanvas(); var scale = new HeatmapColorScale(ColorMap.Viridis, new(1, 1000), mode);
                var frame = new HeatmapLegendRenderer().Render(canvas, scale, orientation == ColorLegendOrientation.Vertical ? 180 : 800,
                    orientation == ColorLegendOrientation.Vertical ? 600 : 110, new() { Orientation = orientation, Flipped = flipped, Title = "Signal" });
                TestSuite.True(!frame.Bar.IsEmpty); TestSuite.Equal(canvas.SaveDepth, 0);
                foreach (double fraction in new[] { 0.0, .25, .5, .75, 1.0 })
                {
                    double value = scale.ValueAt(fraction), coordinate = frame.Coordinate(value);
                    var point = orientation == ColorLegendOrientation.Vertical ? new PointD(frame.Bar.Center.X, coordinate) : new PointD(coordinate, frame.Bar.Center.Y);
                    TestSuite.Near(frame.HitTest(point)!.Value, value, 1e-10);
                }
                TestSuite.True(frame.HitTest(new(-1, -1)) is null);
                TestSuite.True(frame.Ticks.Any(t => t.Value == 1) && frame.Ticks.Any(t => t.Value == 1000));
                for (int i = 0; i < frame.Ticks.Count; i++) for (int j = i + 1; j < frame.Ticks.Count; j++)
                    TestSuite.True(!frame.Ticks[i].LabelBounds.Intersects(frame.Ticks[j].LabelBounds));
            });
            suite.Run("legend actual raster colors " + name, () =>
            {
                int width = orientation == ColorLegendOrientation.Vertical ? 180 : 800, height = orientation == ColorLegendOrientation.Vertical ? 600 : 110;
                using var surface = SKSurface.Create(new SKImageInfo(width, height)); using var resources = new SkiaResources();
                var scale = new HeatmapColorScale(ColorMap.Viridis, new(1, 1000), mode);
                var frame = new HeatmapLegendRenderer().Render(new SkiaChartCanvas(surface.Canvas, resources), scale, width, height,
                    new() { Orientation = orientation, Flipped = flipped });
                using var image = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(image);
                foreach (double fraction in new[] { .1, .25, .5, .75, .9 })
                {
                    double coordinate = frame.Coordinate(scale.ValueAt(fraction));
                    int x = (int)(orientation == ColorLegendOrientation.Vertical ? frame.Bar.Center.X : coordinate);
                    int y = (int)(orientation == ColorLegendOrientation.Vertical ? coordinate : frame.Bar.Center.Y);
                    var expected = scale.ColorAt(frame.HitTest(new(x + .5, y + .5))!.Value); var actual = bitmap.GetPixel(x, y);
                    NearColor(actual, expected, 4);
                }
                Directory.CreateDirectory("artifacts/color-legends"); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes($"artifacts/color-legends/{name}.png", png.ToArray());
            });
        }
        suite.Run("legend empty and invalid viewport behavior", () =>
        {
            var renderer = new HeatmapLegendRenderer(); var canvas = new RecordingCanvas(); var scale = new HeatmapColorScale(ColorMap.Viridis, new(1, 2));
            TestSuite.True(renderer.Render(canvas, scale, 0, 0).Bar.IsEmpty); TestSuite.Equal(canvas.SaveDepth, 0);
            TestSuite.Throws<ArgumentOutOfRangeException>(() => renderer.Render(canvas, scale, double.NaN, 200));
            TestSuite.Throws<ArgumentOutOfRangeException>(() => renderer.Render(canvas, scale, 100, 200, new() { BarThickness = double.PositiveInfinity }));
        });
        foreach (bool uniform in new[] { true, false }) suite.Run("heatmap raster cache follows legend scale uniform=" + uniform, () =>
        {
            double[] values = [1, 10, 100, 1000, double.NaN, 0, -1];
            double[] edges = uniform ? [0, 1, 2, 3, 4, 5, 6, 7] : [0, 1, 3, 6, 10, 15, 21, 28];
            using var model = new ChartModel { ShowLegend = false };
            using var series = new FastHeatMapRenderableSeries { HeatmapGrid = new(edges, [0, 1], values), ColorMapRange = new(1, 1000), ColorMap = ColorMap.Viridis };
            model.RenderableSeries.Add(series); model.XAxis.VisibleRange = new(0, edges[^1]); model.YAxis.VisibleRange = new(0, 1);
            model.XAxis.AutoRange = model.YAxis.AutoRange = AutoRange.Never;
            var renderer = new ChartRenderer(); using var resources = new SkiaResources();
            using var surface = SKSurface.Create(new SKImageInfo(900, 450));
            foreach (var mode in new[] { HeatmapColorScaleMode.Linear, HeatmapColorScaleMode.Logarithmic, HeatmapColorScaleMode.Linear })
            {
                series.ColorScaleMode = mode; var scale = HeatmapColorScale.Capture(series);
                var frame = renderer.Render(new SkiaChartCanvas(surface.Canvas, resources), model, 900, 450);
                using var image = surface.Snapshot(); using var bitmap = SKBitmap.FromImage(image);
                for (int i = 0; i < values.Length; i++)
                {
                    var p = frame.Layout.Projection(model, series).ToScreen((edges[i] + edges[i + 1]) / 2, .5);
                    var expected = scale.ColorAt(values[i]); if (expected.A == 0) expected = model.Theme.PlotBackground;
                    NearColor(bitmap.GetPixel((int)p.X, (int)p.Y), expected, 2);
                }
            }
        });
        suite.Run("legend opacity uses source alpha", () =>
        {
            var scale = new HeatmapColorScale(new(new(0, new(30, 100, 200, 120)), new(1, new(220, 100, 30, 240))), new(0, 1), opacity: .5);
            TestSuite.Equal(scale.ColorAt(0).A, (byte)60); TestSuite.Equal(scale.ColorAt(1).A, (byte)120);
            TestSuite.Equal(scale.ColorAt(double.NaN).A, (byte)0);
        });
    }
    private static void NearColor(SKColor actual, ColorRgba expected, int tolerance)
    {
        if (Math.Abs(actual.Red - expected.R) > tolerance || Math.Abs(actual.Green - expected.G) > tolerance || Math.Abs(actual.Blue - expected.B) > tolerance)
            throw new Exception($"Expected {expected.Hex}; got {actual}; tolerance {tolerance}.");
    }
}
