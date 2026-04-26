using System;
using System.Windows.Forms;
using MonsterMonitor.UI;

namespace MonsterMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.ThreadException += (sender, args) =>
            {
                MessageBox.Show(
                    args.Exception.ToString(),
                    "Необработанное исключение UI-потока",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                MessageBox.Show(
                    args.ExceptionObject.ToString(),
                    "Критическое исключение",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
