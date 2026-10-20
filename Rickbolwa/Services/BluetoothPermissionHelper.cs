using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Rickbolwa.Services
{
    /// <summary>
    /// Platform-neutral facade so shared pages can request Bluetooth
    /// permissions without referencing Android assemblies. The Android head
    /// installs the real implementation at startup:
    ///
    ///     BluetoothPermissionHelper.SetProvider(BluetoothPermissionProvider.RequestAsync);
    ///
    /// Call sites across all pages (`BluetoothPermissionHelper.RequestAsync()`)
    /// are unchanged. When no provider is installed (unit tests, other
    /// platforms) it returns true so flows can proceed against the mock printer.
    /// </summary>
    public static class BluetoothPermissionHelper
    {
        private static Func<Task<bool>> _provider;

        public static void SetProvider(Func<Task<bool>> provider)
            => _provider = provider;

        public static Task<bool> RequestAsync()
            => _provider != null ? _provider() : Task.FromResult(true);
    }
}