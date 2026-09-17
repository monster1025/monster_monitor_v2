using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
        private SshClient _client;
        private ForwardedPortRemote _forwardedPort;
        // Локальный проброс на серверный RemotePort: даёт замкнутый e2e-маршрут
        // local -> SSH -> сервер:RemotePort -> обратный проброс -> наш LocalPort.
        private ForwardedPortLocal _probePort;
        private int _probeLocalPort;
        private int _probeFailures;
        private DateTime _lastProbeUtc = DateTime.MinValue;
        private DateTime _lastGoodProbeUtc = DateTime.MinValue;
        private SshCommand _heartbeatCommand;
        private CancellationTokenSource _heartbeatReadTokenSource;
        private Task _heartbeatReadTask;
        private CancellationTokenSource _monitorTokenSource;
        private AppSettings _settings;
        private bool _disposed;
        private bool _reconnectInProgress;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
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

                _client = new SshClient(connectionInfo);
                _client.ErrorOccurred += ClientOnErrorOccurred;
                _client.Connect();

                _forwardedPort = new ForwardedPortRemote((uint)_settings.RemotePort, "127.0.0.1", (uint)_settings.LocalPort);
                _forwardedPort.Exception += ForwardedPortOnException;
                _client.AddForwardedPort(_forwardedPort);
                _forwardedPort.Start();
                StartProbePortNoLock();
                StartRemoteHeartbeatNoLock();
                _probeFailures = 0;
                _lastProbeUtc = DateTime.UtcNow;
                _lastGoodProbeUtc = DateTime.UtcNow;

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
            Task.Run(() => MonitorLoop(_monitorTokenSource.Token));
        }

        private async Task MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!IsConnected())
                    {
                        _log.Warn("SSH-сессия не подключена. Переподключаю.");
                        await Reconnect().ConfigureAwait(false);
                    }
                    else
                    {
                        var now = DateTime.UtcNow;
                        var silenceThresholdSec = Math.Max(5, _settings.HeartbeatSilenceSec);
                        var probeIntervalSec = Math.Max(5, _settings.ProbeIntervalSec);

                        var lastHeartbeat = _lastHeartbeatUtc;
                        var heartbeatSilentSec = lastHeartbeat == DateTime.MinValue
                            ? double.MaxValue
                            : (now - lastHeartbeat).TotalSeconds;
                        var heartbeatSuspicious = heartbeatSilentSec > silenceThresholdSec;

                        // Heartbeat живёт в том же SSH-соединении, что и полезный трафик,
                        // поэтому под большой выгрузкой он опаздывает - это НЕ повод рвать
                        // туннель. Решение принимает только e2e-проверка проброса.
                        var needProbe = heartbeatSuspicious ||
                                        (now - _lastProbeUtc).TotalSeconds >= probeIntervalSec;

                        if (needProbe)
                        {
                            _lastProbeUtc = now;
                            var ok = await ProbeForwardAsync().ConfigureAwait(false);
                            if (ok)
                            {
                                if (_probeFailures > 0)
                                {
                                    _log.Info("e2e-проверка проброса снова проходит.");
                                }

                                _probeFailures = 0;
                                _lastGoodProbeUtc = DateTime.UtcNow;
                            }
                            else
                            {
                                _probeFailures++;
                                _log.Warn($"e2e-проверка проброса не прошла ({_probeFailures} подряд).");
                            }
                        }

                        if (heartbeatSuspicious && _probeFailures == 0)
                        {
                            _log.Debug(
                                $"Heartbeat молчит {heartbeatSilentSec:F0}с, но проброс работает - вероятно идёт передача. Не переподключаю.");
                        }

                        if (_probeFailures >= Math.Max(1, _settings.ProbeFailuresBeforeReconnect))
                        {
                            _log.Warn("Проброс портов не пропускает трафик. Переподключаю SSH.");
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
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
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
                await Task.Delay(1000).ConfigureAwait(false);
                await Reconnect().ConfigureAwait(false);
            });
        }

        private async Task Reconnect()
        {
            if (_reconnectInProgress || _disposed)
            {
                return;
            }

            _reconnectInProgress = true;
            try
            {
                var timeout = Math.Min(60, Math.Max(5, _settings.ReconnectTimeoutSec));
                _log.Warn("Перезапуск SSH-соединения...");

                var reconnectTask = Task.Run(() =>
                {
                    DisconnectCore();
                    Connect();
                });

                var completed = await Task.WhenAny(reconnectTask, Task.Delay(TimeSpan.FromSeconds(timeout))).ConfigureAwait(false);
                if (completed != reconnectTask)
                {
                    _log.Error("Переподключение превысило таймаут " + timeout + "с.");
                }
                else
                {
                    _log.Info("SSH-соединение восстановлено.");
                }
            }
            catch (Exception ex)
            {
                _log.Error("Ошибка переподключения SSH: " + ex.Message);
            }
            finally
            {
                _reconnectInProgress = false;
            }
        }

        private void DisconnectCore()
        {
            StopRemoteHeartbeatNoLock();
            StopProbePortNoLock();

            try
            {
                if (_forwardedPort != null)
                {
                    if (_forwardedPort.IsStarted)
                    {
                        _forwardedPort.Stop();
                    }
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

        /// <summary>
        /// Поднимает локальный проброс на серверный RemotePort. Подключение к нему
        /// проходит весь маршрут целиком: наш процесс -> SSH -> слушатель обратного
        /// проброса на сервере -> SSH обратно -> наш LocalPort (ss.exe).
        /// Успешный TCP-connect означает, что проброс реально пропускает трафик,
        /// а не просто "сессия жива".
        /// </summary>
        private void StartProbePortNoLock()
        {
            StopProbePortNoLock();

            try
            {
                _probeLocalPort = FindFreeLocalPort();
                _probePort = new ForwardedPortLocal(
                    "127.0.0.1",
                    (uint)_probeLocalPort,
                    "127.0.0.1",
                    (uint)_settings.RemotePort);
                _client.AddForwardedPort(_probePort);
                _probePort.Start();
                _log.Info($"e2e-проверка проброса включена (127.0.0.1:{_probeLocalPort}).");
            }
            catch (Exception ex)
            {
                _probePort = null;
                _log.Warn("Не удалось поднять порт e2e-проверки: " + ex.Message);
            }
        }

        private void StopProbePortNoLock()
        {
            try
            {
                if (_probePort != null)
                {
                    if (_probePort.IsStarted)
                    {
                        _probePort.Stop();
                    }

                    _probePort.Dispose();
                    _probePort = null;
                }
            }
            catch
            {
                // Ignore errors on shutdown.
            }
        }

        private static int FindFreeLocalPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private async Task<bool> ProbeForwardAsync()
        {
            ForwardedPortLocal probe;
            int port;
            lock (_sync)
            {
                probe = _probePort;
                port = _probeLocalPort;
            }

            if (probe == null || !probe.IsStarted || port <= 0)
            {
                // Порт проверки поднять не удалось - не выдумываем отказ проброса,
                // иначе будем рвать рабочий туннель из-за собственной диагностики.
                return true;
            }

            var timeoutMs = Math.Max(1, _settings.ProbeTimeoutSec) * 1000;

            try
            {
                using (var tcp = new TcpClient())
                {
                    var connectTask = tcp.ConnectAsync(IPAddress.Loopback, port);
                    var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (completed != connectTask)
                    {
                        _log.Debug("e2e-проверка: таймаут подключения через проброс.");
                        return false;
                    }

                    await connectTask.ConfigureAwait(false);
                    return tcp.Connected;
                }
            }
            catch (Exception ex)
            {
                _log.Debug("e2e-проверка: " + ex.Message);
                return false;
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
            _lastHeartbeatUtc = DateTime.UtcNow;
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

                        _lastHeartbeatUtc = DateTime.UtcNow;
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
                var command = _client.CreateCommand("cmd /c ver");
                var output = command.Execute() ?? string.Empty;
                return output.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        public void Stop()
        {
            _monitorTokenSource?.Cancel();
            _monitorTokenSource?.Dispose();
            _monitorTokenSource = null;
            lock (_sync)
            {
                DisconnectCore();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }
    }
}
