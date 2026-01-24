using MeshAI.Core.Configuration;

namespace MeshAI.Core.Logging;

/// <summary>
/// Simple structured logger for inspectable logs.
/// </summary>
public sealed class Logger
{
    private readonly string _source;
    private readonly LogConfig _config;
    private readonly object _lock = new();
    private static StreamWriter? _fileWriter;

    private static LogConfig _globalConfig = new();
    private static readonly object _globalLock = new();

    /// <summary>
    /// Configures global logging settings.
    /// </summary>
    public static void Configure(LogConfig config)
    {
        lock (_globalLock)
        {
            _globalConfig = config;

            if (config.LogFilePath != null)
            {
                _fileWriter?.Dispose();
                _fileWriter = new StreamWriter(config.LogFilePath, append: true)
                {
                    AutoFlush = true
                };
            }
        }
    }

    /// <summary>
    /// Creates a logger for a specific source.
    /// </summary>
    public Logger(string source)
    {
        _source = source;
        _config = _globalConfig;
    }

    /// <summary>
    /// Creates a logger for a type.
    /// </summary>
    public static Logger For<T>() => new(typeof(T).Name);

    /// <summary>
    /// Creates a logger for a type.
    /// </summary>
    public static Logger For(Type type) => new(type.Name);

    public void Trace(string message, params object[] args) => Log(LogLevel.Trace, message, args);
    public void Debug(string message, params object[] args) => Log(LogLevel.Debug, message, args);
    public void Info(string message, params object[] args) => Log(LogLevel.Information, message, args);
    public void Warn(string message, params object[] args) => Log(LogLevel.Warning, message, args);
    public void Error(string message, params object[] args) => Log(LogLevel.Error, message, args);
    public void Error(Exception ex, string message, params object[] args) => Log(LogLevel.Error, $"{message}: {ex.Message}", args);
    public void Critical(string message, params object[] args) => Log(LogLevel.Critical, message, args);

    private void Log(LogLevel level, string message, object[] args)
    {
        if (level < _config.MinLevel)
            return;

        var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
        var logLine = FormatLogLine(level, formattedMessage);

        lock (_lock)
        {
            // Console output with color
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = GetLevelColor(level);
            Console.WriteLine(logLine);
            Console.ForegroundColor = originalColor;

            // File output
            _fileWriter?.WriteLine(logLine);
        }
    }

    private string FormatLogLine(LogLevel level, string message)
    {
        var parts = new List<string>();

        if (_config.IncludeTimestamps)
        {
            parts.Add(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        }

        parts.Add($"[{GetLevelShort(level)}]");

        if (_config.IncludeSource)
        {
            parts.Add($"[{_source}]");
        }

        parts.Add(message);

        return string.Join(" ", parts);
    }

    private static string GetLevelShort(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };

    private static ConsoleColor GetLevelColor(LogLevel level) => level switch
    {
        LogLevel.Trace => ConsoleColor.DarkGray,
        LogLevel.Debug => ConsoleColor.Gray,
        LogLevel.Information => ConsoleColor.White,
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Critical => ConsoleColor.DarkRed,
        _ => ConsoleColor.White
    };
}
