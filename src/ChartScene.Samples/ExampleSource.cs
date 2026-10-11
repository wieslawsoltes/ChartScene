namespace ChartScene.Samples;

/// <summary>The exact compiled sample-factory source, embedded in the reusable samples package.</summary>
public static class ExampleSource
{
    private static readonly Lazy<string> Source = new(() =>
    {
        using var stream = typeof(SampleCatalog).Assembly.GetManifestResourceStream("ChartScene.Samples.SampleCatalog.cs")
            ?? throw new InvalidOperationException("The sample source resource was not packaged.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }, LazyThreadSafetyMode.ExecutionAndPublication);
    public static string FactoryCode => Source.Value;
    public const string FileName = "SampleCatalog.cs";
}
