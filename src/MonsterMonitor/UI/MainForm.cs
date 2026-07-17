using MonsterMonitor.Models;
using MonsterMonitor.Services;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MonsterMonitor.UI
{
    public sealed class MainForm : Form
    {
        private const int EmGetFirstVisibleLine = 0x00CE;
        private const int EmLineScroll = 0x00B6;
        private const int WmSetRedraw = 0x000B;

        // Ограничение размера буфера консоли, чтобы текст не рос бесконечно.
        private const int ConsoleMaxChars = 100000;
        private const int ConsoleTrimToChars = 80000;

        private readonly RichTextBox _console = new RichTextBox();
        private readonly Button _btnSettings = new Button();
        private readonly Button _btnExit = new Button();
        private readonly NotifyIcon _notifyIcon = new NotifyIcon();
        private readonly Timer _updateTimer = new Timer();
        private readonly Timer _logFlushTimer = new Timer();
        private readonly ConcurrentQueue<LogEntry> _pendingLogs = new ConcurrentQueue<LogEntry>();
        private readonly Icon _trayIcon;
        private readonly LogService _log = new LogService();
        private readonly PowerManagementService _power = new PowerManagementService();
        private GitHubUpdateService _updateService;
        private AppController _controller;
        private AppSettings _settings;
        private bool _allowClose;
        private bool _isUpdateCheckRunning;

        public MainForm()
        {
            Text = string.Format("Monster Monitor v{0}", Application.ProductVersion);
            Width = 980;
            Height = 620;
            StartPosition = FormStartPosition.CenterScreen;
            _trayIcon = LoadTrayIcon();
            Icon = _trayIcon;

            BuildUi();
            BindEvents();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            _settings = AppSettings.Load();
            _controller = new AppController(_log, _power);
            _updateService = new GitHubUpdateService(_log, _settings);
            ConfigureLogFlushTimer();
            RestartServices();
            ConfigureUpdateTimer();
            _ = RunUpdateCheckAsync(true);
        }

        private void BuildUi()
        {
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 42 };
            _btnSettings.Text = "Настройки";
            _btnSettings.Width = 110;
            _btnSettings.Location = new Point(10, 8);

            _btnExit.Text = "Выход";
            _btnExit.Width = 110;
            _btnExit.Location = new Point(130, 8);

            topPanel.Controls.Add(_btnSettings);
            topPanel.Controls.Add(_btnExit);

            _console.Dock = DockStyle.Fill;
            _console.ReadOnly = true;
            _console.BackColor = Color.Black;
            _console.ForeColor = Color.White;
            _console.Font = new Font("Consolas", 10f);

            Controls.Add(_console);
            Controls.Add(topPanel);

            _notifyIcon.Text = "Monster Monitor";
            _notifyIcon.Icon = _trayIcon;
            _notifyIcon.Visible = true;
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Открыть", null, (_, __) => RestoreFromTray());
            trayMenu.Items.Add("Выход", null, (_, __) => ExitApplication());
            _notifyIcon.ContextMenuStrip = trayMenu;
        }

        private void BindEvents()
        {
            Resize += MainFormOnResize;
            FormClosing += MainFormOnFormClosing;
            _notifyIcon.DoubleClick += (_, __) => RestoreFromTray();
            _btnSettings.Click += (_, __) => OpenSettings();
            _btnExit.Click += (_, __) => ExitApplication();
            _log.LogReceived += AppendLog;
        }

        private void OpenSettings()
        {
            using (var form = new SettingsForm(_settings))
            {
                if (form.ShowDialog(this) == DialogResult.OK)
                {
                    _log.Info("Настройки обновлены. Перезапускаю сервисы.");
                    RestartServices();
                }
            }
        }

        private void RestartServices()
        {
            try
            {
                _controller.Start(_settings);
            }
            catch (Exception ex)
            {
                _log.Error("Ошибка запуска сервисов: " + ex.Message);
            }
        }

        private void MainFormOnResize(object sender, EventArgs e)
        {
            if (WindowState != FormWindowState.Minimized)
            {
                return;
            }

            Hide();
            _notifyIcon.BalloonTipTitle = "Monster Monitor";
            _notifyIcon.BalloonTipText = "Приложение свернуто в трей.";
            _notifyIcon.ShowBalloonTip(1000);
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        public void RestoreAndActivateFromExternalSignal()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(RestoreAndActivateFromExternalSignal));
                return;
            }

            _log.Info("Получен сигнал активации от второго инстанса.");

            if (!Visible)
            {
                Show();
            }

            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            // Небольшой трюк для надежного вывода окна на передний план.
            TopMost = true;
            Activate();
            TopMost = false;
            Focus();
        }

        private void ExitApplication()
        {
            _allowClose = true;
            Close();
        }

        private void MainFormOnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                WindowState = FormWindowState.Minimized;
                return;
            }

            // Останавливаем таймеры заранее, чтобы не было тиков во время разрушения формы.
            _logFlushTimer.Stop();
            _updateTimer.Stop();
            _notifyIcon.Visible = false;
        }

        private void ConfigureLogFlushTimer()
        {
            // Логи копятся в очереди и выводятся пачкой ~10 раз в секунду,
            // а не по одной строке за событие — это снимает нагрузку на CPU и убирает мерцание.
            _logFlushTimer.Interval = 100;
            _logFlushTimer.Tick += (_, __) => FlushLogs();
            _logFlushTimer.Start();
        }

        // Вызывается из фоновых потоков — только кладём запись в очередь, без обращения к UI.
        private void AppendLog(LogEntry entry)
        {
            _pendingLogs.Enqueue(entry);
        }

        // Выполняется всегда в UI-потоке (таймер WinForms).
        private void FlushLogs()
        {
            if (_pendingLogs.IsEmpty || !_console.IsHandleCreated)
            {
                return;
            }

            var wasNearBottom = IsConsoleNearBottom();
            var firstVisibleLineBeforeAppend = GetFirstVisibleLine(_console);

            // Замораживаем отрисовку на время пакетного добавления — одна перерисовка вместо десятков.
            SendMessage(_console.Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            try
            {
                while (_pendingLogs.TryDequeue(out var entry))
                {
                    _console.SelectionStart = _console.TextLength;
                    _console.SelectionLength = 0;
                    _console.SelectionColor = GetColor(entry.Level);
                    _console.AppendText($"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}{Environment.NewLine}");
                    _console.SelectionColor = _console.ForeColor;
                }

                TrimConsole();
            }
            finally
            {
                SendMessage(_console.Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
                _console.Invalidate();
            }

            if (wasNearBottom)
            {
                _console.SelectionStart = _console.TextLength;
                _console.SelectionLength = 0;
                _console.ScrollToCaret();
                return;
            }

            var firstVisibleLineAfterAppend = GetFirstVisibleLine(_console);
            var linesToRestore = firstVisibleLineBeforeAppend - firstVisibleLineAfterAppend;
            if (linesToRestore != 0)
            {
                SendMessage(_console.Handle, EmLineScroll, IntPtr.Zero, (IntPtr)linesToRestore);
            }
        }

        // Обрезаем старые строки, а не сбрасываем весь буфер — плавно и без рывка скролла.
        private void TrimConsole()
        {
            if (_console.TextLength <= ConsoleMaxChars)
            {
                return;
            }

            var removeUpTo = _console.TextLength - ConsoleTrimToChars;
            var line = _console.GetLineFromCharIndex(removeUpTo);
            var cut = _console.GetFirstCharIndexFromLine(line + 1);
            if (cut <= 0)
            {
                cut = removeUpTo;
            }

            _console.Select(0, cut);
            _console.SelectedText = string.Empty;
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
        }

        private bool IsConsoleNearBottom()
        {
            if (_console.TextLength == 0)
            {
                return true;
            }

            var bottomLeftPoint = new Point(1, Math.Max(0, _console.ClientSize.Height - 1));
            var lastVisibleCharIndex = _console.GetCharIndexFromPosition(bottomLeftPoint);
            return lastVisibleCharIndex >= _console.TextLength - 2;
        }

        private static int GetFirstVisibleLine(RichTextBox richTextBox)
        {
            return SendMessage(richTextBox.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt32();
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private void ConfigureUpdateTimer()
        {
            _updateTimer.Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds;
            _updateTimer.Tick += async (_, __) => await RunUpdateCheckAsync(false);
            _updateTimer.Start();
            _log.Info("Автопроверка обновлений включена (раз в 1 час).");
        }

        private async Task RunUpdateCheckAsync(bool isInitialCheck)
        {
            if (_isUpdateCheckRunning)
            {
                return;
            }

            try
            {
                _isUpdateCheckRunning = true;
                if (isInitialCheck)
                {
                    // Небольшая задержка, чтобы окно и лог уже успели инициализироваться.
                    await Task.Delay(1000);
                }
                else
                {
                    _log.Info("Плановая проверка обновлений...");
                }

                await _updateService.CheckAndPrepareUpdateAsync();
            }
            catch (Exception ex)
            {
                // async void (Timer.Tick) — необработанное исключение уронило бы приложение.
                _log.Error("Ошибка проверки обновлений: " + ex.Message);
            }
            finally
            {
                _isUpdateCheckRunning = false;
            }
        }

        private static Color GetColor(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug:
                    return Color.Silver;
                case LogLevel.Warn:
                    return Color.Orange;
                case LogLevel.Error:
                    return Color.Red;
                default:
                    return Color.White;
            }
        }

        private static Icon LoadTrayIcon()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            return ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _log.LogReceived -= AppendLog;

                _logFlushTimer.Stop();
                _logFlushTimer.Dispose();
                _updateTimer.Stop();
                _updateTimer.Dispose();

                _controller?.Dispose();

                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _trayIcon?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
