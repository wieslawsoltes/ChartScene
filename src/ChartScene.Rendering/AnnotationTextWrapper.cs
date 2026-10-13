using System.Globalization;
using ChartScene.Charting;

namespace ChartScene.Rendering;

internal readonly record struct MeasuredAnnotationLine(string Text,TextMetrics Metrics);

/// <summary>Measured, grapheme-safe soft wrapping. Never rewrites the model's source text.</summary>
internal static class AnnotationTextWrapper
{
    internal static List<MeasuredAnnotationLine>? Measure(IChartCanvas canvas,string source,TextStyle style,
        AnnotationTextWrapping wrapping,double maxWidth)
    {
        var lines=new List<MeasuredAnnotationLine>();
        foreach(string paragraph in AnnotationTextContent.NormalizeLineEndings(source).Split('\n'))
        {
            var whole=MeasureLine(paragraph);
            if(whole is null)return null;
            if(wrapping==AnnotationTextWrapping.NoWrap||whole.Value.Metrics.Width<=maxWidth)
            { lines.Add(whole.Value);continue; }

            // Parse once per hard paragraph. Prefix probes grow from the CURRENT line;
            // they never repeatedly shape the entire remaining suffix of a long paragraph.
            int[] elements=StringInfo.ParseCombiningCharacters(paragraph);
            int count=elements.Length,start=0;
            bool valid=true;
            while(start<count)
            {
                int fit=Fit(start),end=fit;
                if(!valid)return null;
                if(end<count)
                {
                    int separator=IsSeparator(end)?end:end-1;
                    while(separator>=start&&!IsSeparator(separator))separator--;
                    if(separator>start)
                    {
                        end=separator;
                        while(end>start&&IsSeparator(end-1))end--;
                    }
                    else if(wrapping==AnnotationTextWrapping.WrapWithOverflow)
                    {
                        // No legal word break fits: keep this entire word intact.
                        while(end<count&&!IsSeparator(end))end++;
                    }
                }
                if(end<=start)end=fit; // whitespace-only lines still make progress
                var line=MeasureLine(Slice(start,end));
                if(line is null)return null;
                lines.Add(line.Value);
                start=end;
                // Whitespace at an AUTOMATIC line break is not painted. Hard paragraph
                // indentation and the original source (including all separators) survive.
                while(start<count&&IsSeparator(start))start++;
            }

            int Offset(int index)=>index==count?paragraph.Length:elements[index];
            string Slice(int first,int last)=>paragraph[Offset(first)..Offset(last)];
            bool IsSeparator(int index)
            {
                int offset=Offset(index);
                if(index>=count||Offset(index+1)-offset!=1)return false;
                char c=paragraph[offset];
                return char.IsWhiteSpace(c)&&c is not '\u00A0' and not '\u2007' and not '\u202F';
            }
            bool Fits(int first,int length)
            {
                var candidate=MeasureLine(Slice(first,first+length));
                if(candidate is null){valid=false;return false;}
                return candidate.Value.Metrics.Width<=maxWidth;
            }
            int Fit(int first)
            {
                int remaining=count-first,good=0,probe=1,bad=remaining;
                while(valid)
                {
                    if(!Fits(first,probe)){bad=probe;break;}
                    good=probe;
                    if(probe==remaining)return count;
                    probe=probe<=remaining/2?probe*2:remaining;
                }
                // Never split a combining sequence, surrogate pair or joined emoji.
                if(good==0)return first+1;
                while(valid&&bad-good>1)
                {
                    int middle=good+(bad-good)/2;
                    if(Fits(first,middle))good=middle;else bad=middle;
                }
                return first+good;
            }
        }
        return lines;

        MeasuredAnnotationLine? MeasureLine(string text)
        {
            var m=canvas.MeasureText(text.Length==0?"Mg":text,style);
            if(!double.IsFinite(m.Width)||!double.IsFinite(m.Height)||!double.IsFinite(m.Ascent)||!double.IsFinite(m.Descent)
                ||m.Width<0||m.Height<=0||m.Ascent<0||m.Descent<0)return null;
            return new(text,text.Length==0?m with{Width=0}:m);
        }
    }
}
