using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using MonsterMonitor.Models;
using MonsterMonitor.Services;

namespace MonsterMonitor.UI
{
    public sealed class MainForm : Form
    {
        private readonly RichTextBox _console = new RichTextBox();
        private readonly Button _btnSettings = new Button();
        private readonly Button _btnExit = new Button();
        private readonly NotifyIcon _notifyIcon = new NotifyIcon();
        private readonly Timer _updateTimer = new Timer();
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

            BuildUi();
            BindEvents();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));

            _settings = AppSettings.Load();
            _controller = new AppController(_log, _power);
            _updateService = new GitHubUpdateService(_log, _settings);
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

            _notifyIcon.Visible = false;
            _updateTimer.Stop();
            _updateTimer.Dispose();
            _controller?.Dispose();
            _trayIcon?.Dispose();
        }

        private void AppendLog(LogEntry entry)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<LogEntry>(AppendLog), entry);
                return;
            }

            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _console.SelectionColor = GetColor(entry.Level);
            _console.AppendText($"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}{Environment.NewLine}");
            _console.SelectionColor = _console.ForeColor;
            _console.ScrollToCaret();
        }

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

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.SuspendLayout();
            // 
            // MainForm
            // 
            this.ClientSize = new System.Drawing.Size(284, 261);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Name = "MainForm";
            this.ResumeLayout(false);

        }
    }
}
