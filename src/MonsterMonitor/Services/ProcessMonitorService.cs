using System;
using System.Diagnostics;
using System.IO;
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
                // Подчищаем ss.exe, оставшийся от предыдущей (аварийно закрытой) копии
                // приложения — иначе его занятый порт не даст новому 3proxy стартовать,
                // и watchdog будет бесконечно перезапускать мгновенно умирающий процесс.
                KillOrphanedProcesses();
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

                // ss (3proxy) при занятом порту завершается почти мгновенно.
                // Если процесс умер сразу после старта — не рапортуем «запущен» и не
                // крутим бесконечный цикл рестартов, а один раз пишем понятную причину.
                if (_process.WaitForExit(500))
                {
                    var exitCode = SafeGetExitCode(_process);
                    var message = "Процесс ss завершился сразу после запуска (код " + exitCode +
                                  "). Вероятно, локальный порт уже занят другим процессом ss.";
                    if (_lastStartError != message)
                    {
                        _lastStartError = message;
                        _log.Error(message);
                    }
                    return;
                }

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

        // Завершает «осиротевшие» экземпляры ss.exe, оставшиеся от прошлой копии
        // приложения. Сопоставление по полному пути к исполняемому файлу, чтобы не
        // задеть посторонние одноимённые процессы.
        private void KillOrphanedProcesses()
        {
            string targetName;
            string targetFullPath;
            try
            {
                targetName = Path.GetFileNameWithoutExtension(_path);
                targetFullPath = Path.GetFullPath(_path);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(targetName))
            {
                return;
            }

            var ownPid = _process != null && IsProcessRunning(_process) ? SafeGetId(_process) : -1;

            Process[] candidates;
            try
            {
                candidates = Process.GetProcessesByName(targetName);
            }
            catch (Exception ex)
            {
                _log.Warn("Не удалось перечислить процессы ss: " + ex.Message);
                return;
            }

            foreach (var proc in candidates)
            {
                try
                {
                    if (proc.Id == ownPid)
                    {
                        continue;
                    }

                    // Убиваем только процессы из нашего каталога ss.exe.
                    if (!PathsEqual(SafeGetProcessPath(proc), targetFullPath))
                    {
                        continue;
                    }

                    proc.Kill();
                    proc.WaitForExit(3000);
                    _log.Warn("Завершен осиротевший процесс ss (PID " + proc.Id + ") от предыдущего запуска.");
                }
                catch (Exception ex)
                {
                    _log.Warn("Не удалось завершить осиротевший ss: " + ex.Message);
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
                   string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeGetProcessPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                // Доступ к MainModule может быть запрещён (другой пользователь/разрядность).
                return null;
            }
        }

        private static int SafeGetId(Process process)
        {
            try
            {
                return process.Id;
            }
            catch
            {
                return -1;
            }
        }

        private static int SafeGetExitCode(Process process)
        {
            try
            {
                return process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }

        public void Dispose()
        {
            Stop();
            _watchdogTimer.Dispose();
        }
    }
}
