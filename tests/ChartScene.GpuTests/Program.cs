using System.Runtime.InteropServices;
using System.Text.Json;
using ChartScene.Rendering.OpenGL;
using ChartScene.ThreeD;
using ChartScene.Samples3D;
using Silk.NET.OpenGL;
using SkiaSharp;

Directory.CreateDirectory("artifacts/gpu-tests");
var results = new List<object>(); int failed = 0;
foreach (bool es in new[] { false, true })
{
    using var context = new EglContext(es, 900, 600);
    using var gl = GL.GetApi(name => EglContext.eglGetProcAddress(name));
    using var renderer = new OpenGlChartRenderer(gl);
    foreach (var example in SampleCatalog3D.Examples)
    {
        string name = $"{(es ? "gles3" : "gl33")}-{example.Id}";
        try
        {
            using var model = example.Create(); var builder = new SceneBuilder3D(); var scene = builder.Build(model);
            var first = renderer.Render(scene, model, 900, 600);
            if (first.DrawCalls == 0 || first.UploadedBytes == 0) throw new Exception("No GPU draws or initial vertex upload.");
            model.Camera.Yaw += 10; var second = renderer.Render(builder.Build(model), model, 900, 600);
            if (second.UploadedBytes != 0) throw new Exception("Camera movement rebuilt immutable GPU buffers.");
            var pixels = new byte[900 * 600 * 4];
            unsafe { fixed (byte* data = pixels) gl.ReadPixels(0, 0, 900, 600, PixelFormat.Rgba, PixelType.UnsignedByte, data); }
            if (gl.GetError() != GLEnum.NoError) throw new Exception("Readback failed.");
            int colored = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) > 95 && Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) - Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2])) > 45) colored++;
            if (colored < 50) throw new Exception($"No visible colored data geometry: {colored} pixels.");
            using var bitmap = new SKBitmap(new SKImageInfo(900, 600, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            for (int y = 0; y < 600; y++) Marshal.Copy(pixels, (599 - y) * 900 * 4, bitmap.GetPixels() + y * bitmap.RowBytes, 900 * 4);
            using var image = SKImage.FromBitmap(bitmap); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes($"artifacts/gpu-tests/{name}.png", png.ToArray());
            results.Add(new { name, passed = true, driver = renderer.Driver, coloredPixels = colored, first, second });
            Console.WriteLine($"PASS {name}: {colored} colored pixels, zero camera upload");
        }
        catch (Exception error) { failed++; results.Add(new { name, passed = false, error = error.ToString() }); Console.Error.WriteLine($"FAIL {name}: {error}"); }
    }
}
File.WriteAllText("artifacts/gpu-tests/results.json", JsonSerializer.Serialize(new { total = results.Count, failed, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failed}/{results.Count} actual OpenGL / OpenGL ES tests passed");
return failed == 0 ? 0 : 1;

internal sealed class EglContext : IDisposable
{
    private nint _display, _surface, _context;
    public EglContext(bool es, int width, int height)
    {
        _display = eglGetPlatformDisplay(0x31DD, 0, [0x3038]);
        if (_display == 0 || eglInitialize(_display, out _, out _) == 0) throw Error("initialize");
        try
        {
            if (eglBindAPI(es ? 0x30A0u : 0x30A2u) == 0) throw Error("bind API");
            int[] attributes = [0x3033, 1, 0x3040, es ? 0x40 : 8, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, 0x3025, 24, 0x3038];
            if (eglChooseConfig(_display, attributes, out nint config, 1, out int count) == 0 || count == 0) throw Error("choose configuration");
            _surface = eglCreatePbufferSurface(_display, config, [0x3057, width, 0x3056, height, 0x3038]);
            if (_surface == 0) throw Error("pbuffer");
            _context = eglCreateContext(_display, config, 0, es ? [0x3098, 3, 0x3038] : [0x3098, 3, 0x30FB, 3, 0x30FD, 1, 0x3038]);
            if (_context == 0 || eglMakeCurrent(_display, _surface, _surface, _context) == 0) throw Error("make context current");
            using var gl = GL.GetApi(name => eglGetProcAddress(name)); gl.Viewport(0, 0, (uint)width, (uint)height);
        }
        catch { Dispose(); throw; }
    }
    private static Exception Error(string operation) => new InvalidOperationException($"EGL {operation} failed (0x{eglGetError():X}). A software or hardware EGL driver is required.");
    public void Dispose()
    {
        if (_display == 0) return;
        eglMakeCurrent(_display, 0, 0, 0);
        if (_context != 0) eglDestroyContext(_display, _context);
        if (_surface != 0) eglDestroySurface(_display, _surface);
        eglTerminate(_display); _display = _context = _surface = 0;
    }
    [DllImport("libEGL.so.1")] private static extern nint eglGetPlatformDisplay(uint platform, nint display, nint[] attributes);
    [DllImport("libEGL.so.1")] private static extern int eglInitialize(nint display, out int major, out int minor);
    [DllImport("libEGL.so.1")] private static extern int eglBindAPI(uint api);
    [DllImport("libEGL.so.1")] private static extern int eglChooseConfig(nint display, int[] attributes, out nint config, int size, out int count);
    [DllImport("libEGL.so.1")] private static extern nint eglCreatePbufferSurface(nint display, nint config, int[] attributes);
    [DllImport("libEGL.so.1")] private static extern nint eglCreateContext(nint display, nint config, nint share, int[] attributes);
    [DllImport("libEGL.so.1")] private static extern int eglMakeCurrent(nint display, nint draw, nint read, nint context);
    [DllImport("libEGL.so.1")] private static extern int eglDestroyContext(nint display, nint context);
    [DllImport("libEGL.so.1")] private static extern int eglDestroySurface(nint display, nint surface);
    [DllImport("libEGL.so.1")] private static extern int eglTerminate(nint display);
    [DllImport("libEGL.so.1")] private static extern uint eglGetError();
    [DllImport("libEGL.so.1")] public static extern nint eglGetProcAddress([MarshalAs(UnmanagedType.LPStr)] string name);
}
