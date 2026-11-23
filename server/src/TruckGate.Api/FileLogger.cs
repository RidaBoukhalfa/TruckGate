using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace TruckGate.Api;

/// <summary>
/// Plain daily log files (truckgate-YYYYMMDD.log) in the folder given by Logging:File:Path, kept for
/// 30 days. The Windows installer turns it on so that a site without a developer can send the log
/// file when something goes wrong; the levels follow the usual Logging:LogLevel settings.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepDays = 30;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, c => new FileLogger(this, c));

    internal void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || today != _day)
                {
                    _writer?.Dispose();
                    _day = today;
                    var path = Path.Combine(_directory, $"truckgate-{today:yyyyMMdd}.log");
                    var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    DeleteOldFiles();
                }
                _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // A full or read-only disk must not stop the gate; the event log still has the message.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void DeleteOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var f in Directory.EnumerateFiles(_directory, "truckgate-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(' ').Append(Level(logLevel)).Append(' ').Append(category).Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null) sb.AppendLine().Append(exception);
            owner.Write(sb.ToString());
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "FATAL",
        };
    }
}
