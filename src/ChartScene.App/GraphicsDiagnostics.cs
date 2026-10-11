using Microsoft.Extensions.Logging;

namespace ChartScene.App;

/// <summary>Local diagnostic logging; no network transport or user-data collection.</summary>
internal static class GraphicsDiagnostics
{
    public static void Initialize()
    {
        Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new GraphicsConsoleProvider());
        });
    }

    private sealed class GraphicsConsoleProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new GraphicsConsoleLogger(categoryName);
        public void Dispose() { }
    }

    private sealed class GraphicsConsoleLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None
            && (level >= LogLevel.Warning || category.Contains("OpenGL", StringComparison.OrdinalIgnoreCase)
                || category.Contains("Graphics3D", StringComparison.OrdinalIgnoreCase));
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            string message = $"[{level}] {category}: {formatter(state, exception)}";
            if (exception is not null) message += "\n" + exception;
            Console.Error.WriteLine(message);
        }
    }
}
