public static partial class AdditionalTests
{
    static partial void RegisterSerialization(TestSuite suite)
    {
        PlotLayoutTests.Register(suite);
        CursorLayoutTests.Register(suite);
        SkiaTests.Register(suite);
        LabelCollisionTests.Register(suite);
        ThumbnailTests.Register(suite);
        SampleTests.Register(suite);
        DiagnosticTests.Register(suite);
        CursorSyncTests.Register(suite);
        HeatmapLegendTests.Register(suite);
        IndexedResamplingTests.Register(suite);
        RangeNavigatorTests.Register(suite);
        NavigatorReentrancyTests.Register(suite);
        GridPerformanceTests.Register(suite);
        DataIntegrityTests.Register(suite);
        HitTestIntegrityTests.Register(suite);
        GraphicsIntegrityTests.Register(suite);
        StackCacheTests.Register(suite);
        SceneCacheTests.Register(suite);
        ExampleBrowserTests.Register(suite);
        BrowserVisualTests.Register(suite);
    }
}
