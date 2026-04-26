using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using MonsterMonitor.Models;

namespace MonsterMonitor.Services
{
    public sealed class GitHubUpdateService
    {
        private const string GithubUser = "monster1025";
        private const string GithubRepo = "monster_monitor_v2";
        private const string GithubToken = "";

        private readonly LogService _log;
        private readonly AppSettings _settings;

        public GitHubUpdateService(LogService log, AppSettings settings)
        {
            _log = log;
            _settings = settings;
        }

        public async Task CheckAndPrepareUpdateAsync()
        {
            try
            {
                var currentVersion = GetCurrentVersion();
                _log.Info("Проверка обновлений...");
                _log.Info("Текущая версия: " + currentVersion);

                var release = await GetLatestReleaseAsync();
                if (release == null)
                {
                    _log.Warn("Не удалось получить данные о релизе с GitHub.");
                    return;
                }

                var latestVersion = ParseVersion(release.TagName);
                if (latestVersion == null)
                {
                    _log.Warn("Не удалось распарсить версию релиза: " + (release.TagName ?? "<empty>"));
                    return;
                }

                _log.Info("Версия на GitHub: " + latestVersion);
                if (latestVersion <= currentVersion)
                {
                    _log.Info("Обновление не требуется.");
                    return;
                }

                _log.Info("Найдена новая версия. Начинаю загрузку обновления...");
                var zipAsset = FindZipAsset(release.Assets);
                if (zipAsset == null)
                {
                    _log.Warn("У релиза нет .zip-ассета для автообновления.");
                    return;
                }

                var tempRoot = Path.Combine(Path.GetTempPath(), "MonsterMonitorUpdate");
                var packageDir = Path.Combine(tempRoot, latestVersion.ToString());
                var packageZip = Path.Combine(packageDir, "update.zip");
                var extractDir = Path.Combine(packageDir, "extracted");
                Directory.CreateDirectory(packageDir);

                await DownloadFileAsync(GetAssetDownloadUrl(zipAsset), packageZip);
                _log.Info("Архив обновления загружен: " + zipAsset.Name);

                if (Directory.Exists(extractDir))
                {
                    Directory.Delete(extractDir, true);
                }

                ZipFile.ExtractToDirectory(packageZip, extractDir);
                _log.Info("Архив обновления распакован.");

                var appDir = AppDomain.CurrentDomain.BaseDirectory;
                var exePath = Application.ExecutablePath;
                var updaterBat = Path.Combine(packageDir, "apply_update.bat");
                File.WriteAllText(updaterBat, BuildUpdaterScript(appDir, extractDir, exePath), Encoding.ASCII);

                _log.Info("Обновление подготовлено.");
                var restartNow = MessageBox.Show(
                    "Обновление загружено и готово к установке. Перезапустить приложение сейчас?",
                    "Monster Monitor",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (restartNow == DialogResult.Yes)
                {
                    _log.Info("Перезапуск приложения для применения обновления...");
                    StartUpdaterAndExit(updaterBat);
                    return;
                }

                _log.Warn("Перезапуск отложен. Обновление будет применено после следующего запуска через обновлятор.");
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                var statusCode = response != null ? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) : "n/a";
                var statusText = response != null ? response.StatusCode.ToString() : "unknown";
                _log.Error("Ошибка обновления (HTTP): " + statusCode + " " + statusText + ". " + ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error("Ошибка обновления: " + ex.Message);
            }
        }

        private static Version GetCurrentVersion()
        {
            Version version;
            if (!Version.TryParse(Application.ProductVersion, out version))
            {
                version = new Version(1, 0, 0, 0);
            }

            return version;
        }

        private static Version ParseVersion(string rawVersion)
        {
            if (string.IsNullOrWhiteSpace(rawVersion))
            {
                return null;
            }

            var normalized = rawVersion.Trim();
            if (normalized.StartsWith("v", true, CultureInfo.InvariantCulture))
            {
                normalized = normalized.Substring(1);
            }

            Version version;
            if (Version.TryParse(normalized, out version))
            {
                return version;
            }

            return null;
        }

        private async Task<GithubRelease> GetLatestReleaseAsync()
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.github.com/repos/{0}/{1}/releases/latest",
                GithubUser,
                GithubRepo);

            try
            {
                var request = CreateGithubRequest(url, "application/vnd.github+json");
                using (var response = (HttpWebResponse)await request.GetResponseAsync())
                using (var stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return null;
                    }

                    var serializer = new DataContractJsonSerializer(typeof(GithubRelease));
                    return serializer.ReadObject(stream) as GithubRelease;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    _log.Warn("Релиз /latest не найден. Пробую получить список релизов...");
                    return await GetLatestReleaseFromListAsync();
                }

                throw;
            }
        }

        private async Task<GithubRelease> GetLatestReleaseFromListAsync()
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "https://api.github.com/repos/{0}/{1}/releases",
                GithubUser,
                GithubRepo);

            var request = CreateGithubRequest(url, "application/vnd.github+json");
            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    return null;
                }

                var serializer = new DataContractJsonSerializer(typeof(List<GithubRelease>));
                var releases = serializer.ReadObject(stream) as List<GithubRelease>;
                if (releases == null || releases.Count == 0)
                {
                    return null;
                }

                return releases[0];
            }
        }

        private async Task DownloadFileAsync(string url, string filePath)
        {
            var request = CreateGithubRequest(url, "application/octet-stream");

            using (var response = (HttpWebResponse)await request.GetResponseAsync())
            using (var responseStream = response.GetResponseStream())
            using (var fileStream = File.Create(filePath))
            {
                if (responseStream == null)
                {
                    throw new InvalidOperationException("Пустой поток ответа при загрузке файла.");
                }

                await responseStream.CopyToAsync(fileStream);
            }
        }

        private static GithubAsset FindZipAsset(IList<GithubAsset> assets)
        {
            if (assets == null)
            {
                return null;
            }

            foreach (var asset in assets)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.Name))
                {
                    continue;
                }

                if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
            }

            return null;
        }

        private static string GetAssetDownloadUrl(GithubAsset asset)
        {
            if (asset == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(asset.ApiUrl))
            {
                return asset.ApiUrl;
            }

            return asset.DownloadUrl ?? string.Empty;
        }

        private HttpWebRequest CreateGithubRequest(string url, string accept)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = accept;
            request.UserAgent = "MonsterMonitorUpdater/1.0";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (!string.IsNullOrWhiteSpace(GithubToken))
            {
                request.Headers.Add("Authorization", "Bearer " + GithubToken);
            }
            if (!string.IsNullOrWhiteSpace(_settings.Proxy))
            {
                request.Proxy = GetProxy(_settings.Proxy);
            }

            return request;
        }

        private WebProxy GetProxy(string proxyUri)
        {
            var proxy = new WebProxy(new Uri(proxyUri), false);
            var cc = new CredentialCache();
            cc.Add(
                new Uri(proxyUri),
                "Negotiate", // if we don't set it to "Kerberos" we get error 407 with ---> the function requested is not supported.
                CredentialCache.DefaultNetworkCredentials);
            proxy.Credentials = cc;
            return proxy;
        }

        private static string BuildUpdaterScript(string appDir, string extractDir, string exePath)
        {
            return "@echo off" + Environment.NewLine +
                   "setlocal" + Environment.NewLine +
                   "timeout /t 2 /nobreak >nul" + Environment.NewLine +
                   "xcopy \"" + extractDir + "\\*\" \"" + appDir + "\\\" /E /I /Y /Q >nul" + Environment.NewLine +
                   "start \"\" \"" + exePath + "\"" + Environment.NewLine +
                   "endlocal" + Environment.NewLine;
        }

        private static void StartUpdaterAndExit(string updaterBat)
        {
            var psi = new ProcessStartInfo
            {
                FileName = updaterBat,
                WorkingDirectory = Path.GetDirectoryName(updaterBat) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);
            Application.Exit();
        }
    }

    [DataContract]
    public sealed class GithubRelease
    {
        [DataMember(Name = "tag_name")]
        public string TagName { get; set; }

        [DataMember(Name = "assets")]
        public List<GithubAsset> Assets { get; set; }
    }

    [DataContract]
    public sealed class GithubAsset
    {
        [DataMember(Name = "url")]
        public string ApiUrl { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "browser_download_url")]
        public string DownloadUrl { get; set; }
    }
}
