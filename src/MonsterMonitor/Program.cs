using System;
using System.Threading;
using System.Windows.Forms;
using MonsterMonitor.UI;

namespace MonsterMonitor
{
    internal static class Program
    {
        private const string SingleInstanceMutexName = @"Global\MonsterMonitor_v2_SingleInstance";
        private const string ActivateEventName = @"Global\MonsterMonitor_v2_Activate";

        [STAThread]
        private static void Main()
        {
            var createdNew = false;
            using (var mutex = new Mutex(true, SingleInstanceMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    TrySignalExistingInstance();
                    return;
                }

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
                using (var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName))
                {
                    var form = new MainForm();
                    ThreadPool.RegisterWaitForSingleObject(
                        activateEvent,
                        (state, timedOut) =>
                        {
                            var mainForm = state as MainForm;
                            mainForm?.RestoreAndActivateFromExternalSignal();
                        },
                        form,
                        Timeout.Infinite,
                        false);

                    Application.Run(form);
                }
            }
        }

        private static void TrySignalExistingInstance()
        {
            try
            {
                using (var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName))
                {
                    activateEvent.Set();
                }
            }
            catch
            {
                // Если не удалось отправить сигнал, просто завершаем второй инстанс.
            }
        }
    }
}
