using System;
using System.Diagnostics;
using System.Threading;

namespace MonsterMonitor.Services
{
    public sealed class ProcessMonitorService : IDisposable
    {
        private readonly LogService _log;
        private readonly object _sync = new object();
        private readonly Timer _watchdogTimer;
        private Process _process;
        private string _path;
        private string _arguments;
        private bool _stopping;
        private string _lastStartError;

        public ProcessMonitorService(LogService log)
        {
            _log = log;
            _watchdogTimer = new Timer(WatchdogTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public void Start(string path, string arguments)
        {
            lock (_sync)
            {
                _path = path;
                _arguments = arguments ?? string.Empty;
                _stopping = false;
                EnsureStarted();
                _watchdogTimer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            }
        }

        private void EnsureStarted()
        {
            if (IsProcessRunning(_process))
            {
                return;
            }

            try
            {
                if (_process != null)
                {
                    _process.Dispose();
                }
                _process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _path,
                        Arguments = _arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };
                _process.Start();
                _lastStartError = null;
                _log.Info("Процесс ss запущен.");
            }
            catch (Exception ex)
            {
                // Watchdog тикает каждые 10с; при неверном пути не флудим лог
                // одинаковой ошибкой — пишем её только при изменении.
                if (_lastStartError != ex.Message)
                {
                    _lastStartError = ex.Message;
                    _log.Error("Ошибка запуска ss: " + ex.Message);
                }
            }
        }

        private void WatchdogTick(object state)
        {
            lock (_sync)
            {
                if (_stopping)
                {
                    return;
                }

                EnsureStarted();
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _stopping = true;
                _watchdogTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

                try
                {
                    if (IsProcessRunning(_process))
                    {
                        _process.Kill();
                        _process.WaitForExit(5000);
                        _log.Info("Процесс ss остановлен.");
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Не удалось корректно завершить ss: " + ex.Message);
                }
                finally
                {
                    if (_process != null)
                    {
                        _process.Dispose();
                    }
                    _process = null;
                }
            }
        }

        private static bool IsProcessRunning(Process process)
        {
            if (process == null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            Stop();
            _watchdogTimer.Dispose();
        }
    }
}
