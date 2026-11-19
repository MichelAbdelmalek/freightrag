using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FreightRag.Tests.Support;

/// <summary>Records every log entry (all levels): rendered message, structured values and category.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Values, Exception? Exception);

    public ConcurrentQueue<Entry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public void Dispose() { }

    /// <summary>Everything that could end up in a log sink, as text.</summary>
    public string AllText() => string.Join("\n", Entries.Select(e =>
        $"{e.Category} {e.Level} {e.Message} {string.Join(";", e.Values.Select(v => $"{v.Key}={v.Value}"))} {e.Exception}"));

    private sealed class Logger(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            owner.Entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), values, exception));
        }
    }
}
