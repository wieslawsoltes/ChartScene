using System.Numerics;
using ChartScene.Core;

namespace ChartScene.ThreeD;

/// <summary>Data coordinates remain double precision until rebased and normalized GPU buffers are generated.</summary>
public readonly record struct Vector3D(double X,double Y,double Z)
{
    public static Vector3D Zero=>new(0,0,0);
    public static Vector3D UnitX=>new(1,0,0);
    public static Vector3D UnitY=>new(0,1,0);
    public static Vector3D UnitZ=>new(0,0,1);
    public bool IsFinite=>double.IsFinite(X)&&double.IsFinite(Y)&&double.IsFinite(Z);
    public double LengthSquared=>X*X+Y*Y+Z*Z;
    public double Length=>double.Hypot(double.Hypot(X,Y),Z);
    public Vector3D Normalized { get { double length=Length; return length>1e-300&&double.IsFinite(length)?this/length:Zero; } }
    public Vector3 ToSingle()=>new((float)X,(float)Y,(float)Z);
    public static Vector3D FromSingle(Vector3 value)=>new(value.X,value.Y,value.Z);
    public static Vector3D operator +(Vector3D a,Vector3D b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Vector3D operator -(Vector3D a,Vector3D b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    public static Vector3D operator -(Vector3D a)=>new(-a.X,-a.Y,-a.Z);
    public static Vector3D operator *(Vector3D a,double b)=>new(a.X*b,a.Y*b,a.Z*b);
    public static Vector3D operator /(Vector3D a,double b)=>new(a.X/b,a.Y/b,a.Z/b);
    public static double Dot(Vector3D a,Vector3D b)=>a.X*b.X+a.Y*b.Y+a.Z*b.Z;
    public static Vector3D Cross(Vector3D a,Vector3D b)=>new(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);
    public static Vector3D Min(Vector3D a,Vector3D b)=>new(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Min(a.Z,b.Z));
    public static Vector3D Max(Vector3D a,Vector3D b)=>new(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y),Math.Max(a.Z,b.Z));
    public double Component(int axis)=>axis switch {0=>X,1=>Y,2=>Z,_=>throw new ArgumentOutOfRangeException(nameof(axis))};
}

public readonly record struct Bounds3D(Vector3D Min,Vector3D Max)
{
    public Vector3D Center=>Min+(Max-Min)*.5;
    public Vector3D Size=>Max-Min;
    public bool IsValid=>Min.IsFinite&&Max.IsFinite&&Min.X<=Max.X&&Min.Y<=Max.Y&&Min.Z<=Max.Z;
    public Bounds3D Union(Bounds3D other)=>new(Vector3D.Min(Min,other.Min),Vector3D.Max(Max,other.Max));
    public Bounds3D Include(Vector3D point)=>new(Vector3D.Min(Min,point),Vector3D.Max(Max,point));
    public bool Contains(Vector3D point)=>point.X>=Min.X&&point.X<=Max.X&&point.Y>=Min.Y&&point.Y<=Max.Y&&point.Z>=Min.Z&&point.Z<=Max.Z;
    public bool Intersect(Ray3D ray,double maximumDistance,out double minimumDistance)
    {
        double low=0,high=maximumDistance;
        for(int axis=0;axis<3;axis++)
        {
            double origin=ray.Origin.Component(axis),direction=ray.Direction.Component(axis),min=Min.Component(axis),max=Max.Component(axis);
            if(Math.Abs(direction)<1e-300) { if(origin<min||origin>max) { minimumDistance=0; return false; } continue; }
            double a=(min-origin)/direction,b=(max-origin)/direction; if(a>b) (a,b)=(b,a);
            low=Math.Max(low,a); high=Math.Min(high,b);
            if(low>high) { minimumDistance=0; return false; }
        }
        minimumDistance=low; return true;
    }
    public static Bounds3D FromPoints(IEnumerable<Vector3D> points)
    {
        using var iterator=points.GetEnumerator();
        if(!iterator.MoveNext()||!iterator.Current.IsFinite) throw new ArgumentException("At least one finite point is required.");
        var bounds=new Bounds3D(iterator.Current,iterator.Current);
        while(iterator.MoveNext()) { if(!iterator.Current.IsFinite) throw new ArgumentException("Bounds cannot contain non-finite points."); bounds=bounds.Include(iterator.Current); }
        return bounds;
    }
}

public readonly record struct Ray3D(Vector3D Origin,Vector3D Direction)
{
    public Vector3D At(double distance)=>Origin+Direction*distance;
    public bool IntersectTriangle(Vector3D a,Vector3D b,Vector3D c,out double distance,out Vector3D barycentric,bool doubleSided=true)
    {
        Vector3D edge1=b-a,edge2=c-a,p=Vector3D.Cross(Direction,edge2);
        double determinant=Vector3D.Dot(edge1,p),epsilon=1e-13*Math.Max(1e-30,edge1.Length*edge2.Length);
        if(doubleSided?Math.Abs(determinant)<=epsilon:determinant<=epsilon) { distance=0; barycentric=default; return false; }
        double inverse=1/determinant; Vector3D t=Origin-a;
        double u=Vector3D.Dot(t,p)*inverse;
        if(u<0||u>1) { distance=0; barycentric=default; return false; }
        Vector3D q=Vector3D.Cross(t,edge1); double v=Vector3D.Dot(Direction,q)*inverse;
        if(v<0||u+v>1) { distance=0; barycentric=default; return false; }
        distance=Vector3D.Dot(edge2,q)*inverse; barycentric=new(1-u-v,u,v); return distance>=0&&double.IsFinite(distance);
    }
}

public readonly record struct ProjectedPoint3D(PointD Pixel,double Depth,bool IsVisible);
public enum CameraProjection { Perspective,Orthographic }

public sealed class Camera3D:ObservableObject
{
    private double _yaw=45,_pitch=25,_distance=5.3,_fieldOfView=45,_orthographicWidth=4.5;
    private Vector3D _target=Vector3D.Zero;
    private CameraProjection _projection;
    public double Yaw { get=>_yaw; set { Finite(value); Set(ref _yaw,Math.IEEERemainder(value,360)); } }
    public double Pitch { get=>_pitch; set { Finite(value); Set(ref _pitch,Math.Clamp(value,-89.5,89.5)); } }
    public double Distance { get=>_distance; set { Positive(value); Set(ref _distance,Math.Clamp(value,.02,1e7)); } }
    public double FieldOfView { get=>_fieldOfView; set { Finite(value); if(value<1||value>150) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _fieldOfView,value); } }
    public double OrthographicWidth { get=>_orthographicWidth; set { Positive(value); Set(ref _orthographicWidth,Math.Clamp(value,1e-6,1e7)); } }
    public Vector3D Target { get=>_target; set { if(!value.IsFinite) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _target,value); } }
    public CameraProjection Projection { get=>_projection; set=>Set(ref _projection,value); }
    public Vector3D Position
    {
        get
        {
            double yaw=Yaw*Math.PI/180,pitch=Pitch*Math.PI/180;
            return Target+new Vector3D(Math.Sin(yaw)*Math.Cos(pitch),Math.Sin(pitch),Math.Cos(yaw)*Math.Cos(pitch))*Distance;
        }
    }
    public Vector3D Forward=>(Target-Position).Normalized;
    public Vector3D Right=>Vector3D.Cross(Forward,Vector3D.UnitY).Normalized;
    public Vector3D Up=>Vector3D.Cross(Right,Forward).Normalized;
    public Matrix4x4 ViewMatrix=>Matrix4x4.CreateLookAt(Position.ToSingle(),Target.ToSingle(),Up.ToSingle());
    public Matrix4x4 ProjectionMatrix(double aspectRatio)
    {
        Positive(aspectRatio);
        double near=Math.Max(.0001,Distance*.001),far=Math.Max(100,Distance*100);
        if(Projection==CameraProjection.Orthographic)
        {
            double width=OrthographicWidth,height=width/aspectRatio;
            return new((float)(2/width),0,0,0,0,(float)(2/height),0,0,0,0,(float)(-2/(far-near)),0,0,0,(float)(-(far+near)/(far-near)),1);
        }
        double f=1/Math.Tan(FieldOfView*Math.PI/360);
        // System.Numerics row-vector matrix, right-handed OpenGL NDC depth [-1,+1].
        return new((float)(f/aspectRatio),0,0,0,0,(float)f,0,0,0,0,(float)((far+near)/(near-far)),-1,0,0,(float)(2*far*near/(near-far)),0);
    }
    public Matrix4x4 ViewProjection(double aspectRatio)=>ViewMatrix*ProjectionMatrix(aspectRatio);
    public ProjectedPoint3D Project(Vector3D point,RectD viewport)
    {
        if(viewport.IsEmpty||!point.IsFinite) return new(default,double.NaN,false);
        Vector4 clip=Vector4.Transform(new Vector4(point.ToSingle(),1),ViewProjection(viewport.Width/viewport.Height));
        if(clip.W<=0||!float.IsFinite(clip.W)) return new(default,double.NaN,false);
        double x=clip.X/clip.W,y=clip.Y/clip.W,z=clip.Z/clip.W;
        return new(new(viewport.X+(x+1)*viewport.Width/2,viewport.Y+(1-y)*viewport.Height/2),z,x>=-1&&x<=1&&y>=-1&&y<=1&&z>=-1&&z<=1);
    }
    public Ray3D GetRay(PointD point,RectD viewport)
    {
        if(viewport.IsEmpty) throw new ArgumentException("A non-empty viewport is required.");
        if(!Matrix4x4.Invert(ViewProjection(viewport.Width/viewport.Height),out var inverse)) throw new InvalidOperationException("Camera matrix is singular.");
        float x=(float)((point.X-viewport.X)*2/viewport.Width-1),y=(float)(1-(point.Y-viewport.Y)*2/viewport.Height);
        Vector4 a=Vector4.Transform(new Vector4(x,y,-1,1),inverse),b=Vector4.Transform(new Vector4(x,y,1,1),inverse);
        var near=new Vector3D(a.X/a.W,a.Y/a.W,a.Z/a.W); var far=new Vector3D(b.X/b.W,b.Y/b.W,b.Z/b.W);
        return new(near,(far-near).Normalized);
    }
    public void Orbit(double deltaX,double deltaY) { Yaw-=deltaX*.4; Pitch+=deltaY*.4; }
    public void Pan(double deltaX,double deltaY,double viewportWidth,double viewportHeight)
    {
        if(viewportHeight<=0||viewportWidth<=0) return;
        double height=Projection==CameraProjection.Perspective?2*Distance*Math.Tan(FieldOfView*Math.PI/360):OrthographicWidth*viewportHeight/viewportWidth;
        Target+=Right*(-deltaX*height/viewportHeight)+Up*(deltaY*height/viewportHeight);
    }
    public void Zoom(double factor)
    {
        Positive(factor);
        if(Projection==CameraProjection.Perspective) Distance=Math.Clamp(Distance*factor,.02,1e7); else OrthographicWidth=Math.Clamp(OrthographicWidth*factor,1e-6,1e7);
    }
    public void Reset() { Yaw=45; Pitch=25; Distance=5.3; OrthographicWidth=4.5; Target=Vector3D.Zero; }
    private static void Finite(double value) { if(!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static void Positive(double value) { Finite(value); if(value<=0) throw new ArgumentOutOfRangeException(nameof(value)); }
}
