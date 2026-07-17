using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MonsterMonitor.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MonsterMonitor.Services
{
    public sealed class SshTunnelService : IDisposable
    {
        private readonly LogService _log;
        private readonly object _sync = new object();

        // Сериализует реконнект: перекрывающиеся запросы (из событий и из монитора)
        // отбрасываются, а не выполняются параллельно и не копятся в очередь.
        private readonly SemaphoreSlim _reconnectGate = new SemaphoreSlim(1, 1);

        private SshClient _client;
        private ForwardedPortRemote _forwardedPort;
        private SshCommand _heartbeatCommand;
        private CancellationTokenSource _heartbeatReadTokenSource;
        private Task _heartbeatReadTask;
        private CancellationTokenSource _monitorTokenSource;
        private Task _monitorTask;
        private AppSettings _settings;
        private volatile bool _disposed;
        private long _lastHeartbeatTicks;
        private bool _remoteIsWindows;

        public SshTunnelService(LogService log)
        {
            _log = log;
        }

        public void Start(AppSettings settings)
        {
            _settings = settings;
            try
            {
                Connect();
            }
            catch (Exception ex)
            {
                _log.Error("Не удалось запустить SSH-туннель с первого раза: " + ex.Message);
                ReconnectSoon();
            }

            StartMonitor();
        }

        private void Connect()
        {
            lock (_sync)
            {
                DisconnectCore();

                var password = _settings.GetPassword();
                var authMethod = new PasswordAuthenticationMethod(_settings.SshUsername, password);
                var connectionInfo = new ConnectionInfo(_settings.SshHost, _settings.SshPort, _settings.SshUsername, authMethod);

                // Ограничиваем блокирующий Connect(), чтобы он не висел бесконечно.
                var timeoutSec = Math.Min(60, Math.Max(5, _settings.ReconnectTimeoutSec));
                connectionInfo.Timeout = TimeSpan.FromSeconds(timeoutSec);

                _client = new SshClient(connectionInfo);
                _client.ErrorOccurred += ClientOnErrorOccurred;
                _client.Connect();

                _forwardedPort = new ForwardedPortRemote((uint)_settings.RemotePort, "127.0.0.1", (uint)_settings.LocalPort);
                _forwardedPort.Exception += ForwardedPortOnException;
                _client.AddForwardedPort(_forwardedPort);
                _forwardedPort.Start();
                StartRemoteHeartbeatNoLock();

                _log.Info(
                    $"SSH подключен. Туннель remote:{_settings.RemotePort} -> local:{_settings.LocalPort}");
            }
        }

        private void ClientOnErrorOccurred(object sender, ExceptionEventArgs e)
        {
            _log.Warn("Ошибка SSH: " + e.Exception.Message);
            ReconnectSoon();
        }

        private void ForwardedPortOnException(object sender, ExceptionEventArgs e)
        {
            _log.Warn("Ошибка перенаправления порта: " + e.Exception.Message);
            ReconnectSoon();
        }

        private void StartMonitor()
        {
            _monitorTokenSource = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorLoop(_monitorTokenSource.Token));
        }

        private async Task MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Пропускаем проверку, пока идёт реконнект — иначе получаем лог-флуд
                    // и лишние пробуждения, дёргающие уже занятый шлюз реконнекта.
                    if (_reconnectGate.CurrentCount > 0)
                    {
                        // heartbeat присылается раз в секунду; MaxPingFailures трактуем
                        // как допустимое число пропущенных ответов (≈ секунд тишины).
                        var silenceThresholdSec = Math.Max(10, _settings.MaxPingFailures);

                        var lastHeartbeatTicks = Interlocked.Read(ref _lastHeartbeatTicks);
                        var isHeartbeatAlive = lastHeartbeatTicks != 0 &&
                                               (DateTime.UtcNow - new DateTime(lastHeartbeatTicks, DateTimeKind.Utc)).TotalSeconds <= silenceThresholdSec;

                        if (!IsConnected() || !isHeartbeatAlive)
                        {
                            _log.Warn("Нет живого вывода heartbeat-команды на удаленном сервере. Переподключаю SSH.");
                            await Reconnect().ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn("Исключение мониторинга heartbeat: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private bool IsConnected()
        {
            lock (_sync)
            {
                return _client != null && _client.IsConnected;
            }
        }

        private void ReconnectSoon()
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    await Reconnect().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // fire-and-forget: гарантированно не роняем процесс необработанным исключением.
                    _log.Warn("Ошибка отложенного переподключения SSH: " + ex.Message);
                }
            });
        }

        private async Task Reconnect()
        {
            if (_disposed)
            {
                return;
            }

            // Неблокирующая попытка захватить шлюз: если реконнект уже идёт — выходим,
            // не создавая второй параллельный Connect() и не накапливая очередь.
            if (!await _reconnectGate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                if (_disposed)
                {
                    return;
                }

                _log.Warn("Перезапуск SSH-соединения...");

                // Connect() ограничен ConnectionInfo.Timeout, поэтому не зависнет навсегда.
                // Ждём завершения задачи (без брошенного WhenAny) — нет орфанных SshClient.
                await Task.Run(() =>
                {
                    DisconnectCore();
                    Connect();
                }).ConfigureAwait(false);

                _log.Info("SSH-соединение восстановлено.");
            }
            catch (Exception ex)
            {
                _log.Error("Ошибка переподключения SSH: " + ex.Message);
            }
            finally
            {
                _reconnectGate.Release();
            }
        }

        private void DisconnectCore()
        {
            lock (_sync)
            {
                StopRemoteHeartbeatNoLock();

                try
                {
                    if (_forwardedPort != null)
                    {
                        if (_forwardedPort.IsStarted)
                        {
                            _forwardedPort.Stop();
                        }
                        _forwardedPort.Exception -= ForwardedPortOnException;
                        _forwardedPort.Dispose();
                        _forwardedPort = null;
                    }
                }
                catch
                {
                    // Ignore errors on shutdown.
                }

                try
                {
                    if (_client != null)
                    {
                        _client.ErrorOccurred -= ClientOnErrorOccurred;
                        if (_client.IsConnected)
                        {
                            _client.Disconnect();
                        }
                        _client.Dispose();
                        _client = null;
                    }
                }
                catch
                {
                    // Ignore errors on shutdown.
                }
            }
        }

        private void StartRemoteHeartbeatNoLock()
        {
            if (_client == null || !_client.IsConnected)
            {
                return;
            }

            StopRemoteHeartbeatNoLock();
            _remoteIsWindows = DetectRemoteWindowsNoLock();

            var heartbeatCommand = _remoteIsWindows
                ? "ping ya.ru -t"
                : "while :; do echo 'some type of ping response '$C; C=$((C+1)); sleep 1; done";

            _heartbeatCommand = _client.CreateCommand(heartbeatCommand);
            _heartbeatReadTokenSource = new CancellationTokenSource();
            Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            _heartbeatCommand.BeginExecute();

            _heartbeatReadTask = Task.Run(() =>
                ReadHeartbeatOutputLoop(_heartbeatCommand, _heartbeatReadTokenSource.Token));
            _log.Info("Запущен удаленный heartbeat через SSH (" + (_remoteIsWindows ? "Windows" : "Linux/Unix") + ").");
        }

        private void StopRemoteHeartbeatNoLock()
        {
            try
            {
                _heartbeatReadTokenSource?.Cancel();
                _heartbeatReadTask?.Wait(1000);
            }
            catch
            {
                // Ignore errors on shutdown.
            }
            finally
            {
                _heartbeatReadTokenSource?.Dispose();
                _heartbeatReadTokenSource = null;
                _heartbeatReadTask = null;
            }

            try
            {
                _heartbeatCommand?.Dispose();
                _heartbeatCommand = null;
            }
            catch
            {
                // Ignore errors on shutdown.
            }
        }

        private void ReadHeartbeatOutputLoop(SshCommand command, CancellationToken token)
        {
            try
            {
                using (var reader = new StreamReader(command.OutputStream))
                {
                    while (!token.IsCancellationRequested)
                    {
                        var line = reader.ReadLine();
                        if (line == null)
                        {
                            Thread.Sleep(200);
                            continue;
                        }

                        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
                        _log.Debug("HB: " + line);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    _log.Warn("Ошибка чтения heartbeat-вывода: " + ex.Message);
                }
            }
        }

        private bool DetectRemoteWindowsNoLock()
        {
            try
            {
                using (var command = _client.CreateCommand("cmd /c ver"))
                {
                    var output = command.Execute() ?? string.Empty;
                    return output.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public void Stop()
        {
            var monitorTokenSource = _monitorTokenSource;
            var monitorTask = _monitorTask;
            _monitorTokenSource = null;
            _monitorTask = null;

            try
            {
                monitorTokenSource?.Cancel();
                // Дожидаемся завершения цикла монитора до освобождения CTS,
                // иначе Task.Delay(token) может словить ObjectDisposedException.
                monitorTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore shutdown errors.
            }
            finally
            {
                monitorTokenSource?.Dispose();
            }

            DisconnectCore();
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
            _reconnectGate.Dispose();
        }
    }
}
