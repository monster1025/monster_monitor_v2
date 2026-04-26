using System;
using System.IO;
using System.Security.Cryptography;
using MonsterMonitor.Models;

namespace MonsterMonitor.Services
{
    public sealed class SsConfigService
    {
        private readonly LogService _log;

        public SsConfigService(LogService log)
        {
            _log = log;
        }

        public void EnsureConfig(AppSettings settings)
        {
            var password = settings.GetThreeProxyPassword();
            if (string.IsNullOrWhiteSpace(password))
            {
                password = GeneratePassword();
                settings.SetThreeProxyPassword(password);
                settings.Save();
                _log.Info("Сгенерирован пароль прокси и сохранен в настройках.");
            }
            var port = settings.LocalPort;

            var config = "users admin:CL:" + password + "\r\n" +
                         "auth strong\r\n" +
                         $"socks -i127.0.0.1 -p{port}";

            var configPath = GetConfigPath();
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(configPath, config);
            _log.Info("Сконфигурирован ss.cfg: " + configPath);
        }

        private static string GetConfigPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App_Data", "ss", "ss.cfg");
        }

        private static string GeneratePassword()
        {
            const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            const int length = 20;
            var bytes = new byte[length];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            var chars = new char[length];
            for (var i = 0; i < length; i++)
            {
                chars[i] = alphabet[bytes[i] % alphabet.Length];
            }

            return new string(chars);
        }
    }
}
