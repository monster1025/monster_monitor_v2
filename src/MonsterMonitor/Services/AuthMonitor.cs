using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MonsterMonitor.Models;

namespace MonsterMonitor.Services
{
    public class AuthMonitor : IAuthMonitor
    {
        private const string AuthWindowMarker = "XAuth request";

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private readonly AppSettings _settings;
        private readonly LogService _log;
        private CancellationTokenSource _monitorCancellation;
        private Task _monitorTask;

        public AuthMonitor(AppSettings settings, LogService log)
        {
            _settings = settings;
            _log = log;
        }

        private static string GetWindowTitle(IntPtr handle)
        {
            var length = GetWindowTextLength(handle);
            if (length <= 0)
            {
                return null;
            }

            var buff = new StringBuilder(length + 1);
            return GetWindowText(handle, buff, buff.Capacity) > 0 ? buff.ToString() : null;
        }

        private static string GetActiveWindowTitle()
        {
            return GetWindowTitle(GetForegroundWindow());
        }

        // Ищет окно авторизации через перечисление окон (без перебора всех процессов
        // и без утечки хендлов Process). Возвращает первое видимое подходящее окно.
        private static IntPtr FindAuthWindow()
        {
            var found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd))
                {
                    return true;
                }

                var title = GetWindowTitle(hWnd);
                if (title != null && title.IndexOf(AuthWindowMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = hWnd;
                    return false; // прекращаем перечисление
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        public async Task StartMonitor()
        {
            StopMonitor();
            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = Task.Run(async () => await CheckProcess(_monitorCancellation.Token));
            await Task.CompletedTask;
        }

        public void StopMonitor()
        {
            if (_monitorCancellation == null)
            {
                return;
            }

            _monitorCancellation.Cancel();
            try
            {
                _monitorTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Ignore shutdown errors.
            }
            finally
            {
                _monitorCancellation.Dispose();
                _monitorCancellation = null;
                _monitorTask = null;
            }
        }

        private async Task CheckProcess(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var authWindow = FindAuthWindow();
                    if (authWindow != IntPtr.Zero)
                    {
                        SetForegroundWindow(authWindow);

                        var activeWindowTitle = GetActiveWindowTitle();
                        if (activeWindowTitle?.IndexOf(AuthWindowMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var password = _settings.GetSystemPassword();
                            if (!string.IsNullOrEmpty(password))
                            {
                                SendKeys.SendWait(password);
                                SendKeys.SendWait("{ENTER}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Раньше здесь был MessageBox.Show из фонового потока — при повторяющемся
                    // исключении он заваливал рабочий стол окнами. Теперь просто пишем в лог.
                    _log.Warn("Ошибка мониторинга авторизации: " + ex.Message);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
    }

    public interface IAuthMonitor
    {
        Task StartMonitor();
        void StopMonitor();
    }
}
