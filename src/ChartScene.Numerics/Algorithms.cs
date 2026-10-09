using System.Numerics;
using ChartScene.Core;
using ChartScene.Data;

namespace ChartScene.Numerics;

public static class Resampling
{
    /// <summary>Pixel-bucket min/max in source order, retaining endpoint samples and every NaN break.</summary>
    public static int[] MinMax(SeriesSnapshot data,int yChannel,DoubleRange visible,int pixels)
    {
        if(pixels<1) throw new ArgumentOutOfRangeException(nameof(pixels));
        var(start,end)=data.VisibleIndices(visible);
        if(end-start<=pixels*4||!data.IsSortedAscending) return Enumerable.Range(start,end-start).ToArray();
        var result=new List<int>(pixels*4+8);
        int bucket=int.MinValue,first=-1,last=-1,min=-1,max=-1;
        void Flush()
        {
            if(first<0) return;
            Span<int> candidates=stackalloc int[4] {first,min,max,last}; candidates.Sort();
            foreach(int i in candidates) if(i>=0&&(result.Count==0||result[^1]!=i)) result.Add(i);
            first=last=min=max=-1;
        }
        for(int i=start;i<end;i++)
        {
            double y=data[yChannel,i];
            if(!double.IsFinite(y)) { Flush(); result.Add(i); bucket=int.MinValue; continue; }
            int next=(int)Math.Clamp(Math.Floor(visible.Fraction(data[0,i])*pixels),-1,pixels);
            if(next!=bucket) { Flush(); bucket=next; }
            if(first<0) first=min=max=i;
            last=i;
            if(y<data[yChannel,min]) min=i;
            if(y>data[yChannel,max]) max=i;
        }
        Flush(); return result.ToArray();
    }
    /// <summary>Largest-triangle-three-buckets, separately applied to each continuous finite run.</summary>
    public static int[] Lttb(SeriesSnapshot data,int yChannel,int threshold)
    {
        if(threshold<3) throw new ArgumentOutOfRangeException(nameof(threshold));
        if(data.Count<=threshold) return Enumerable.Range(0,data.Count).ToArray();
        var result=new List<int>(); int start=0;
        while(start<data.Count)
        {
            if(!double.IsFinite(data[yChannel,start])) { result.Add(start++); continue; }
            int end=start+1; while(end<data.Count&&double.IsFinite(data[yChannel,end])) end++;
            int count=end-start, target=Math.Min(count,Math.Max(3,(int)Math.Round((double)threshold*count/data.Count)));
            if(count<=target) { for(int i=start;i<end;i++) result.Add(i); start=end; continue; }
            double every=(count-2.0)/(target-2); int a=start; result.Add(a);
            for(int bucket=0;bucket<target-2;bucket++)
            {
                int avgStart=Math.Min(end,(int)Math.Floor((bucket+1)*every)+start+1),avgEnd=Math.Min(end,(int)Math.Floor((bucket+2)*every)+start+1);
                double avgX=0,avgY=0; int n=Math.Max(1,avgEnd-avgStart);
                for(int i=avgStart;i<avgEnd;i++) { avgX+=data[0,i]/n; avgY+=data[yChannel,i]/n; }
                if(avgStart==avgEnd) { avgX=data[0,end-1]; avgY=data[yChannel,end-1]; }
                int from=(int)Math.Floor(bucket*every)+start+1,to=Math.Min(end-1,(int)Math.Floor((bucket+1)*every)+start+1),chosen=from;
                double area=-1;
                for(int i=from;i<to;i++)
                {
                    double candidate=Math.Abs((data[0,a]-avgX)*(data[yChannel,i]-data[yChannel,a])-(data[0,a]-data[0,i])*(avgY-data[yChannel,a]));
                    if(candidate>area) { area=candidate; chosen=i; }
                }
                result.Add(chosen); a=chosen;
            }
            result.Add(end-1); start=end;
        }
        return result.ToArray();
    }
}

public static class Statistics
{
    public static (int Count,double Mean,double Variance,double Min,double Max) Describe(ReadOnlySpan<double> values)
    {
        int count=0; double mean=0,m2=0,min=double.PositiveInfinity,max=double.NegativeInfinity;
        foreach(double x in values)
        {
            if(!double.IsFinite(x)) continue;
            count++; double delta=x-mean; mean+=delta/count; m2+=delta*(x-mean); min=Math.Min(min,x); max=Math.Max(max,x);
        }
        return (count,count>0?mean:double.NaN,count>1?m2/(count-1):double.NaN,count>0?min:double.NaN,count>0?max:double.NaN);
    }
    public static double Quantile(ReadOnlySpan<double> values,double q)
    {
        if(q<0||q>1||!double.IsFinite(q)) throw new ArgumentOutOfRangeException(nameof(q));
        double[] sorted=values.ToArray().Where(double.IsFinite).Order().ToArray();
        if(sorted.Length==0) return double.NaN;
        double index=(sorted.Length-1)*q; int lo=(int)index,hi=Math.Min(lo+1,sorted.Length-1);
        return sorted[lo]+(sorted[hi]-sorted[lo])*(index-lo);
    }
    public static (double Slope,double Intercept,double RSquared) LinearRegression(ReadOnlySpan<double> x,ReadOnlySpan<double> y)
    {
        if(x.Length!=y.Length) throw new ArgumentException("X and Y lengths differ.");
        int n=0; double mx=0,my=0,sxx=0,syy=0,sxy=0;
        for(int i=0;i<x.Length;i++)
        {
            if(!double.IsFinite(x[i])||!double.IsFinite(y[i])) continue;
            n++; double dx=x[i]-mx,dy=y[i]-my; mx+=dx/n; my+=dy/n; sxx+=dx*(x[i]-mx); syy+=dy*(y[i]-my); sxy+=dx*(y[i]-my);
        }
        if(n<2||sxx==0) return(double.NaN,double.NaN,double.NaN);
        double slope=sxy/sxx;
        return(slope,my-slope*mx,syy==0?1:Math.Clamp(sxy*sxy/(sxx*syy),0,1));
    }
}

public static class Indicators
{
    public static double[] SimpleMovingAverage(ReadOnlySpan<double> values,int period)
    {
        if(period<1) throw new ArgumentOutOfRangeException(nameof(period));
        var output=new double[values.Length]; double sum=0; int finite=0;
        for(int i=0;i<values.Length;i++)
        {
            if(double.IsFinite(values[i])) { sum+=values[i]; finite++; }
            if(i>=period&&double.IsFinite(values[i-period])) { sum-=values[i-period]; finite--; }
            output[i]=i+1>=period&&finite==period?sum/period:double.NaN;
        }
        return output;
    }
    public static double[] ExponentialMovingAverage(ReadOnlySpan<double> values,int period)
    {
        if(period<1) throw new ArgumentOutOfRangeException(nameof(period));
        var output=new double[values.Length]; double alpha=2.0/(period+1),state=double.NaN;
        for(int i=0;i<values.Length;i++)
        {
            if(!double.IsFinite(values[i])) { output[i]=double.NaN; state=double.NaN; continue; }
            state=double.IsFinite(state)?alpha*values[i]+(1-alpha)*state:values[i]; output[i]=state;
        }
        return output;
    }
    public static double[] RelativeStrengthIndex(ReadOnlySpan<double> values,int period=14)
    {
        if(period<1) throw new ArgumentOutOfRangeException(nameof(period));
        var output=Enumerable.Repeat(double.NaN,values.Length).ToArray(); double gain=0,loss=0; int count=0;
        for(int i=1;i<values.Length;i++)
        {
            if(!double.IsFinite(values[i])||!double.IsFinite(values[i-1])) { count=0; gain=loss=0; continue; }
            double change=values[i]-values[i-1],g=Math.Max(0,change),l=Math.Max(0,-change);
            if(count<period) { gain+=g; loss+=l; count++; if(count<period) continue; gain/=period; loss/=period; }
            else { gain=(gain*(period-1)+g)/period; loss=(loss*(period-1)+l)/period; }
            output[i]=loss==0?(gain==0?50:100):100-100/(1+gain/loss);
        }
        return output;
    }
    public static (double[] Lower,double[] Middle,double[] Upper) BollingerBands(ReadOnlySpan<double> values,int period=20,double deviations=2)
    {
        if(period<2||deviations<0||!double.IsFinite(deviations)) throw new ArgumentOutOfRangeException(nameof(period));
        double[] middle=SimpleMovingAverage(values,period),lower=new double[values.Length],upper=new double[values.Length];
        for(int i=0;i<values.Length;i++)
        {
            if(!double.IsFinite(middle[i])) { lower[i]=upper[i]=double.NaN; continue; }
            double sum=0; for(int j=i-period+1;j<=i;j++) { double d=values[j]-middle[i]; sum+=d*d; }
            double sd=Math.Sqrt(sum/period); lower[i]=middle[i]-deviations*sd; upper[i]=middle[i]+deviations*sd;
        }
        return(lower,middle,upper);
    }
    public static (double[] Macd,double[] Signal,double[] Histogram) Macd(ReadOnlySpan<double> values,int fast=12,int slow=26,int signal=9)
    {
        if(fast>=slow) throw new ArgumentException("Fast period must be less than slow period.");
        double[] a=ExponentialMovingAverage(values,fast),b=ExponentialMovingAverage(values,slow),macd=new double[values.Length];
        for(int i=0;i<macd.Length;i++) macd[i]=a[i]-b[i];
        double[] s=ExponentialMovingAverage(macd,signal),h=new double[macd.Length];
        for(int i=0;i<h.Length;i++) h[i]=macd[i]-s[i];
        return(macd,s,h);
    }
}

public enum WindowFunction { Rectangular,Hann,Hamming,Blackman }
public static class Fourier
{
    /// <summary>In-place O(N log N) transform, radix-2 or Bluestein for arbitrary lengths.</summary>
    public static void Transform(Span<Complex> values,bool inverse=false)
    {
        if(values.Length==0) return;
        if(inverse) { for(int i=0;i<values.Length;i++) values[i]=Complex.Conjugate(values[i]); Transform(values); for(int i=0;i<values.Length;i++) values[i]=Complex.Conjugate(values[i])/values.Length; return; }
        int n=values.Length;
        if((n&(n-1))==0) { Radix2(values); return; }
        int m=1; while(m<checked(2*n-1)) m=checked(m*2);
        var a=new Complex[m]; var b=new Complex[m];
        for(int i=0;i<n;i++)
        {
            double angle=Math.PI*((long)i*i%(2L*n))/n; var w=Complex.FromPolarCoordinates(1,angle);
            a[i]=values[i]*Complex.Conjugate(w); b[i]=w; if(i!=0) b[m-i]=w;
        }
        Radix2(a); Radix2(b); for(int i=0;i<m;i++) a[i]*=b[i]; Transform(a,true);
        for(int i=0;i<n;i++) values[i]=a[i]*Complex.FromPolarCoordinates(1,-Math.PI*((long)i*i%(2L*n))/n);
    }
    private static void Radix2(Span<Complex> values)
    {
        int n=values.Length;
        for(int i=1,j=0;i<n;i++)
        {
            int bit=n>>1; for(;(j&bit)!=0;bit>>=1) j^=bit; j^=bit;
            if(i<j) (values[i],values[j])=(values[j],values[i]);
        }
        for(int len=2;len<=n;len=checked(len*2))
        {
            Complex root=Complex.FromPolarCoordinates(1,-2*Math.PI/len);
            for(int start=0;start<n;start+=len)
            {
                Complex w=Complex.One;
                for(int j=0;j<len/2;j++) { Complex u=values[start+j],v=values[start+j+len/2]*w; values[start+j]=u+v; values[start+j+len/2]=u-v; w*=root; }
            }
            if(len==n) break;
        }
    }
    public static double[] AmplitudeSpectrum(ReadOnlySpan<double> samples,WindowFunction window=WindowFunction.Hann)
    {
        if(samples.Length<2) throw new ArgumentException("At least two samples are required.");
        var data=new Complex[samples.Length]; double gain=0;
        for(int i=0;i<samples.Length;i++)
        {
            if(!double.IsFinite(samples[i])) throw new ArgumentException("FFT input must be finite.");
            double phase=2*Math.PI*i/(samples.Length-1);
            double weight=window switch {WindowFunction.Hann=>.5-.5*Math.Cos(phase),WindowFunction.Hamming=>.54-.46*Math.Cos(phase),WindowFunction.Blackman=>.42-.5*Math.Cos(phase)+.08*Math.Cos(2*phase),_=>1};
            gain+=weight; data[i]=new(samples[i]*weight,0);
        }
        if(gain<=0) throw new ArgumentException("The chosen window has zero gain at this sample count.");
        Transform(data); var result=new double[samples.Length/2+1];
        for(int i=0;i<result.Length;i++) result[i]=data[i].Magnitude/gain*(i==0||samples.Length%2==0&&i==samples.Length/2?1:2);
        return result;
    }
}

/// <summary>Shape-preserving cubic Hermite interpolation (Fritsch-Carlson slope limiting).</summary>
public sealed class MonotoneSpline
{
    private readonly double[] _x,_y,_m;
    public MonotoneSpline(double[] x,double[] y)
    {
        if(x.Length!=y.Length||x.Length<2) throw new ArgumentException("At least two matching samples are required.");
        for(int i=0;i<x.Length;i++) if(!double.IsFinite(x[i])||!double.IsFinite(y[i])||i>0&&x[i]<=x[i-1]) throw new ArgumentException("Spline samples require increasing finite X and finite Y.");
        _x=(double[])x.Clone(); _y=(double[])y.Clone(); _m=new double[x.Length]; var d=new double[x.Length-1];
        for(int i=0;i<d.Length;i++) d[i]=(y[i+1]-y[i])/(x[i+1]-x[i]);
        _m[0]=d[0]; _m[^1]=d[^1];
        for(int i=1;i<_m.Length-1;i++) _m[i]=d[i-1]*d[i]<=0?0:(d[i-1]+d[i])/2;
        for(int i=0;i<d.Length;i++)
        {
            if(d[i]==0) { _m[i]=_m[i+1]=0; continue; }
            double a=_m[i]/d[i],b=_m[i+1]/d[i],h=Math.Sqrt(a*a+b*b);
            if(h>3) { double t=3/h; _m[i]=t*a*d[i]; _m[i+1]=t*b*d[i]; }
        }
    }
    public double Evaluate(double x)
    {
        if(!double.IsFinite(x)) return double.NaN;
        if(x<=_x[0]) return _y[0]; if(x>=_x[^1]) return _y[^1];
        int index=Array.BinarySearch(_x,x); if(index>=0) return _y[index]; int i=~index-1;
        double h=_x[i+1]-_x[i],t=(x-_x[i])/h,t2=t*t,t3=t2*t;
        return (2*t3-3*t2+1)*_y[i]+(t3-2*t2+t)*h*_m[i]+(-2*t3+3*t2)*_y[i+1]+(t3-t2)*h*_m[i+1];
    }
}
