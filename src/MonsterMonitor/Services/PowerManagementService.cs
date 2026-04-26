using System;
using System.Runtime.InteropServices;

namespace MonsterMonitor.Services
{
    public sealed class PowerManagementService
    {
        [Flags]
        private enum ExecutionState : uint
        {
            ES_SYSTEM_REQUIRED = 0x00000001,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_CONTINUOUS = 0x80000000
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

        public void PreventSleep()
        {
            SetThreadExecutionState(
                ExecutionState.ES_CONTINUOUS |
                ExecutionState.ES_SYSTEM_REQUIRED |
                ExecutionState.ES_DISPLAY_REQUIRED);
        }

        public void RestoreDefault()
        {
            SetThreadExecutionState(ExecutionState.ES_CONTINUOUS);
        }
    }
}
