using ChartScene.Charting;
using ChartScene.Core;
using ChartScene.Rendering;
using ChartScene.Rendering.Skia;
using SkiaSharp;

public static class SkiaTests
{
    public static void Register(TestSuite suite)
    {
        Directory.CreateDirectory("artifacts/rendering");
        foreach(var kind in Enum.GetValues<SeriesKind>()) suite.Run($"Skia PNG {kind}",()=>
        {
            using var model=TestModels.Create(kind); byte[] png=RasterExporter.ExportPng(model,900,600);
            TestSuite.True(png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}));
            using var bitmap=SKBitmap.Decode(png); TestSuite.Equal(bitmap.Width,900); TestSuite.Equal(bitmap.Height,600);
            int colored=0; for(int y=50;y<550;y+=10) for(int x=60;x<850;x+=10) { var pixel=bitmap.GetPixel(x,y); if(pixel.Red>50||pixel.Green>60||pixel.Blue>100) colored++; }
            TestSuite.True(colored>10); File.WriteAllBytes($"artifacts/rendering/{kind}.png",png);
        });
        suite.Run("HarfBuzz shaped multilingual text",()=>
        {
            using var resources=new SkiaResources(); using var surface=SKSurface.Create(new SKImageInfo(600,200)); var canvas=new SkiaChartCanvas(surface.Canvas,resources);
            foreach(string text in new[]{"ChartScene","Zażółć gęślą jaźń","مرحبا","α β γ Ω","first\nsecond"})
            { var metrics=canvas.MeasureText(text,new(ColorRgba.White,16)); TestSuite.True(metrics.Width>0&&metrics.Height>0); canvas.DrawText(text,new(20,40),new(ColorRgba.White,16)); }
        });
        suite.Run("Skia resource reuse and disposal",()=>
        {
            using var resources=new SkiaResources(); using var surface=SKSurface.Create(new SKImageInfo(200,200)); var canvas=new SkiaChartCanvas(surface.Canvas,resources);
            for(int i=0;i<100;i++) { resources.BeginFrame(); canvas.DrawText("cached",new(10,20),new(ColorRgba.White,12)); canvas.DrawLine(new(0,0),new(100,100),new(ColorRgba.White,1,[4,3])); }
        });
    }
}
