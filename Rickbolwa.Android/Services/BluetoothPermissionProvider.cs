using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Essentials;

namespace Rickbolwa.Droid.Services
{
    public static class BluetoothPermissionProvider
    {
        // ── Android 12+ (API 31+) permission strings ───────────────────────────
        private const string BLUETOOTH_CONNECT = "android.permission.BLUETOOTH_CONNECT";
        private const string BLUETOOTH_SCAN = "android.permission.BLUETOOTH_SCAN";

        /// <summary>
        /// Requests all Bluetooth permissions required for the current API level.
        /// Returns true when printing can proceed, false when a permission was denied.
        /// </summary>
        public static async Task<bool> RequestAsync()
        {
            try
            {
#if __ANDROID__
                int sdk = (int)Build.VERSION.SdkInt;

                // ── API 30 and below ──────────────────────────────────────────
                // Bluetooth only needs Location at runtime on these versions.
                if (sdk < 31)
                {
                    return await EnsureLocationPermissionAsync();
                }

                // ── API 31+ (Android 12 and above) ────────────────────────────
                // Requires BLUETOOTH_CONNECT (and optionally BLUETOOTH_SCAN).
                // Both must be declared in AndroidManifest.xml too.

                bool connectGranted = await EnsureRuntimePermissionAsync(BLUETOOTH_CONNECT);
                bool scanGranted = await EnsureRuntimePermissionAsync(BLUETOOTH_SCAN);

                // BLUETOOTH_SCAN is needed to discover/pair; BLUETOOTH_CONNECT
                // is needed to actually open a socket.  Both must be granted.
                return connectGranted && scanGranted;
#else
                // Non-Android platforms — not applicable
                return true;
#endif
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BluetoothPermissionHelper] {ex.Message}");
                return false;
            }
        }

#if __ANDROID__
        // ── Location (API < 31) ───────────────────────────────────────────────

        private static async Task<bool> EnsureLocationPermissionAsync()
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();

            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

            if (status != PermissionStatus.Granted)
            {
                // Try fine location as a fallback (some devices require it)
                var fine = await Permissions.CheckStatusAsync<Permissions.LocationAlways>();
                if (fine != PermissionStatus.Granted)
                    fine = await Permissions.RequestAsync<Permissions.LocationAlways>();
                return fine == PermissionStatus.Granted;
            }

            return true;
        }

        // ── Runtime permission check / request (API 31+) ─────────────────────

        private static Task<bool> EnsureRuntimePermissionAsync(string androidPermission)
        {
            var tcs = new TaskCompletionSource<bool>();

            try
            {
                var activity = GetCurrentActivity();
                if (activity == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BluetoothPermissionHelper] No current activity for {androidPermission}");
                    tcs.SetResult(false);
                    return tcs.Task;
                }

                // Check if already granted
                Android.Content.PM.Permission check =
                    ContextCompat.CheckSelfPermission(activity, androidPermission);
                if (check == Android.Content.PM.Permission.Granted)
                {
                    tcs.SetResult(true);
                    return tcs.Task;
                }

                // Use Xamarin.Essentials custom permission wrapper
                // This queues the request on the main thread and awaits the result.
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        var granted = await RequestSinglePermissionAsync(activity, androidPermission);
                        tcs.SetResult(granted);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[BluetoothPermissionHelper] Permission request error: {ex.Message}");
                        tcs.SetResult(false);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BluetoothPermissionHelper] EnsureRuntimePermissionAsync error: {ex.Message}");
                tcs.SetResult(false);
            }

            return tcs.Task;
        }

        private static async Task<bool> RequestSinglePermissionAsync(Activity activity, string permission)
        {
            // Use Xamarin.Essentials built-in mechanism via a custom permission class
            // so we get the proper dialog + result callback handling for free.
            try
            {
                var customPerm = permission == BLUETOOTH_CONNECT
                    ? (Permissions.BasePermission)new BluetoothConnectPermission()
                    : (Permissions.BasePermission)new BluetoothScanPermission();

                var status = await customPerm.CheckStatusAsync();
                if (status != PermissionStatus.Granted)
                    status = await customPerm.RequestAsync();

                return status == PermissionStatus.Granted;
            }
            catch
            {
                // Fallback: direct ActivityCompat request (fire-and-forget result)
                ActivityCompat.RequestPermissions(
                    activity,
                    new[] { permission },
                    requestCode: permission == BLUETOOTH_CONNECT ? 1001 : 1002);

                // Give Android time to show the dialog and let the user respond
                await Task.Delay(4000);

                // Re-check after the delay
                Android.Content.PM.Permission result =
                    ContextCompat.CheckSelfPermission(activity, permission);
                return result == Android.Content.PM.Permission.Granted;
            }
        }

        private static Activity GetCurrentActivity()
        {
            try
            {
                // Xamarin.Essentials exposes the current activity
                return Platform.CurrentActivity;
            }
            catch
            {
                return null;
            }
        }
#endif
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Custom Xamarin.Essentials permission wrappers for Android 12+
    // ═══════════════════════════════════════════════════════════════════════

#if __ANDROID__
    /// <summary>BLUETOOTH_CONNECT — required on Android 12+ to open RFCOMM sockets.</summary>
    public class BluetoothConnectPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions
            => new[]
            {
                (Android.Manifest.Permission.BluetoothConnect, true)
            };
    }

    /// <summary>BLUETOOTH_SCAN — required on Android 12+ to discover paired devices.</summary>
    public class BluetoothScanPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions
            => new[]
            {
                (Android.Manifest.Permission.BluetoothScan, true)
            };
    }
#endif
}