using ChartScene.Core;

namespace ChartScene.Rendering;

public enum ChartGlyph { Back, Forward, Home, Menu, Search, Fit, Pan, Select, Annotate, Cursor, Plus, Minus, Play, Pause, Undo, Redo, Source, Download, Info, Close, Star, Line, Columns, Surface, Grid, Settings, TextBold, TextItalic, TextAlignLeft, TextAlignCenter, TextAlignRight }

/// <summary>Independent, resolution-neutral line icons shared by chart controls and raster tests.</summary>
public static class ChartIconRenderer
{
    public static void Draw(IChartCanvas canvas, ChartGlyph glyph, RectD bounds, ColorRgba color)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (!Enum.IsDefined(glyph)) throw new ArgumentOutOfRangeException(nameof(glyph));
        if (bounds.IsEmpty) return;
        double size = Math.Min(bounds.Width, bounds.Height), x = bounds.Center.X - size / 2, y = bounds.Center.Y - size / 2;
        PointD P(double a, double b) => new(x + a * size / 24, y + b * size / 24);
        var stroke = new StrokeStyle(color, Math.Max(.75, size / 15));
        void L(double a, double b, double c, double d) => canvas.DrawLine(P(a,b),P(c,d),stroke);
        void Path(bool close, params double[] values)
        {
            var points = new PointD[values.Length / 2];
            for (int i = 0; i < points.Length; i++) points[i] = P(values[2*i],values[2*i+1]);
            canvas.DrawPolyline(points,stroke,close);
        }
        void Circle(double a,double b,double r) => canvas.DrawEllipse(new(x+(a-r)*size/24,y+(b-r)*size/24,2*r*size/24,2*r*size/24),null,stroke);
        switch (glyph)
        {
            case ChartGlyph.Back: Path(false,15,5,8,12,15,19); break;
            case ChartGlyph.Forward: Path(false,9,5,16,12,9,19); break;
            case ChartGlyph.Home: Path(false,3,11,12,3,21,11); Path(false,6,10,6,21,10,21,10,15,14,15,14,21,18,21,18,10); break;
            case ChartGlyph.Menu: L(4,6,20,6);L(4,12,20,12);L(4,18,20,18);break;
            case ChartGlyph.Search: Circle(10,10,6);L(15,15,21,21);break;
            case ChartGlyph.Fit: Path(false,9,4,4,4,4,9);Path(false,15,4,20,4,20,9);Path(false,4,15,4,20,9,20);Path(false,15,20,20,20,20,15);L(8,12,16,12);L(12,8,12,16);break;
            case ChartGlyph.Pan: L(3,12,21,12);L(12,3,12,21);Path(false,8,7,12,3,16,7);Path(false,8,17,12,21,16,17);Path(false,7,8,3,12,7,16);Path(false,17,8,21,12,17,16);break;
            case ChartGlyph.Select: Path(true,5,3,5,20,10,15,14,22,17,20,13,13,20,13);break;
            case ChartGlyph.Annotate: Path(true,4,17,17,4,21,8,8,21,3,22);L(14,7,18,11);break;
            case ChartGlyph.Cursor: Circle(12,12,5);L(12,2,12,8);L(12,16,12,22);L(2,12,8,12);L(16,12,22,12);break;
            case ChartGlyph.Plus: L(4,12,20,12);L(12,4,12,20);break;
            case ChartGlyph.Minus: L(4,12,20,12);break;
            case ChartGlyph.Play: Path(true,6,3,21,12,6,21);break;
            case ChartGlyph.Pause: L(8,4,8,20);L(16,4,16,20);break;
            case ChartGlyph.Undo: Path(false,8,4,3,9,8,14);Path(false,3,9,15,9,19,11,21,15,19,19);break;
            case ChartGlyph.Redo: Path(false,16,4,21,9,16,14);Path(false,21,9,9,9,5,11,3,15,5,19);break;
            case ChartGlyph.Source: Path(false,8,5,2,12,8,19);Path(false,16,5,22,12,16,19);L(14,3,10,21);break;
            case ChartGlyph.Download: L(12,3,12,16);Path(false,7,11,12,16,17,11);Path(false,4,16,4,21,20,21,20,16);break;
            case ChartGlyph.Info: Circle(12,12,9);L(12,10,12,18);Circle(12,6.5,.5);break;
            case ChartGlyph.Close: L(5,5,19,19);L(19,5,5,19);break;
            case ChartGlyph.Star: Path(true,12,2,15,8,22,9,17,14,18,21,12,18,6,21,7,14,2,9,9,8);break;
            case ChartGlyph.Line: Path(false,3,3,3,21,22,21);Path(false,5,16,9,12,12,16,15,6,19,10,22,5);break;
            case ChartGlyph.Columns: Path(false,3,3,3,21,22,21);Path(true,6,20,6,12,9,12,9,20);Path(true,12,20,12,5,15,5,15,20);Path(true,18,20,18,9,21,9,21,20);break;
            case ChartGlyph.Surface: Path(true,12,2,22,8,22,17,12,23,2,17,2,8);Path(false,2,8,12,14,22,8);L(12,14,12,23);Path(false,3,14,8,10,13,12,18,6);break;
            case ChartGlyph.Grid: for(int i=4;i<=20;i+=8){L(i,4,i,20);L(4,i,20,i);}break;
            case ChartGlyph.TextBold: L(6,4,6,20);Path(false,6,4,14,4,18,7,18,9,14,12,6,12);Path(false,14,12,19,15,19,17,15,20,6,20);break;
            case ChartGlyph.TextItalic: L(10,4,20,4);L(4,20,14,20);L(15,4,9,20);break;
            case ChartGlyph.TextAlignLeft: L(4,5,20,5);L(4,10,14,10);L(4,15,20,15);L(4,20,14,20);break;
            case ChartGlyph.TextAlignCenter: L(4,5,20,5);L(7,10,17,10);L(4,15,20,15);L(7,20,17,20);break;
            case ChartGlyph.TextAlignRight: L(4,5,20,5);L(10,10,20,10);L(4,15,20,15);L(10,20,20,20);break;
            case ChartGlyph.Settings: L(4,5,20,5);L(4,12,20,12);L(4,19,20,19);L(8,2,8,8);L(16,9,16,15);L(10,16,10,22);break;
        }
    }
}
