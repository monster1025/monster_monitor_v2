using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MonsterMonitor.Models
{
    public sealed class AppSettings
    {
        private const string ProtectedPrefix = "dpapi:";

        public string SshHost { get; set; } = "127.0.0.1";
        public int SshPort { get; set; } = 22;
        public string SshUsername { get; set; } = "user";
        public string SshPasswordProtected { get; set; } = string.Empty;
        public string SystemPasswordProtected { get; set; } = string.Empty;
        public string ThreeProxyPasswordProtected { get; set; } = string.Empty;
        public bool SavePassword { get; set; } = false;
        public int RemotePort { get; set; } = 3328;
        public int LocalPort { get; set; } = 7829;
        public int MaxPingFailures { get; set; } = 3;
        public int ReconnectTimeoutSec { get; set; } = 45;
        public string Proxy { get; set; } = string.Empty;
        public string SsProcessPath { get; set; } = Path.Combine("App_Data", "ss", "ss.exe");
        public string SsArguments { get; set; } = string.Empty;

        public string GetPassword()
        {
            if (string.IsNullOrWhiteSpace(SshPasswordProtected))
            {
                return string.Empty;
            }

            if (!SshPasswordProtected.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                return SshPasswordProtected;
            }

            var protectedData = Convert.FromBase64String(SshPasswordProtected.Substring(ProtectedPrefix.Length));
            var unprotected = ProtectedData.Unprotect(protectedData, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }

        public void SetPassword(string password)
        {
            if (!SavePassword || string.IsNullOrEmpty(password))
            {
                SshPasswordProtected = string.Empty;
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(password);
            var protectedData = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            SshPasswordProtected = ProtectedPrefix + Convert.ToBase64String(protectedData);
        }

        public string GetSystemPassword()
        {
            if (string.IsNullOrWhiteSpace(SystemPasswordProtected))
            {
                return "";
            }

            if (!SystemPasswordProtected.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                return SystemPasswordProtected;
            }

            var protectedData = Convert.FromBase64String(SystemPasswordProtected.Substring(ProtectedPrefix.Length));
            var unprotected = ProtectedData.Unprotect(protectedData, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }

        public void SetSystemPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                SystemPasswordProtected = string.Empty;
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(password);
            var protectedData = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            SystemPasswordProtected = ProtectedPrefix + Convert.ToBase64String(protectedData);
        }

        public string GetThreeProxyPassword()
        {
            if (string.IsNullOrWhiteSpace(ThreeProxyPasswordProtected))
            {
                return string.Empty;
            }

            if (!ThreeProxyPasswordProtected.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                return ThreeProxyPasswordProtected;
            }

            var protectedData = Convert.FromBase64String(ThreeProxyPasswordProtected.Substring(ProtectedPrefix.Length));
            var unprotected = ProtectedData.Unprotect(protectedData, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }

        public void SetThreeProxyPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                ThreeProxyPasswordProtected = string.Empty;
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(password);
            var protectedData = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            ThreeProxyPasswordProtected = ProtectedPrefix + Convert.ToBase64String(protectedData);
        }

        public static string GetSettingsPath()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MonsterMonitor");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "settings.ini");
        }

        public static AppSettings Load()
        {
            var path = GetSettingsPath();
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var result = new AppSettings();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                map[key] = value;
            }

            result.SshHost = Get(map, nameof(SshHost), result.SshHost);
            result.SshPort = GetInt(map, nameof(SshPort), result.SshPort);
            result.SshUsername = Get(map, nameof(SshUsername), result.SshUsername);
            result.SshPasswordProtected = Get(map, nameof(SshPasswordProtected), result.SshPasswordProtected);
            result.SystemPasswordProtected = Get(map, nameof(SystemPasswordProtected), result.SystemPasswordProtected);
            result.ThreeProxyPasswordProtected = Get(map, nameof(ThreeProxyPasswordProtected), result.ThreeProxyPasswordProtected);
            result.SavePassword = GetBool(map, nameof(SavePassword), result.SavePassword);
            result.RemotePort = GetInt(map, nameof(RemotePort), result.RemotePort);
            result.LocalPort = GetInt(map, nameof(LocalPort), result.LocalPort);
            result.MaxPingFailures = GetInt(map, nameof(MaxPingFailures), result.MaxPingFailures);
            result.ReconnectTimeoutSec = GetInt(map, nameof(ReconnectTimeoutSec), result.ReconnectTimeoutSec);
            result.Proxy = Get(map, nameof(Proxy), result.Proxy);
            result.SsProcessPath = Get(map, nameof(SsProcessPath), result.SsProcessPath);
            result.SsArguments = Get(map, nameof(SsArguments), result.SsArguments);

            if (string.IsNullOrWhiteSpace(result.SystemPasswordProtected))
            {
                result.SetSystemPassword("STerra");
                result.Save();
            }

            return result;
        }

        public void Save()
        {
            var path = GetSettingsPath();
            var lines = new[]
            {
                $"{nameof(SshHost)}={SshHost}",
                $"{nameof(SshPort)}={SshPort}",
                $"{nameof(SshUsername)}={SshUsername}",
                $"{nameof(SshPasswordProtected)}={SshPasswordProtected}",
                $"{nameof(SystemPasswordProtected)}={SystemPasswordProtected}",
                $"{nameof(ThreeProxyPasswordProtected)}={ThreeProxyPasswordProtected}",
                $"{nameof(SavePassword)}={SavePassword}",
                $"{nameof(RemotePort)}={RemotePort}",
                $"{nameof(LocalPort)}={LocalPort}",
                $"{nameof(MaxPingFailures)}={MaxPingFailures}",
                $"{nameof(ReconnectTimeoutSec)}={ReconnectTimeoutSec}",
                $"{nameof(Proxy)}={Proxy}",
                $"{nameof(SsProcessPath)}={SsProcessPath}",
                $"{nameof(SsArguments)}={SsArguments}"
            };
            File.WriteAllLines(path, lines);
        }

        private static string Get(IDictionary<string, string> map, string key, string defaultValue)
        {
            return map.TryGetValue(key, out var value) ? value : defaultValue;
        }

        private static int GetInt(IDictionary<string, string> map, string key, int defaultValue)
        {
            if (map.TryGetValue(key, out var value) && int.TryParse(value, out var parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        private static bool GetBool(IDictionary<string, string> map, string key, bool defaultValue)
        {
            if (map.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            return defaultValue;
        }
    }
}
