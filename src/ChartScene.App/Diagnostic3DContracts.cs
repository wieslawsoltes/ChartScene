using System.Text.Json.Serialization;
namespace ChartScene.App;

public sealed record CameraSnapshotDto(double yaw, double pitch, double distance, double orthographicWidth, string projection, double targetX, double targetY, double targetZ);
public sealed record ShellSnapshotDto(string view, Dictionary<string, ControlBounds?> controls);
public sealed class ThreeDDiagnosticSnapshot
{
    public bool ready { get; init; }
    public string exampleId { get; init; } = "";
    public string? error { get; init; }
    public string driver { get; init; } = "";
    public long frames { get; init; }
    public long sourcePoints { get; init; }
    public long vertices { get; init; }
    public long triangles { get; init; }
    public long uploadedBytes { get; init; }
    public int drawCalls { get; init; }
    public int selected { get; init; }
    public double width { get; init; }
    public double height { get; init; }
    public bool canUndo { get; init; }
    public bool canRedo { get; init; }
    public bool grid { get; init; }
    public string surfaceMode { get; init; } = "";
    public string theme { get; init; } = "";
    public CameraSnapshotDto? camera { get; init; }
    public Dictionary<string, ControlBounds?> controls { get; init; } = [];
    public CatalogEntryDto[] catalog { get; init; } = [];
}
[JsonSerializable(typeof(ThreeDDiagnosticSnapshot))]
[JsonSerializable(typeof(ShellSnapshotDto))]
public partial class Diagnostic3DJsonContext : JsonSerializerContext;
