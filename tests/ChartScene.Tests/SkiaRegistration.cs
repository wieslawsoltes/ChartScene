public static partial class AdditionalTests
{
    static partial void RegisterSerialization(TestSuite suite)
    {
        SkiaTests.Register(suite);
        SampleTests.Register(suite);
        DiagnosticTests.Register(suite);
        CursorSyncTests.Register(suite);
        HeatmapLegendTests.Register(suite);
    }
}
