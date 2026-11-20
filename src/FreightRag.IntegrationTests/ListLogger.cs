using Microsoft.Extensions.Logging;

namespace FreightRag.IntegrationTests;

/// <summary>Collects log entries for assertions.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, EventId EventId, string Message);

    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
            Entries.Add(new Entry(logLevel, eventId, formatter(state, exception)));
    }
}
