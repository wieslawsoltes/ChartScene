namespace ChartScene.Charting;

public enum AnnotationTextWrapping { NoWrap, Wrap, WrapWithOverflow }

public partial class ChartAnnotation
{
    private AnnotationTextWrapping _textWrapping;
    private double _textWrapWidth=160;
    /// <summary>NoWrap preserves hard-line-only layout. Wrap can split long words at grapheme boundaries.</summary>
    public AnnotationTextWrapping TextWrapping
    {
        get=>_textWrapping;
        set { CheckEnum(value); Set(ref _textWrapping,value); }
    }
    /// <summary>Requested content width in logical pixels, excluding padding. Ignored for NoWrap.
    /// A single oversized grapheme (or an unbroken word in WrapWithOverflow) may exceed this width.</summary>
    public double TextWrapWidth
    {
        get=>_textWrapWidth;
        set { Finite(value); if(value<=0)throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _textWrapWidth,value); }
    }
}
