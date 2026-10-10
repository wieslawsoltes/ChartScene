using System.Text.Json.Serialization;
using ChartScene.Core;
using ChartScene.Rendering;

namespace ChartScene.App;

// Explicit source-generated contracts: no runtime metadata discovery in trimmed WASM.
public sealed record ControlBounds(double x, double y, double width, double height);
public sealed record AxisRangeSnapshot(double min, double max);
public sealed record ViewportSnapshotDto(AxisRangeSnapshot x, AxisRangeSnapshot y);
public sealed record SeriesSnapshotDto(string Id, string name, string kind, bool visible, int selected, int count, long revision);
public sealed record AnnotationSnapshotDto(string Id, string kind, double X1, double Y1, double X2, double Y2, bool IsSelected);
public sealed record CatalogEntryDto(string Id, string Title, string Category);
public sealed class DiagnosticSnapshot
{
    public bool ready { get; init; }
    public string exampleId { get; init; } = "";
    public string theme { get; init; } = "";
    public string mode { get; init; } = "";
    public bool streaming { get; init; }
    public long frames { get; init; }
    public string? error { get; init; }
    public string renderer { get; init; } = "";
    public double width { get; init; }
    public double height { get; init; }
    public RectD? plot { get; init; }
    public ViewportSnapshotDto? ranges { get; init; }
    public RenderStatistics? statistics { get; init; }
    public SeriesSnapshotDto[] series { get; init; } = [];
    public AnnotationSnapshotDto[] annotations { get; init; } = [];
    public int hits { get; init; }
    public bool canUndo { get; init; }
    public bool canRedo { get; init; }
    public Dictionary<string, ControlBounds?> controls { get; init; } = [];
    public CatalogEntryDto[] catalog { get; init; } = [];
}
[JsonSerializable(typeof(DiagnosticSnapshot))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
public partial class DiagnosticJsonContext : JsonSerializerContext;
