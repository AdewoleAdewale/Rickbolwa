using Android.Bluetooth;
using Java.Util;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rickbolwa.Services
{
    // ══════════════════════════════════════════════════════════════
    //  PRINTER HEALTH REPORT
    // ══════════════════════════════════════════════════════════════

    public enum PrinterHealthStatus
    {
        Healthy,        // connected, latency acceptable
        Degraded,       // connected but slow or noisy
        Unavailable,    // paired but cannot connect
        NoPairedDevice, // no known printer in bonded list
        BluetoothOff,   // adapter disabled
        PermissionDenied
    }

    /// <summary>Snapshot of the printer's connection quality at a point in time.</summary>
    public sealed class PrinterHealthReport
    {
        public PrinterHealthStatus Status { get; set; }
        public string DeviceName { get; set; }
        public string DeviceAddress { get; set; }
        public TimeSpan? ConnectLatency { get; set; }
        public string StatusMessage { get; set; }
        public DateTime MeasuredAt { get; set; } = DateTime.UtcNow;

        /// <summary>Human-readable one-liner suitable for a status bar or toast.</summary>
        public string Summary
        {
            get
            {
                switch (Status)
                {
                    case PrinterHealthStatus.Healthy:
                        return $"✅ {DeviceName} — connected ({(ConnectLatency.HasValue ? ConnectLatency.Value.TotalMilliseconds.ToString("F0") : "0")} ms)";

                    case PrinterHealthStatus.Degraded:
                        return $"⚠️ {DeviceName} — slow ({(ConnectLatency.HasValue ? ConnectLatency.Value.TotalMilliseconds.ToString("F0") : "0")} ms)";

                    case PrinterHealthStatus.Unavailable:
                        return $"❌ {DeviceName} — cannot connect";

                    case PrinterHealthStatus.NoPairedDevice:
                        return "❌ No paired printer found";

                    case PrinterHealthStatus.BluetoothOff:
                        return "❌ Bluetooth is turned off";

                    case PrinterHealthStatus.PermissionDenied:
                        return "❌ Bluetooth permission denied";

                    default:
                        return "❓ Unknown";
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  PRINTER DIAGNOSTICS SERVICE
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Probes the printer connection, measures connect latency over multiple
    /// rounds, and surfaces a structured <see cref="PrinterHealthReport"/>.
    /// Useful for a "Printer Settings" screen or pre-flight check before a
    /// high-value payment.
    /// </summary>
    public sealed class PrinterDiagnosticsService
    {
        private const string SPP_UUID = "00001101-0000-1000-8000-00805f9b34fb";
        private const int CONNECT_TIMEOUT_MS = 10_000;
        private const int DEGRADED_LATENCY_MS = 5_000; // above this = Degraded

        private static readonly HashSet<string> SupportedPrinters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "RRN2OP",
                "MPT-II", "MTP-II_89EB", "MTP-II-6111",
                "RPP02N", "RPP210",
                "MP300", "IposPrinter", "FP8800", "S60",
                "InnerPrinter", "Internal Bluetooth Printer",
                "printer001", "b906", "ANDROID BT", "CS10",
                "Q2i"
            };

        // ── Single probe ──────────────────────────────────────────

        /// <summary>
        /// Performs one connection probe and returns the health report.
        /// Safe to call from any thread; wraps all Android exceptions.
        /// </summary>
        public async Task<PrinterHealthReport> CheckHealthAsync(
       CancellationToken cancellationToken = default)
        {
            try
            {
                bool granted = await BluetoothPermissionHelper.RequestAsync();
                if (!granted)
                    return Report(PrinterHealthStatus.PermissionDenied, null,
                                  "Bluetooth permission was denied.");

                var adapter = BluetoothAdapter.DefaultAdapter;
                if (adapter == null || !adapter.IsEnabled)
                    return Report(PrinterHealthStatus.BluetoothOff, null,
                                  "Bluetooth adapter is disabled.");

                // Same discovery as the print service: built-in printer by MAC first,
                // then known external printer names.
                var device = BluetoothPrinterService.FindPrinterDevice(adapter);

                if (device == null)
                    return Report(PrinterHealthStatus.NoPairedDevice, null,
                                  "No recognised printer is paired. Open Android Settings → Bluetooth and pair first.");

                // Time the connection
                var sw = Stopwatch.StartNew();
                BluetoothSocket socket = null;
                try
                {
                    socket = device.CreateRfcommSocketToServiceRecord(UUID.FromString(SPP_UUID));

                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        cts.CancelAfter(CONNECT_TIMEOUT_MS);

                        var connectTask = socket.ConnectAsync();
                        var completedTask = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, cts.Token));
                        if (completedTask != connectTask)
                            return Report(PrinterHealthStatus.Unavailable, device,
                                          $"Connection timed out after {CONNECT_TIMEOUT_MS / 1000} s.");

                        await connectTask;
                        sw.Stop();

                        if (!socket.IsConnected)
                            return Report(PrinterHealthStatus.Unavailable, device,
                                          "Socket connected but IsConnected = false.");

                        var latency = sw.Elapsed;
                        var status = latency.TotalMilliseconds > DEGRADED_LATENCY_MS
                                      ? PrinterHealthStatus.Degraded
                                      : PrinterHealthStatus.Healthy;

                        return new PrinterHealthReport
                        {
                            Status = status,
                            DeviceName = device.Name,
                            DeviceAddress = device.Address,
                            ConnectLatency = latency,
                            StatusMessage = status == PrinterHealthStatus.Degraded
                                             ? $"High latency ({latency.TotalMilliseconds:F0} ms). " +
                                               "Move closer to the printer."
                                             : "Printer is online and responding normally."
                        };
                    }
                }
                catch (Exception ex)
                {
                    return Report(PrinterHealthStatus.Unavailable, device,
                                  $"Connection failed: {ex.Message}");
                }
                finally
                {
                    SafeDispose(socket);
                }
            }
            catch (Exception ex)
            {
                return Report(PrinterHealthStatus.Unavailable, null,
                              $"Unexpected diagnostics error: {ex.Message}");
            }
        }
        // ── Multi-round latency measurement ───────────────────────

        /// <summary>
        /// Runs <paramref name="rounds"/> sequential probes and returns per-round
        /// latency samples plus aggregate min/avg/max. Use on a diagnostics page
        /// to give the user reliable signal quality information.
        /// </summary>
        public async Task<LatencyReport> MeasureLatencyAsync(
            int rounds = 3,
            CancellationToken cancellationToken = default)
        {
            var samples = new List<double>();
            string deviceName = "—";
            string deviceAddress = "—";
            string lastError = null;

            for (int i = 0; i < rounds; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var report = await CheckHealthAsync(cancellationToken);
                deviceName = report.DeviceName ?? deviceName;
                deviceAddress = report.DeviceAddress ?? deviceAddress;

                if (report.ConnectLatency.HasValue)
                    samples.Add(report.ConnectLatency.Value.TotalMilliseconds);
                else
                    lastError = report.StatusMessage;

                // Brief pause between rounds to let the printer's RFCOMM stack settle
                if (i < rounds - 1)
                    await Task.Delay(500, cancellationToken);
            }

            return new LatencyReport
            {
                DeviceName = deviceName,
                DeviceAddress = deviceAddress,
                Samples = samples.AsReadOnly(),
                MinMs = samples.Count > 0 ? samples.Min() : 0,
                AvgMs = samples.Count > 0 ? samples.Average() : 0,
                MaxMs = samples.Count > 0 ? samples.Max() : 0,
                SuccessfulRounds = samples.Count,
                TotalRounds = rounds,
                LastError = lastError
            };
        }

        // ── Paired printer enumeration ────────────────────────────

        /// <summary>
        /// Returns every Bluetooth device that is both bonded and appears in
        /// the supported-printer list. Useful for a "Select Printer" picker.
        /// </summary>
        public static List<PairedPrinterInfo> GetPairedPrinters()
        {
            var result = new List<PairedPrinterInfo>();
            var adapter = BluetoothAdapter.DefaultAdapter;
            if (adapter == null || !adapter.IsEnabled) return result;

            foreach (var device in adapter.BondedDevices ?? Enumerable.Empty<BluetoothDevice>())
            {
                if (BluetoothPrinterService.IsSupportedPrinter(device))
                {
                    result.Add(new PairedPrinterInfo
                    {
                        Name = device.Name,
                        Address = device.Address,
                        BondState = device.BondState.ToString()
                    });
                }
            }
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────

        private static PrinterHealthReport Report(
            PrinterHealthStatus status, BluetoothDevice device, string message)
            => new PrinterHealthReport
            {
                Status = status,
                DeviceName = device?.Name,
                DeviceAddress = device?.Address,
                StatusMessage = message
            };

        private static void SafeDispose(IDisposable obj)
        { try { obj?.Dispose(); } catch { } }
    }

    // ══════════════════════════════════════════════════════════════
    //  LATENCY REPORT
    // ══════════════════════════════════════════════════════════════

    public sealed class LatencyReport
    {
        public string DeviceName { get; set; }
        public string DeviceAddress { get; set; }
        public IReadOnlyList<double> Samples { get; set; }
        public double MinMs { get; set; }
        public double AvgMs { get; set; }
        public double MaxMs { get; set; }

        public string Message { get; set; }
        public int SuccessfulRounds { get; set; }
        public int TotalRounds { get; set; }
        public string LastError { get; set; }

        public string Summary
        {
            get
            {
                if (SuccessfulRounds == 0)
                {
                    return string.Format(
                        "All {0} probe(s) failed: {1}",
                        TotalRounds,
                        LastError
                    );
                }

                return string.Format(
                    "{0}: min {1:F0} ms / avg {2:F0} ms / max {3:F0} ms ({4}/{5} rounds)",
                    DeviceName,
                    MinMs,
                    AvgMs,
                    MaxMs,
                    SuccessfulRounds,
                    TotalRounds
                );
            }
        }
    }
    // ══════════════════════════════════════════════════════════════
    //  PAIRED PRINTER INFO
    // ══════════════════════════════════════════════════════════════

    public sealed class PairedPrinterInfo
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public string BondState { get; set; }
    }
}