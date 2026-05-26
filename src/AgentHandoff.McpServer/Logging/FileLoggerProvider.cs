using Microsoft.Extensions.Logging;

namespace AgentHandoff.McpServer.Logging;

/// <summary>
/// Tiny no-dependency file logger provider. Writes every log line (Information+) to a single
/// file. Useful here because the parent process spawns the MCP server over stdio and discards
/// the child's stderr — without this, ALL Microsoft.Extensions.Logging output is invisible.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private static readonly object s_lock = new();

    public FileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_path, categoryName);

    public void Dispose() { }

    public static void Append(string path, string line)
    {
        try
        {
            lock (s_lock)
                File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { /* best-effort */ }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _path;
        private readonly string _category;

        public FileLogger(string path, string category) { _path = path; _category = category; }

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var msg = formatter(state, ex);
            var levelTag = level switch
            {
                LogLevel.Trace       => "TRC",
                LogLevel.Debug       => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning     => "WRN",
                LogLevel.Error       => "ERR",
                LogLevel.Critical    => "CRT",
                _                    => "   ",
            };
            var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} [{levelTag}] {_category}: {msg}";
            if (ex is not null) line += Environment.NewLine + ex;
            Append(_path, line);
        }
    }
}
