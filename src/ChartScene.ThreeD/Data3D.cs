using ChartScene.Core;

namespace ChartScene.ThreeD;

public sealed class SurfaceData3D
{
    private readonly double[] _x,_z,_height;
    public int Width=>_x.Length;
    public int Depth=>_z.Length;
    public ReadOnlyMemory<double> XCoordinates=>_x;
    public ReadOnlyMemory<double> ZCoordinates=>_z;
    public ReadOnlyMemory<double> Heights=>_height;
    public double this[int x,int z]=>_height[checked(z*Width+x)];
    public SurfaceData3D(double[] x,double[] z,double[] heights)
    {
        ArgumentNullException.ThrowIfNull(x); ArgumentNullException.ThrowIfNull(z); ArgumentNullException.ThrowIfNull(heights);
        if(x.Length<2||z.Length<2||heights.Length!=checked(x.Length*z.Length)) throw new ArgumentException("Surface dimensions must match and contain at least 2 × 2 samples.");
        static void Coordinates(double[] values)
        {
            for(int i=0;i<values.Length;i++) if(!double.IsFinite(values[i])||i>0&&values[i]<=values[i-1]) throw new ArgumentException("Surface coordinates must be finite and strictly increasing.");
        }
        Coordinates(x); Coordinates(z);
        if(heights.Any(double.IsInfinity)) throw new ArgumentException("Heights may contain NaN holes but not infinity.");
        _x=(double[])x.Clone(); _z=(double[])z.Clone(); _height=(double[])heights.Clone();
    }
    public static SurfaceData3D Uniform(int width,int depth,double xStart,double xStep,double zStart,double zStep,Func<int,int,double> height)
    {
        if(width<2||depth<2||xStep<=0||zStep<=0) throw new ArgumentOutOfRangeException(nameof(width));
        return new(Enumerable.Range(0,width).Select(i=>xStart+i*xStep).ToArray(),Enumerable.Range(0,depth).Select(i=>zStart+i*zStep).ToArray(),Enumerable.Range(0,checked(width*depth)).Select(i=>height(i%width,i/width)).ToArray());
    }
}

public sealed class MeshData3D
{
    private readonly Vector3D[] _positions;
    private readonly uint[] _indices;
    private readonly ColorRgba[]? _colors;
    public ReadOnlyMemory<Vector3D> Positions=>_positions;
    public ReadOnlyMemory<uint> Indices=>_indices;
    public ReadOnlyMemory<ColorRgba> Colors=>_colors??[];
    public Bounds3D Bounds { get; }
    public MeshData3D(Vector3D[] positions,uint[] triangleIndices,ColorRgba[]? colors=null)
    {
        ArgumentNullException.ThrowIfNull(positions); ArgumentNullException.ThrowIfNull(triangleIndices);
        if(positions.Length<3||positions.Any(p=>!p.IsFinite)) throw new ArgumentException("At least three finite positions are required.");
        if(triangleIndices.Length==0||triangleIndices.Length%3!=0||triangleIndices.Any(i=>i>=positions.Length)) throw new ArgumentException("Triangle indices must reference existing vertices.");
        if(colors is not null&&colors.Length!=positions.Length) throw new ArgumentException("Vertex color count must match positions.");
        _positions=(Vector3D[])positions.Clone(); _indices=(uint[])triangleIndices.Clone(); _colors=colors is null?null:(ColorRgba[])colors.Clone(); Bounds=Bounds3D.FromPoints(_positions);
    }
}

public enum MeshTopology3D { Triangles,Points,ThickLines }
public readonly record struct Vertex3D(Vector3D Position,Vector3D Normal,ColorRgba Color,float Size=1);

/// <summary>Normalized geometry. Thick-line Normal stores the other endpoint, and signed Size selects a quad side.</summary>
public sealed class MeshBuffer3D
{
    private readonly Vertex3D[] _vertices;
    private readonly uint[] _indices;
    private readonly int[] _primitiveDataIndices;
    public ReadOnlyMemory<Vertex3D> Vertices=>_vertices;
    public ReadOnlyMemory<uint> Indices=>_indices;
    public ReadOnlyMemory<int> PrimitiveDataIndices=>_primitiveDataIndices;
    public MeshTopology3D Topology { get; }
    public Bounds3D Bounds { get; }
    public int PrimitiveCount=>Topology==MeshTopology3D.Points?_vertices.Length:_indices.Length/3;
    public bool HasTransparency=>_vertices.Any(v=>v.Color.A<255);
    public MeshBuffer3D(Vertex3D[] vertices,uint[] indices,MeshTopology3D topology,int[]? primitiveDataIndices=null)
    {
        if(vertices.Length==0||vertices.Any(v=>!v.Position.IsFinite||!v.Normal.IsFinite||!float.IsFinite(v.Size))) throw new ArgumentException("Geometry must contain finite vertices.");
        if(indices.Any(i=>i>=vertices.Length)||topology!=MeshTopology3D.Points&&indices.Length%3!=0) throw new ArgumentException("Invalid geometry indices.");
        int primitives=topology==MeshTopology3D.Points?vertices.Length:indices.Length/3;
        if(primitiveDataIndices is not null&&primitiveDataIndices.Length!=primitives) throw new ArgumentException("Primitive metadata count mismatch.");
        _vertices=(Vertex3D[])vertices.Clone(); _indices=(uint[])indices.Clone(); _primitiveDataIndices=primitiveDataIndices is null?Enumerable.Range(0,primitives).ToArray():(int[])primitiveDataIndices.Clone();
        Topology=topology; Bounds=Bounds3D.FromPoints(_vertices.Select(v=>v.Position));
    }
    public float[] InterleavedVertices()
    {
        var values=new float[checked(_vertices.Length*11)];
        for(int i=0;i<_vertices.Length;i++)
        {
            var v=_vertices[i]; int o=i*11;
            values[o]=(float)v.Position.X; values[o+1]=(float)v.Position.Y; values[o+2]=(float)v.Position.Z;
            values[o+3]=(float)v.Normal.X; values[o+4]=(float)v.Normal.Y; values[o+5]=(float)v.Normal.Z;
            values[o+6]=v.Color.R/255f; values[o+7]=v.Color.G/255f; values[o+8]=v.Color.B/255f; values[o+9]=v.Color.A/255f; values[o+10]=v.Size;
        }
        return values;
    }
}
