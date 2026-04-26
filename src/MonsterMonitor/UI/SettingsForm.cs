using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;
using MonsterMonitor.Models;

namespace MonsterMonitor.UI
{
    public sealed class SettingsForm : Form
    {
        private readonly AppSettings _settings;
        private readonly TextBox _txtHost = new TextBox();
        private readonly NumericUpDown _numSshPort = new NumericUpDown();
        private readonly TextBox _txtUser = new TextBox();
        private readonly TextBox _txtPassword = new TextBox();
        private readonly CheckBox _chkSavePassword = new CheckBox();
        private readonly NumericUpDown _numRemotePort = new NumericUpDown();
        private readonly NumericUpDown _numLocalPort = new NumericUpDown();
        private readonly NumericUpDown _numMaxFailures = new NumericUpDown();
        private readonly NumericUpDown _numReconnectTimeout = new NumericUpDown();
        private readonly TextBox _txtProxy = new TextBox();
        private readonly TextBox _txtSsPath = new TextBox();
        private readonly TextBox _txtSsArgs = new TextBox();
        private readonly TextBox _txtSystemPassword = new TextBox();
        private readonly CheckBox _chkShowSystemPassword = new CheckBox();
        private readonly TextBox _txtThreeProxyPassword = new TextBox();
        private readonly CheckBox _chkShowThreeProxyPassword = new CheckBox();
        private readonly Button _btnGenerateThreeProxyPassword = new Button();

        public SettingsForm(AppSettings settings)
        {
            _settings = settings;
            Text = "Настройки";
            Width = 560;
            Height = 560;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            BuildLayout();
            LoadValues();
        }

        private void BuildLayout()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10),
                ColumnCount = 2,
                RowCount = 15
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44f));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56f));

            Controls.Add(panel);

            AddRow(panel, "Сервер:", _txtHost, 0);
            AddRow(panel, "Порт:", _numSshPort, 1);
            AddRow(panel, "Логин:", _txtUser, 2);
            _txtPassword.PasswordChar = '*';
            AddRow(panel, "Пароль:", _txtPassword, 3);
            _chkSavePassword.Text = "Сохранить пароль";
            AddRow(panel, string.Empty, _chkSavePassword, 4);
            AddRow(panel, "Удаленный порт:", _numRemotePort, 5);
            AddRow(panel, "Локальный порт:", _numLocalPort, 6);
            AddRow(panel, "Макс. потерь:", _numMaxFailures, 7);
            AddRow(panel, "Таймаут reconnect (сек):", _numReconnectTimeout, 8);
            AddRow(panel, "Прокси (http://host:port):", _txtProxy, 9);
            AddRow(panel, "Путь к ss:", _txtSsPath, 10);
            AddRow(panel, "Аргументы ss:", _txtSsArgs, 11);
            BuildSystemPasswordRow(panel, 12);
            BuildThreeProxyPasswordRow(panel, 13);

            foreach (var num in new[] { _numSshPort, _numRemotePort, _numLocalPort })
            {
                num.Minimum = 1;
                num.Maximum = 65535;
            }

            _numMaxFailures.Minimum = 1;
            _numMaxFailures.Maximum = 10;
            _numReconnectTimeout.Minimum = 5;
            _numReconnectTimeout.Maximum = 60;

            var buttonsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            var btnSave = new Button { Text = "Сохранить", Width = 120, Height = 30 };
            var btnCancel = new Button { Text = "Отмена", Width = 120, Height = 30 };
            btnSave.Click += (_, __) => SaveAndClose();
            btnCancel.Click += (_, __) => DialogResult = DialogResult.Cancel;

            buttonsPanel.Controls.Add(btnSave);
            buttonsPanel.Controls.Add(btnCancel);
            panel.Controls.Add(buttonsPanel, 0, 14);
            panel.SetColumnSpan(buttonsPanel, 2);
        }

        private void BuildSystemPasswordRow(TableLayoutPanel panel, int row)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            panel.Controls.Add(new Label
            {
                Text = "Пароль Sterra:",
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            }, 0, row);

            var passPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false
            };

            _txtSystemPassword.Width = 170;
            _txtSystemPassword.PasswordChar = '*';

            _chkShowSystemPassword.Text = "Показать";
            _chkShowSystemPassword.AutoSize = true;
            _chkShowSystemPassword.CheckedChanged += (_, __) =>
            {
                _txtSystemPassword.PasswordChar = _chkShowSystemPassword.Checked ? '\0' : '*';
            };

            passPanel.Controls.Add(_txtSystemPassword);
            passPanel.Controls.Add(_chkShowSystemPassword);
            panel.Controls.Add(passPanel, 1, row);
        }

        private void BuildThreeProxyPasswordRow(TableLayoutPanel panel, int row)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            panel.Controls.Add(new Label
            {
                Text = "Пароль прокси:",
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill
            }, 0, row);

            var passPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false
            };

            _txtThreeProxyPassword.Width = 170;
            _txtThreeProxyPassword.PasswordChar = '*';

            _chkShowThreeProxyPassword.Text = "Показать";
            _chkShowThreeProxyPassword.AutoSize = true;
            _chkShowThreeProxyPassword.CheckedChanged += (_, __) =>
            {
                _txtThreeProxyPassword.PasswordChar = _chkShowThreeProxyPassword.Checked ? '\0' : '*';
            };

            _btnGenerateThreeProxyPassword.Text = "Сгенерировать";
            _btnGenerateThreeProxyPassword.AutoSize = true;
            _btnGenerateThreeProxyPassword.Click += (_, __) =>
            {
                _txtThreeProxyPassword.Text = GeneratePassword();
            };

            passPanel.Controls.Add(_txtThreeProxyPassword);
            passPanel.Controls.Add(_chkShowThreeProxyPassword);
            passPanel.Controls.Add(_btnGenerateThreeProxyPassword);
            panel.Controls.Add(passPanel, 1, row);
        }

        private static void AddRow(TableLayoutPanel panel, string label, Control control, int row)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));

            if (!string.IsNullOrEmpty(label))
            {
                panel.Controls.Add(new Label
                {
                    Text = label,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Dock = DockStyle.Fill
                }, 0, row);
            }

            control.Dock = DockStyle.Fill;
            panel.Controls.Add(control, 1, row);
        }

        private void LoadValues()
        {
            _txtHost.Text = _settings.SshHost;
            _numSshPort.Value = _settings.SshPort;
            _txtUser.Text = _settings.SshUsername;
            _txtPassword.Text = _settings.GetPassword();
            _chkSavePassword.Checked = _settings.SavePassword;
            _numRemotePort.Value = _settings.RemotePort;
            _numLocalPort.Value = _settings.LocalPort;
            _numMaxFailures.Value = _settings.MaxPingFailures;
            _numReconnectTimeout.Value = _settings.ReconnectTimeoutSec;
            _txtProxy.Text = _settings.Proxy;
            _txtSsPath.Text = _settings.SsProcessPath;
            _txtSsArgs.Text = _settings.SsArguments;
            _txtSystemPassword.Text = _settings.GetSystemPassword();
            _txtThreeProxyPassword.Text = _settings.GetThreeProxyPassword();
        }

        private void SaveAndClose()
        {
            if (string.IsNullOrWhiteSpace(_txtHost.Text))
            {
                MessageBox.Show(this, "Поле сервера обязательно.", "Валидация", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _settings.SshHost = _txtHost.Text.Trim();
            _settings.SshPort = (int)_numSshPort.Value;
            _settings.SshUsername = _txtUser.Text.Trim();
            _settings.SavePassword = _chkSavePassword.Checked;
            _settings.SetPassword(_txtPassword.Text);
            _settings.RemotePort = (int)_numRemotePort.Value;
            _settings.LocalPort = (int)_numLocalPort.Value;
            _settings.MaxPingFailures = (int)_numMaxFailures.Value;
            _settings.ReconnectTimeoutSec = (int)_numReconnectTimeout.Value;
            _settings.Proxy = _txtProxy.Text.Trim();
            _settings.SsProcessPath = string.IsNullOrWhiteSpace(_txtSsPath.Text)
                ? System.IO.Path.Combine("App_Data", "ss", "ss.exe")
                : _txtSsPath.Text.Trim();
            _settings.SsArguments = string.IsNullOrWhiteSpace(_txtSsArgs.Text)
                ? System.IO.Path.Combine("App_Data", "ss", "ss.cfg")
                : _txtSsArgs.Text.Trim();
            _settings.SetSystemPassword(_txtSystemPassword.Text.Trim());
            _settings.SetThreeProxyPassword(_txtThreeProxyPassword.Text.Trim());
            _settings.Save();

            DialogResult = DialogResult.OK;
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
