using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rickbolwa.Services
{
    public interface IPrinterService : IDisposable
    {
        /// <summary>Returns true when a paired, connectable printer is detected.</summary>
        Task<bool> IsPrinterAvailableAsync(CancellationToken cancellationToken = default);

        /// <summary>Prints <paramref name="receipt"/> using the chunked retry engine.</summary>
        Task PrintReceiptAsync(
            ReceiptData receipt,
            string logoAssetName = "Logo.png",
            string watermarkText = Rickbolwa.Helper.BrandConfig.ReceiptWatermark,
            PrintRetryPolicy retryPolicy = null,
            IProgress<PrintProgress> progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>Prints a self-test page.</summary>
        Task PrintTestPageAsync(
            PrintRetryPolicy retryPolicy = null,
            IProgress<PrintProgress> progress = null,
            CancellationToken cancellationToken = default);
    }

    // ══════════════════════════════════════════════════════════════
    //  MOCK PRINTER SERVICE  (unit tests / simulator)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Drop-in replacement that simulates the three-chunk printing pipeline
    /// without touching Bluetooth. Configurable failure injection lets you
    /// exercise the retry engine in automated tests.
    /// </summary>
    public sealed class MockPrinterService : IPrinterService
    {
        // ── Configuration ─────────────────────────────────────────

        /// <summary>
        /// When true the mock reports itself as available.
        /// Set to false to test the "no printer found" code path.
        /// </summary>
        public bool SimulatePrinterAvailable { get; set; } = true;

        /// <summary>
        /// Simulated milliseconds to transmit each chunk.
        /// Default 200 ms keeps tests fast but observable.
        /// </summary>
        public int ChunkTransmitDelayMs { get; set; } = 200;

        /// <summary>
        /// Zero-based index of the chunk that should throw on its first attempt.
        /// Set to -1 (default) to disable failure injection.
        /// </summary>
        public int FailOnChunkIndex { get; set; } = -1;

        /// <summary>
        /// How many times the injected failure should fire before succeeding.
        /// Defaults to 1 — simulates a single transient disconnect.
        /// </summary>
        public int FailureRepeatCount { get; set; } = 1;

        // ── Internal state ────────────────────────────────────────

        private int _injectedFailuresFired;
        private bool _disposed;

        // ── IPrinterService ───────────────────────────────────────

        public Task<bool> IsPrinterAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(SimulatePrinterAvailable);

        public async Task PrintReceiptAsync(
            ReceiptData receipt,
            string logoAssetName = "Logo.png",
            string watermarkText = Rickbolwa.Helper.BrandConfig.ReceiptWatermark,
            PrintRetryPolicy retryPolicy = null,
            IProgress<PrintProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!SimulatePrinterAvailable)
                throw new PrinterException("Mock: no printer available.");

            await RunMockSessionAsync(
                new[] { "Header", "Body", "Footer" },
                retryPolicy ?? PrintRetryPolicy.Default,
                progress,
                cancellationToken);
        }

        public async Task PrintTestPageAsync(
            PrintRetryPolicy retryPolicy = null,
            IProgress<PrintProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!SimulatePrinterAvailable)
                throw new PrinterException("Mock: no printer available.");

            await RunMockSessionAsync(
                new[] { "Test-Header", "Test-Footer" },
                retryPolicy ?? PrintRetryPolicy.Default,
                progress,
                cancellationToken);
        }

        // ── Simulation engine ─────────────────────────────────────

        private async Task RunMockSessionAsync(
            string[] chunkNames,
            PrintRetryPolicy policy,
            IProgress<PrintProgress> progress,
            CancellationToken token)
        {
            string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
            int total = chunkNames.Length;

            Report(progress, sessionId, "—", PrintChunkType.Header, 0, total, 0,
                   PrintProgressStatus.SessionStarted, "Mock session started.");

            for (int i = 0; i < chunkNames.Length; i++)
            {
                string chunkName = chunkNames[i];
                bool success = false;

                for (int attempt = 1; attempt <= policy.MaxAttempts; attempt++)
                {
                    bool isRetry = attempt > 1;

                    Report(progress, sessionId, chunkName, (PrintChunkType)(i % 3),
                           i, total, attempt,
                           isRetry ? PrintProgressStatus.ChunkRetrying
                                   : PrintProgressStatus.ChunkStarted,
                           isRetry
                               ? string.Format("Retrying '{0}' (attempt {1}/{2})…", chunkName, attempt, policy.MaxAttempts)
                               : string.Format("Sending '{0}'…", chunkName));

                    // Simulate transmission delay
                    await Task.Delay(ChunkTransmitDelayMs, token);

                    // Inject failure on the configured chunk index
                    if (i == FailOnChunkIndex && _injectedFailuresFired < FailureRepeatCount)
                    {
                        _injectedFailuresFired++;
                        Log(string.Format("Mock injecting failure on chunk '{0}' (fire #{1}).", chunkName, _injectedFailuresFired));

                        if (attempt < policy.MaxAttempts)
                        {
                            Report(progress, sessionId, chunkName, (PrintChunkType)(i % 3),
                                   i, total, attempt,
                                   PrintProgressStatus.ChunkRetrying,
                                   string.Format("Mock failure on '{0}'. Retrying in {1} ms…", chunkName, policy.RetryDelayMs));

                            await Task.Delay(policy.RetryDelayMs, token);
                            continue; // retry
                        }
                        else
                        {
                            Report(progress, sessionId, chunkName, (PrintChunkType)(i % 3),
                                   i, total, attempt,
                                   PrintProgressStatus.ChunkFailed,
                                   string.Format("Mock: '{0}' exhausted all attempts.", chunkName));

                            throw new PrinterException(
                                string.Format("Mock: chunk '{0}' failed after {1} attempt(s).", chunkName, policy.MaxAttempts));
                        }
                    }

                    // ── Success ───────────────────────────────────
                    Report(progress, sessionId, chunkName, (PrintChunkType)(i % 3),
                           i + 1, total, attempt,
                           PrintProgressStatus.ChunkCompleted,
                           string.Format("'{0}' complete ({1}/{2}).", chunkName, i + 1, total));
                    success = true;
                    break;
                }

                if (!success)
                    throw new PrinterException(string.Format("Mock: chunk '{0}' could not be sent.", chunkName));
            }

            Report(progress, sessionId, "—", PrintChunkType.Footer, total, total, 0,
                   PrintProgressStatus.SessionCompleted, "Mock session completed.");
        }

        private static void Report(
            IProgress<PrintProgress> progress,
            string sessionId, string chunkName, PrintChunkType chunkType,
            int chunkIndex, int total, int attempt,
            PrintProgressStatus status, string message)
        {
            progress?.Report(new PrintProgress
            {
                SessionId = sessionId,
                ChunkName = chunkName,
                ChunkType = chunkType,
                ChunkIndex = chunkIndex,
                TotalChunks = total,
                AttemptNumber = attempt,
                Status = status,
                Message = message
            });
        }

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine(string.Format("[MockPrinterService] {0}", msg));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
