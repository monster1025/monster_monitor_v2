using System;
using System.Threading.Tasks;
using MonsterMonitor.Models;

namespace MonsterMonitor.Services
{
    public sealed class AppController : IDisposable
    {
        private readonly LogService _log;
        private readonly PowerManagementService _power;
        private readonly SsConfigService _ssConfig;
        private ProcessMonitorService _processMonitor;
        private SshTunnelService _sshTunnel;
        private IAuthMonitor _authMonitor;

        public AppController(LogService log, PowerManagementService power)
        {
            _log = log;
            _power = power;
            _ssConfig = new SsConfigService(_log);
        }

        public void Start(AppSettings settings)
        {
            Stop();

            _power.PreventSleep();
            _processMonitor = new ProcessMonitorService(_log);
            _sshTunnel = new SshTunnelService(_log);
            _authMonitor = new AuthMonitor(settings, _log);

            _ssConfig.EnsureConfig(settings);
            _processMonitor.Start(settings.SsProcessPath, settings.SsArguments);
            _sshTunnel.Start(settings);
            _authMonitor.StartMonitor().ContinueWith(
                t => _log.Error("Ошибка запуска монитора авторизации: " + t.Exception?.GetBaseException().Message),
                TaskContinuationOptions.OnlyOnFaulted);

            _log.Info("Сервисы приложения запущены.");
        }

        public void Stop()
        {
            _sshTunnel?.Dispose();
            _sshTunnel = null;

            _authMonitor?.StopMonitor();
            _authMonitor = null;

            _processMonitor?.Dispose();
            _processMonitor = null;

            _power.RestoreDefault();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
