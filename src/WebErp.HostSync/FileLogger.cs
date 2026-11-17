using System.Globalization;
using Microsoft.Extensions.Logging;

namespace WebErp.HostSync;

/// <summary>Ghi log ra <c>logs\hostsync-yyyyMMdd.log</c> (giữ 30 ngày). Không bao giờ có bí mật: agent không đọc chuỗi kết nối.</summary>
public sealed class FileLoggerProvider(string directory, LogLevel minimum) : ILoggerProvider
{
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? ex)
    {
        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss} [{level}] {category}: {message}{(ex is null ? "" : " | " + ex.GetType().Name + ": " + ex.Message)}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"hostsync-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log"), line);
                if (now.Minute == 0 && now.Second < 20) Prune();
            }
            catch (IOException)
            {
                // Không để lỗi ghi log làm dừng agent.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Prune()
    {
        foreach (var f in Directory.EnumerateFiles(directory, "hostsync-*.log"))
            if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)) File.Delete(f);
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.Minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(category, logLevel, formatter(state, exception), exception);
        }
    }

    internal LogLevel Minimum => minimum;
}
