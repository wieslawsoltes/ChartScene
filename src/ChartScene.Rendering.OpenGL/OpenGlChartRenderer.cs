using System.Diagnostics;
using System.Numerics;
using ChartScene.ThreeD;
using Silk.NET.OpenGL;

namespace ChartScene.Rendering.OpenGL;

public sealed record GpuFrameStatistics(int DrawCalls, long UploadedBytes, long Vertices, long Triangles, double CpuMilliseconds, string Driver);

/// <summary>
/// Context-owned persistent GPU renderer. Create, render and dispose only while its GL context is current.
/// No native windowing, UI dependency, CPU bitmap upload, or per-frame vertex upload is required for camera changes.
/// </summary>
public sealed unsafe class OpenGlChartRenderer : IDisposable
{
    private sealed class Buffers(uint vao, uint vertex, uint index)
    {
        public uint Vao = vao, Vertex = vertex, Index = index;
        public Vector3D? SortCamera;
        public Vector3D? SortForward;
    }
    private readonly GL _gl;
    private readonly Dictionary<MeshBuffer3D, Buffers> _buffers = [];
    private uint _program, _framebuffer, _color, _depth;
    private int _width, _height;
    private readonly bool _es;
    private readonly int _matrix, _viewport, _topology, _lighting, _clip, _halfSize, _pixelRatio, _maxPoint;
    private readonly float _maxPointSize;
    private bool _disposed;
    public string Driver { get; }

    public OpenGlChartRenderer(GL gl)
    {
        _gl = gl ?? throw new ArgumentNullException(nameof(gl));
        string version = gl.GetStringS(StringName.Version), shaderVersion = gl.GetStringS(StringName.ShadingLanguageVersion);
        _es = shaderVersion.Contains("ES", StringComparison.OrdinalIgnoreCase) || version.Contains("WebGL", StringComparison.OrdinalIgnoreCase);
        Driver = $"{gl.GetStringS(StringName.Renderer)} · {version}";
        float* pointRange = stackalloc float[2]; // ES ALIASED_POINT_SIZE_RANGE (0x846D) is not exposed by the desktop binding enum.
        gl.GetFloat((GLEnum)(_es ? 0x846D : 0x0B12), pointRange);
        _maxPointSize = Math.Max(1, pointRange[1]);
        try
        {
            _program = CreateProgram();
            _matrix = Uniform("uMatrix"); _viewport = Uniform("uViewport"); _topology = Uniform("uTopology");
            _lighting = Uniform("uLighting"); _clip = Uniform("uClip"); _halfSize = Uniform("uHalfSize");
            _pixelRatio = Uniform("uPixelRatio"); _maxPoint = Uniform("uMaxPoint");
        }
        catch { if (_program != 0) gl.DeleteProgram(_program); throw; }
    }
    private int Uniform(string name) => _gl.GetUniformLocation(_program, name);

    public GpuFrameStatistics Render(Scene3D scene, ChartModel3D model, int width, int height, double pixelRatio = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || !double.IsFinite(pixelRatio) || pixelRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var watch = Stopwatch.StartNew(); var gl = _gl; long uploaded = 0; int draws = 0;
        int drawFbo = gl.GetInteger(GLEnum.DrawFramebufferBinding), readFbo = gl.GetInteger(GLEnum.ReadFramebufferBinding);
        int oldProgram = gl.GetInteger(GLEnum.CurrentProgram), oldVao = gl.GetInteger(GLEnum.VertexArrayBinding);
        int* oldViewport = stackalloc int[4]; gl.GetInteger(GLEnum.Viewport, oldViewport);
        bool oldDepth = gl.IsEnabled(EnableCap.DepthTest), oldBlend = gl.IsEnabled(EnableCap.Blend), oldScissor = gl.IsEnabled(EnableCap.ScissorTest), oldCull = gl.IsEnabled(EnableCap.CullFace);
        bool oldDepthMask = gl.GetInteger(GLEnum.DepthWritemask) != 0;
        try
        {
            EnsureTarget(width, height);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            gl.Viewport(0, 0, (uint)width, (uint)height);
            gl.Disable(EnableCap.ScissorTest); gl.Disable(EnableCap.CullFace);
            gl.Enable(EnableCap.DepthTest); gl.DepthFunc(DepthFunction.Lequal); gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);
            var background = model.Theme.PlotBackground;
            gl.ClearColor(background.R / 255f, background.G / 255f, background.B / 255f, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            if (!_es) gl.Enable(EnableCap.ProgramPointSize);
            gl.UseProgram(_program);
            var matrix = model.Camera.ViewProjection((double)width / height);
            gl.UniformMatrix4(_matrix, 1, false, (float*)&matrix);
            gl.Uniform2(_viewport, (float)width, (float)height);
            gl.Uniform3(_halfSize, (float)model.WorldSize.X / 2, (float)model.WorldSize.Y / 2, (float)model.WorldSize.Z / 2);
            gl.Uniform1(_pixelRatio, (float)pixelRatio); gl.Uniform1(_maxPoint, _maxPointSize);
            var retained = scene.Nodes.Select(n => n.Geometry).ToHashSet();
            foreach (var old in _buffers.Keys.Where(k => !retained.Contains(k)).ToArray()) { Delete(_buffers[old]); _buffers.Remove(old); }
            // Centroid sorting supports conventional alpha compositing. Intersecting transparent
            // surfaces require order-independent transparency and are not claimed as exact here.
            var ordered = scene.Nodes.OrderBy(n => n.Geometry.HasTransparency)
                .ThenByDescending(n => n.Geometry.HasTransparency ? Vector3D.Dot(n.Geometry.Bounds.Center - model.Camera.Position, model.Camera.Forward) : 0);
            foreach (var node in ordered)
            {
                var mesh = node.Geometry;
                if (!_buffers.TryGetValue(mesh, out var buffers))
                {
                    buffers = Upload(mesh); _buffers.Add(mesh, buffers);
                    uploaded += (long)mesh.Vertices.Length * 44 + (long)mesh.Indices.Length * 4;
                }
                gl.BindVertexArray(buffers.Vao);
                if (mesh.HasTransparency)
                {
                    gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); gl.DepthMask(false);
                    if (mesh.Topology == MeshTopology3D.Triangles && (buffers.SortCamera != model.Camera.Position || buffers.SortForward != model.Camera.Forward))
                    {
                        var indices = SortedIndices(mesh, model.Camera);
                        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, buffers.Index);
                        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, indices.AsSpan(), BufferUsageARB.DynamicDraw);
                        uploaded += indices.LongLength * 4; buffers.SortCamera = model.Camera.Position; buffers.SortForward = model.Camera.Forward;
                    }
                }
                else { gl.Disable(EnableCap.Blend); gl.DepthMask(true); }
                gl.Uniform1(_topology, (int)mesh.Topology); gl.Uniform1(_lighting, node.Lighting ? 1 : 0); gl.Uniform1(_clip, node.ClipToPlot ? 1 : 0);
                if (mesh.Topology == MeshTopology3D.Points) gl.DrawArrays(PrimitiveType.Points, 0, (uint)mesh.Vertices.Length);
                else gl.DrawElements(PrimitiveType.Triangles, (uint)mesh.Indices.Length, DrawElementsType.UnsignedInt, null);
                draws++;
            }
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _framebuffer);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)drawFbo);
            gl.BlitFramebuffer(0, 0, width, height, oldViewport[0], oldViewport[1], oldViewport[0] + oldViewport[2], oldViewport[1] + oldViewport[3], ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            var error = gl.GetError();
            if (error != GLEnum.NoError) throw new InvalidOperationException($"OpenGL chart draw failed: {error}.");
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)drawFbo); gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)readFbo);
            gl.Viewport(oldViewport[0], oldViewport[1], (uint)oldViewport[2], (uint)oldViewport[3]);
            gl.BindVertexArray((uint)oldVao); gl.UseProgram((uint)oldProgram); gl.DepthMask(oldDepthMask);
            SetEnabled(EnableCap.DepthTest, oldDepth); SetEnabled(EnableCap.Blend, oldBlend); SetEnabled(EnableCap.ScissorTest, oldScissor); SetEnabled(EnableCap.CullFace, oldCull);
        }
        return new(draws, uploaded, scene.VertexCount, scene.TriangleCount, watch.Elapsed.TotalMilliseconds, Driver);
    }
    private void SetEnabled(EnableCap cap, bool enabled) { if (enabled) _gl.Enable(cap); else _gl.Disable(cap); }
    private Buffers Upload(MeshBuffer3D mesh)
    {
        var gl = _gl; var b = new Buffers(gl.GenVertexArray(), gl.GenBuffer(), gl.GenBuffer());
        try
        {
            gl.BindVertexArray(b.Vao); gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Vertex);
            var vertices = mesh.InterleavedVertices(); gl.BufferData<float>(BufferTargetARB.ArrayBuffer, vertices.AsSpan(), BufferUsageARB.StaticDraw);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, b.Index); gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, mesh.Indices.Span, BufferUsageARB.StaticDraw);
            int[] sizes = [3, 3, 4, 1]; uint offset = 0;
            for (uint i = 0; i < sizes.Length; i++)
            {
                gl.VertexAttribPointer(i, sizes[i], VertexAttribPointerType.Float, false, 44, (void*)(offset * 4));
                gl.EnableVertexAttribArray(i); offset += (uint)sizes[i];
            }
            return b;
        }
        catch { Delete(b); throw; }
    }
    private static uint[] SortedIndices(MeshBuffer3D mesh, Camera3D camera)
    {
        var vertices = mesh.Vertices.ToArray(); var indices = mesh.Indices.ToArray();
        var triangles = Enumerable.Range(0, indices.Length / 3).OrderByDescending(i =>
        {
            var center = (vertices[indices[i * 3]].Position + vertices[indices[i * 3 + 1]].Position + vertices[indices[i * 3 + 2]].Position) / 3;
            return Vector3D.Dot(center - camera.Position, camera.Forward);
        }).ToArray();
        var result = new uint[indices.Length];
        for (int i = 0; i < triangles.Length; i++) Array.Copy(indices, triangles[i] * 3, result, i * 3, 3);
        return result;
    }
    private void EnsureTarget(int width, int height)
    {
        if (_width == width && _height == height && _framebuffer != 0) return;
        DeleteTarget(); var gl = _gl;
        _framebuffer = gl.GenFramebuffer(); _color = gl.GenTexture(); _depth = gl.GenRenderbuffer();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, _color);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)height);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _color, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _depth);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete) throw new InvalidOperationException("GPU framebuffer allocation failed.");
            _width = width; _height = height;
        }
        catch { DeleteTarget(); throw; }
    }
    private uint CreateProgram()
    {
        string prefix = _es ? "#version 300 es\nprecision highp float;\nprecision highp int;\n" : "#version 330 core\n";
        uint vertex = Shader(ShaderType.VertexShader, prefix + VertexShader), fragment = 0, program = 0;
        try
        {
            fragment = Shader(ShaderType.FragmentShader, prefix + FragmentShader); program = _gl.CreateProgram();
            _gl.AttachShader(program, vertex); _gl.AttachShader(program, fragment); _gl.LinkProgram(program);
            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int status);
            if (status == 0) throw new InvalidOperationException("Chart shader link failed: " + _gl.GetProgramInfoLog(program));
            return program;
        }
        catch { if (program != 0) _gl.DeleteProgram(program); throw; }
        finally { _gl.DeleteShader(vertex); if (fragment != 0) _gl.DeleteShader(fragment); }
    }
    private uint Shader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type); _gl.ShaderSource(shader, source); _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status != 0) return shader;
        string log = _gl.GetShaderInfoLog(shader); _gl.DeleteShader(shader); throw new InvalidOperationException($"{type}: {log}");
    }
    private const string VertexShader = """
        layout(location=0) in vec3 aPosition;
        layout(location=1) in vec3 aNormal;
        layout(location=2) in vec4 aColor;
        layout(location=3) in float aSize;
        uniform mat4 uMatrix;
        uniform vec2 uViewport;
        uniform float uPixelRatio;
        uniform float uMaxPoint;
        uniform int uTopology;
        out vec4 vColor;
        out vec3 vNormal;
        out vec3 vWorld;
        void main() {
            vec4 p=uMatrix*vec4(aPosition,1.0);
            vColor=aColor; vNormal=aNormal; vWorld=aPosition;
            if(uTopology==2) {
                vec4 q=uMatrix*vec4(aNormal,1.0);
                float pd=p.z+p.w, qd=q.z+q.w;
                if(pd<0.0 && qd<0.0) { gl_Position=vec4(2.0,2.0,2.0,1.0); return; }
                if(pd<0.0) { float t=pd/(pd-qd); p=mix(p,q,t); vWorld=mix(aPosition,aNormal,t); }
                else if(qd<0.0) q=mix(q,p,qd/(qd-pd));
                vec2 direction=(q.xy/q.w-p.xy/p.w)*uViewport;
                float lengthSquared=dot(direction,direction);
                if(lengthSquared>0.000001) {
                    vec2 normal=vec2(-direction.y,direction.x)*inversesqrt(lengthSquared);
                    p.xy+=normal*aSize*uPixelRatio/uViewport*p.w;
                }
                vNormal=vec3(0.0,1.0,0.0);
            }
            gl_PointSize=clamp(abs(aSize)*uPixelRatio,1.0,uMaxPoint);
            gl_Position=p;
        }
        """;
    private const string FragmentShader = """
        in vec4 vColor;
        in vec3 vNormal;
        in vec3 vWorld;
        uniform int uTopology;
        uniform int uLighting;
        uniform int uClip;
        uniform vec3 uHalfSize;
        out vec4 outColor;
        void main() {
            if(uClip!=0 && any(greaterThan(abs(vWorld),uHalfSize+vec3(0.00001)))) discard;
            float coverage=1.0;
            if(uTopology==1) {
                float radius=length(gl_PointCoord*2.0-1.0);
                if(radius>1.0) discard;
                coverage=1.0-smoothstep(1.0-fwidth(radius),1.0,radius);
            }
            float light=1.0;
            if(uLighting!=0 && dot(vNormal,vNormal)>0.00001) {
                vec3 n=normalize(vNormal);
                light=0.30+0.70*abs(dot(n,normalize(vec3(0.35,0.85,0.45))));
            }
            outColor=vec4(vColor.rgb*light,vColor.a*coverage);
        }
        """;
    private void Delete(Buffers b) { _gl.DeleteVertexArray(b.Vao); _gl.DeleteBuffer(b.Vertex); _gl.DeleteBuffer(b.Index); }
    private void DeleteTarget()
    {
        if (_framebuffer != 0) _gl.DeleteFramebuffer(_framebuffer); if (_color != 0) _gl.DeleteTexture(_color); if (_depth != 0) _gl.DeleteRenderbuffer(_depth);
        _framebuffer = _color = _depth = 0; _width = _height = 0;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var b in _buffers.Values) Delete(b); _buffers.Clear(); DeleteTarget();
        if (_program != 0) _gl.DeleteProgram(_program); _program = 0;
    }
}
