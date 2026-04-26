using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MonsterMonitor.Models;

namespace MonsterMonitor.Services
{
    public class AuthMonitor: IAuthMonitor
    {
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        private readonly AppSettings _settings;
        private CancellationTokenSource _monitorCancellation;
        private Task _monitorTask;

        public AuthMonitor(AppSettings settings)
        {
            _settings = settings;
        }

        private string GetActiveWindowTitle()
        {
            const int nChars = 256;
            var buff = new StringBuilder(nChars);
            var handle = GetForegroundWindow();
            return GetWindowText(handle, buff, nChars) > 0 ? buff.ToString() : null;
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
                    Process[] processlist = Process.GetProcesses();
                    foreach (Process process in processlist)
                    {
                        if (!string.IsNullOrEmpty(process.MainWindowTitle) && process.MainWindowTitle.Contains("XAuth request"))
                        {
                            SetForegroundWindow(process.MainWindowHandle);
                        }
                    }

                    var activeWindowTitle = GetActiveWindowTitle();
                    if (activeWindowTitle?.Contains("XAuth request") == true)
                    {
                        var password = _settings.GetSystemPassword();
                        if (!string.IsNullOrEmpty(password))
                        {
                            SendKeys.SendWait(password);
                            SendKeys.SendWait("{ENTER}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message + ex.StackTrace);
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
