using System;

namespace MonsterMonitor.Services
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error
    }

    public sealed class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }
    }

    public sealed class LogService
    {
        public event Action<LogEntry> LogReceived;

        public void Log(LogLevel level, string message)
        {
            LogReceived?.Invoke(new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Message = message ?? string.Empty
            });
        }

        public void Info(string message) => Log(LogLevel.Info, message);
        public void Warn(string message) => Log(LogLevel.Warn, message);
        public void Error(string message) => Log(LogLevel.Error, message);
        public void Debug(string message) => Log(LogLevel.Debug, message);
    }
}
