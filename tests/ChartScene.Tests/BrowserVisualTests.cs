using ChartScene.Core;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using ChartScene.Samples;
using SkiaSharp;

public static class BrowserVisualTests
{
    public static void Register(TestSuite suite)
    {
        foreach(var glyph in Enum.GetValues<ChartGlyph>())suite.Run("command glyph draws vector geometry "+glyph,()=>
        {
            var recording=new RecordingCanvas();ChartIconRenderer.Draw(recording,glyph,new(10,20,24,24),ColorRgba.White);
            TestSuite.True(recording.Commands.Count>0);
            TestSuite.True(recording.Commands.All(c=>c.Points.All(p=>p.IsFinite)));
            using var surface=SKSurface.Create(new SKImageInfo(64,64));using var resources=new SkiaResources();
            surface.Canvas.Clear(SKColors.Black);ChartIconRenderer.Draw(new SkiaChartCanvas(surface.Canvas,resources),glyph,new(4,4,56,56),ColorRgba.White);
            using var image=surface.Snapshot();using var bitmap=SKBitmap.FromImage(image);
            int visible=0;for(int y=0;y<64;y++)for(int x=0;x<64;x++)if(bitmap.GetPixel(x,y).Red>40)visible++;
            TestSuite.True(visible>25);
        });
        suite.Run("source browser embeds exact compilable sample catalogue",()=>
        {
            string source=ExampleSource.FactoryCode;
            TestSuite.True(source.Contains("public static class SampleCatalog",StringComparison.Ordinal));
            TestSuite.True(source.Contains("private static ChartModel Financial()",StringComparison.Ordinal));
            TestSuite.True(source.Contains("namespace ChartScene.Samples",StringComparison.Ordinal));
            TestSuite.True(ReferenceEquals(source,ExampleSource.FactoryCode));
            TestSuite.Equal(ExampleSource.FileName,"SampleCatalog.cs");
        });
        suite.Run("command glyph rejects unknown kind",()=>
        {
            TestSuite.Throws<ArgumentOutOfRangeException>(()=>ChartIconRenderer.Draw(new RecordingCanvas(),(ChartGlyph)999,new(0,0,20,20),ColorRgba.White));
        });
    }
}
