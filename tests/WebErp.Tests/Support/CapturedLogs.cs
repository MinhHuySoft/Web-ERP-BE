using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WebErp.Tests.Support;

/// <summary>Nhà cung cấp log giữ lại các dòng log (loại, mức, nội dung đã định dạng) để test kiểm.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
