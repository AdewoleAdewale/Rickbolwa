using Android.Bluetooth;
using Android.Graphics;
using Java.Util;
using Newtonsoft.Json;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xamarin.Essentials;

namespace Rickbolwa.Services
{
 
    // =========================================================================
    //  PRINT SECTION ENUM
    // =========================================================================

    public enum PrintSection
    {
        Init = 0,
        Logo = 1,
        Header = 2,
        Body = 3,
        Totals = 4,
        Footer = 5,
        FeedAndCut = 6
    }

    // =========================================================================
    //  PRINT CHUNK
    // =========================================================================

    public sealed class PrintChunk
    {
        public string Id { get; }
        public string Label { get; }
        public PrintSection Section { get; }
        public byte[] Data { get; }

        public PrintChunk(PrintSection section, string label, byte[] data)
        {
            Section = section;
            Label = label ?? throw new ArgumentNullException(nameof(label));
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Id = string.Format("{0}:{1}", (int)section, label);
        }

        public override string ToString() =>
            string.Format("[{0}] {1} ({2} B)", Section, Label, Data.Length);
    }

    // =========================================================================
    //  PRINT JOB STATE  –  checkpoint / resume tracking
    // =========================================================================

    public sealed class PrintJobState
    {
        private static readonly object _syncLock = new object();
        private readonly HashSet<string> _completedIds;

        public PrintJobState(string jobId, bool persistState = false)
        {
            JobId = jobId ?? throw new ArgumentNullException(nameof(jobId));
            PersistState = persistState;
            _completedIds = LoadPersistedIds();
        }

        public string JobId { get; }
        public bool PersistState { get; set; }

        public int CompletedCount { get { lock (_syncLock) return _completedIds.Count; } }

        public bool IsCompleted(PrintChunk chunk)
        {
            lock (_syncLock) return _completedIds.Contains(chunk.Id);
        }

        public void MarkCompleted(PrintChunk chunk)
        {
            lock (_syncLock)
            {
                _completedIds.Add(chunk.Id);
                if (PersistState) Persist();
            }
        }

        public void Reset()
        {
            lock (_syncLock)
            {
                _completedIds.Clear();
                if (PersistState) Preferences.Remove(PrefKey);
            }
        }

        private string PrefKey { get { return string.Format("PrintJobState_{0}", JobId); } }

        private HashSet<string> LoadPersistedIds()
        {
            if (!PersistState) return new HashSet<string>();
            try
            {
                var json = Preferences.Get(PrefKey, null);
                if (!string.IsNullOrEmpty(json))
                    return JsonConvert.DeserializeObject<HashSet<string>>(json)
                           ?? new HashSet<string>();
            }
            catch { }
            return new HashSet<string>();
        }

        private void Persist()
        {
            try { Preferences.Set(PrefKey, JsonConvert.SerializeObject(_completedIds)); }
            catch (Exception ex) { Log(string.Format("persist failed – {0}", ex.Message)); }
        }

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine(string.Format("[PrintJobState] {0}", msg));
    }

    // =========================================================================
    //  PRINT SESSION RESULT
    // =========================================================================

    public sealed class PrintSessionResult
    {
        public bool Success { get; set; }
        public int ChunksSent { get; set; }
        public int TotalChunks { get; set; }
        public string FailedChunkLabel { get; set; }
        public string ErrorMessage { get; set; }

        public static PrintSessionResult Ok(int sent, int total)
        {
            return new PrintSessionResult { Success = true, ChunksSent = sent, TotalChunks = total };
        }

        public static PrintSessionResult Fail(string chunk, string error, int sent, int total)
        {
            return new PrintSessionResult
            {
                Success = false,
                FailedChunkLabel = chunk,
                ErrorMessage = error,
                ChunksSent = sent,
                TotalChunks = total
            };
        }
    }

    // =========================================================================
    //  BLUETOOTH PRINTER SERVICE  –  implements IPrinterService
    // =========================================================================

    public sealed class BluetoothPrinterService : IPrinterService
    {
        #region ── Constants ──────────────────────────────────────────────────

        private const string SPP_UUID = "00001101-0000-1000-8000-00805f9b34fb";
        private const int DOTS_58MM = 384;
        private const int DOTS_80MM = 576;
        private const int CONNECT_TIMEOUT_MS = 15_000;
        private const int PRINT_TIMEOUT_MS = 60_000;
        private const int CHUNK_SIZE = 512;
        private const int INTER_CHUNK_DELAY = 20;
        private const int FLUSH_SETTLE_MS = 800;
        private const float MONO_THRESHOLD = 128f;

        /// <summary>
        /// Fixed MAC of the built-in printer on Sunyard S60 and compatible POS terminals.
        /// It only shows up in the paired list when "Bluetooth Printer" is enabled
        /// in the POS Setup app.
        /// </summary>
        private const string BUILTIN_PRINTER_MAC = "FF:FF:FF:FF:FF:FF";

        // ── Raster body / watermark (ported from YIRS Hospital SDK) ──────
        private const int RASTER_BAND_HEIGHT = 256;
        private const int BODY_PADDING = 24;
        private const float BANNER_SIZE_RATIO = 1.4f;
        private const int WATERMARK_GRAY = 185;
        private const float WATERMARK_TEXT_MAX_SIZE = 72f;
        private const float WATERMARK_TEXT_MIN_SIZE = 28f;

        #endregion

        #region ── ESC/POS Commands ───────────────────────────────────────────

        private static readonly byte[] CMD_INIT = { 0x1B, 0x40 };
        private static readonly byte[] CMD_ALIGN_LEFT = { 0x1B, 0x61, 0x00 };
        private static readonly byte[] CMD_ALIGN_CENTER = { 0x1B, 0x61, 0x01 };
        private static readonly byte[] CMD_BOLD_ON = { 0x1B, 0x45, 0x01 };
        private static readonly byte[] CMD_BOLD_OFF = { 0x1B, 0x45, 0x00 };
        private static readonly byte[] CMD_DHEIGHT_ON = { 0x1D, 0x21, 0x01 };
        private static readonly byte[] CMD_DHEIGHT_OFF = { 0x1D, 0x21, 0x00 };
        private static readonly byte[] CMD_DWIDTH_ON = { 0x1D, 0x21, 0x10 };
        private static readonly byte[] CMD_DWIDTH_OFF = { 0x1D, 0x21, 0x00 };
        private static readonly byte[] CMD_UNDERLINE_ON = { 0x1B, 0x2D, 0x01 };
        private static readonly byte[] CMD_UNDERLINE_OFF = { 0x1B, 0x2D, 0x00 };
        private static readonly byte[] CMD_FONT_SMALL = { 0x1B, 0x4D, 0x01 };
        private static readonly byte[] CMD_FONT_NORMAL = { 0x1B, 0x4D, 0x00 };
        private static readonly byte[] CMD_LF = { 0x0A };
        private static readonly byte[] CMD_FEED_CUT = { 0x1B, 0x64, 0x04, 0x1D, 0x56, 0x42, 0x00 };

        private static readonly byte[] CHUNK_PREAMBLE = ConcatBytes(
            CMD_ALIGN_LEFT,
            CMD_BOLD_OFF,
            CMD_DHEIGHT_OFF,
            CMD_DWIDTH_OFF,
            CMD_UNDERLINE_OFF,
            CMD_FONT_NORMAL
        );

        #endregion

        #region ── Supported Printer Names ────────────────────────────────────

        private static readonly HashSet<string> SupportedPrinters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "RRN2OP",
                "MPT-II", "MTP-II_89EB", "MTP-II-6111",
                "RPP02N", "RPP210",
                "MP300", "IposPrinter", "FP8800", "S60",
                "InnerPrinter", "Internal Bluetooth Printer",
                "printer001", "b906", "ANDROID BT", "CS10",
                "Q2i",
                // Added from the YIRS Hospital SDK
                "SUNYARD_S60", "Bluetooth Printer", "MP-58T"
            };

        #endregion

        #region ── Fields ─────────────────────────────────────────────────────

        private readonly int _printerDots;
        private readonly int _charsPerLine;
        private bool _disposed;

        private PrintJobState _activeJobState;
        private List<PrintChunk> _activeJobChunks;
        private string _activeJobId;

        #endregion

        public BluetoothPrinterService(bool use80mm = false)
        {
            _printerDots = use80mm ? DOTS_80MM : DOTS_58MM;
            _charsPerLine = use80mm ? 48 : 32;
        }



        // =====================================================================
        //  IPrinterService  –  IsPrinterAvailableAsync
        // =====================================================================

        public async Task<bool> IsPrinterAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var adapter = BluetoothAdapter.DefaultAdapter;
                if (adapter == null || !adapter.IsEnabled) return false;

                var device = FindPrinterDevice(adapter);
                if (device == null) return false;

                // Uses the same multi-strategy opener as printing, so built-in
                // printers (no SDP record) are not wrongly reported as offline.
                // A shorter per-strategy timeout keeps the check responsive.
                BluetoothSocket socket = null;
                try
                {
                    socket = await OpenSocketAsync(device, cancellationToken, 6_000);
                    return socket.IsConnected;
                }
                catch { return false; }
                finally { SafeDispose(socket); }
            }
            catch { return false; }
        }


        // =====================================================================
        //  IPrinterService  –  PrintReceiptAsync  (with PrintRetryPolicy)
        // =====================================================================

        /// <summary>
        /// IPrinterService implementation.  Delegates to the resumable overload
        /// using the supplied <paramref name="retryPolicy"/> (or default if null).
        /// Progress events are forwarded with <see cref="PrintChunkType"/> and
        /// <see cref="PrintProgressStatus"/> so the UI can show chunk-level status.
        /// </summary>
        public async Task PrintReceiptAsync(
            ReceiptData receipt,
            string logoAssetName = "Logo.png",
            string watermarkText = "RICKBOLWA",
            PrintRetryPolicy retryPolicy = null,
            IProgress<PrintProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            var policy = retryPolicy ?? PrintRetryPolicy.Default;

            // Apply the policy to this service instance for the duration of the call
            MaxChunkRetries = policy.MaxAttempts;
            RetryBaseDelayMs = policy.RetryDelayMs;

            string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();

            // Wrap progress so every internal chunk event emits a PrintProgress object
            var bridgeProgress = BuildBridgeProgress(progress, sessionId);

            var result = await PrintReceiptAsync(
                receipt,
                jobId: null,
                logoAssetName: logoAssetName,
                watermarkText: watermarkText,
                persistState: false,
                internalProgress: bridgeProgress,
                cancellationToken: cancellationToken);

            if (!result.Success)
                throw new PrinterException(
                    string.Format("Print failed at '{0}': {1} ({2}/{3} sections sent)",
                        result.FailedChunkLabel,
                        result.ErrorMessage,
                        result.ChunksSent,
                        result.TotalChunks));
        }

        // =====================================================================
        //  IPrinterService  –  PrintTestPageAsync  (with PrintRetryPolicy)
        // =====================================================================

        public async Task PrintTestPageAsync(
            PrintRetryPolicy retryPolicy = null,
            IProgress<PrintProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            var policy = retryPolicy ?? PrintRetryPolicy.Default;
            MaxChunkRetries = policy.MaxAttempts;
            RetryBaseDelayMs = policy.RetryDelayMs;

            await RequireBluetoothPermissionsAsync();
            EnsureBluetoothReady();

            string sessionId = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper();
            var bridgeProgress = BuildBridgeProgress(progress, sessionId);

            var chunks = await Task.Run(() => BuildTestChunks(), cancellationToken);
            var state = new PrintJobState(string.Format("test_{0:yyyyMMddHHmmssff}", DateTime.UtcNow));

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                cts.CancelAfter(PRINT_TIMEOUT_MS);
                var result = await PrintChunksAsync(chunks, state, bridgeProgress, cts.Token);
                if (!result.Success)
                    throw new PrinterException(
                        string.Format("Test page failed at '{0}': {1}", result.FailedChunkLabel, result.ErrorMessage));
            }
            finally { cts.Dispose(); }
        }

        // =====================================================================
        //  RETRY CONFIGURATION
        // =====================================================================

        public int MaxChunkRetries { get; set; } = 3;
        public int RetryBaseDelayMs { get; set; } = 1_500;

        // =====================================================================
        //  BACKWARD-COMPATIBLE API  (no retryPolicy / progress params)
        // =====================================================================

        public async Task PrintReceiptAsync(
            ReceiptData receipt,
            string logoAssetName = "Logo.png",
            string watermarkText = "RICKBOLWA",
            CancellationToken cancellationToken = default)
        {
            var result = await PrintReceiptAsync(
                receipt,
                jobId: null,
                logoAssetName: logoAssetName,
                watermarkText: watermarkText,
                persistState: false,
                internalProgress: null,
                cancellationToken: cancellationToken);

            if (!result.Success)
                throw new PrinterException(
                    string.Format("Print failed at '{0}': {1} ({2}/{3} sections sent)",
                        result.FailedChunkLabel,
                        result.ErrorMessage,
                        result.ChunksSent,
                        result.TotalChunks));
        }

        // =====================================================================
        //  RESUMABLE CORE API
        // =====================================================================

        public async Task<PrintSessionResult> PrintReceiptAsync(
            ReceiptData receipt,
            string jobId = null,
            string logoAssetName = "Logo.png",
            string watermarkText = "RICKBOLWA",
            bool persistState = false,
            IProgress<PrintProgress> internalProgress = null,
            CancellationToken cancellationToken = default)
        {
            await RequireBluetoothPermissionsAsync();
            EnsureBluetoothReady();

            if (jobId == null)
                jobId = string.Format("receipt_{0:yyyyMMddHHmmssff}", DateTime.UtcNow);

            bool sameJob = _activeJobId == jobId
                           && _activeJobState != null
                           && _activeJobChunks != null;

            if (!sameJob)
            {
                Log(string.Format("Building chunks for job '{0}'.", jobId));
                _activeJobChunks = await Task.Run(
                    () => BuildReceiptChunks(receipt, logoAssetName, watermarkText), cancellationToken);
                _activeJobState = new PrintJobState(jobId, persistState);
                _activeJobId = jobId;
            }
            else
            {
                Log(string.Format("Resuming job '{0}' (confirmed {1}/{2} chunks).",
                    jobId, _activeJobState.CompletedCount, _activeJobChunks.Count));
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                cts.CancelAfter(PRINT_TIMEOUT_MS);
                var result = await PrintChunksAsync(_activeJobChunks, _activeJobState, internalProgress, cts.Token);
                if (result.Success) ClearActiveJob();
                return result;
            }
            finally { cts.Dispose(); }
        }

        // =====================================================================
        //  CORE CHUNK PRINTING ENGINE
        // =====================================================================

        private async Task<PrintSessionResult> PrintChunksAsync(
            IReadOnlyList<PrintChunk> chunks,
            PrintJobState state,
            IProgress<PrintProgress> progress,
            CancellationToken token)
        {
            var device = FindPrinterDevice(BluetoothAdapter.DefaultAdapter);
            int total = chunks.Count;
            BluetoothSocket currentSocket = null;

            // Emit session-started event
            ReportProgress(progress, "—", PrintChunkType.Other, 0, total, 0,
                           PrintProgressStatus.SessionStarted, "Session started.");

            try
            {
                foreach (var chunk in chunks)
                {
                    token.ThrowIfCancellationRequested();

                    if (state.IsCompleted(chunk))
                    {
                        Log(string.Format("Skipping completed: {0}", chunk));
                        continue;
                    }

                    bool chunkOk = false;
                    Exception lastError = null;
                    PrintChunkType chunkType = SectionToChunkType(chunk.Section);

                    for (int attempt = 1; attempt <= MaxChunkRetries; attempt++)
                    {
                        token.ThrowIfCancellationRequested();

                        // Report started / retrying
                        ReportProgress(progress, chunk.Label, chunkType,
                                       state.CompletedCount, total, attempt,
                                       attempt == 1 ? PrintProgressStatus.ChunkStarted
                                                    : PrintProgressStatus.ChunkRetrying,
                                       attempt == 1
                                           ? string.Format("Sending {0}…", chunk.Label)
                                           : string.Format("Retrying {0} (attempt {1}/{2})…", chunk.Label, attempt, MaxChunkRetries));

                        Log(string.Format("Sending {0} – attempt {1}/{2}", chunk, attempt, MaxChunkRetries));

                        try
                        {
                            if (currentSocket == null || !currentSocket.IsConnected)
                            {
                                SafeDispose(currentSocket);
                                currentSocket = null;
                                Log("Opening RFCOMM socket…");
                                currentSocket = await OpenSocketAsync(device, token);
                                Log("Socket ready.");
                            }

                            await SendChunkedAsync(currentSocket.OutputStream, chunk.Data, token);
                            await Task.Delay(FLUSH_SETTLE_MS, token);

                            state.MarkCompleted(chunk);
                            chunkOk = true;

                            // Report chunk completed
                            ReportProgress(progress, chunk.Label, chunkType,
                                           state.CompletedCount, total, attempt,
                                           PrintProgressStatus.ChunkCompleted,
                                           string.Format("{0} sent ({1}/{2}).", chunk.Label, state.CompletedCount, total));

                            Log(string.Format("Confirmed: {0}. Progress {1}/{2}.", chunk, state.CompletedCount, total));
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            ReportProgress(progress, chunk.Label, chunkType,
                                           state.CompletedCount, total, attempt,
                                           PrintProgressStatus.SessionCancelled, "Cancelled.");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            Log(string.Format("Attempt {0} failed for '{1}': {2}", attempt, chunk.Label, ex.Message));
                            SafeDispose(currentSocket);
                            currentSocket = null;

                            if (attempt < MaxChunkRetries)
                            {
                                int delay = RetryBaseDelayMs * (int)Math.Pow(2, attempt - 1);
                                Log(string.Format("Back-off {0} ms…", delay));
                                await Task.Delay(delay, token);
                            }
                        }
                    }

                    if (!chunkOk)
                    {
                        ReportProgress(progress, chunk.Label, chunkType,
                                       state.CompletedCount, total, MaxChunkRetries,
                                       PrintProgressStatus.ChunkFailed,
                                       string.Format("{0} failed after {1} attempts.", chunk.Label, MaxChunkRetries));

                        Log(string.Format("Chunk '{0}' exhausted all {1} retries.", chunk.Label, MaxChunkRetries));
                        return PrintSessionResult.Fail(
                            chunk.Label,
                            lastError?.Message ?? "Unknown error",
                            state.CompletedCount,
                            total);
                    }
                }

                ReportProgress(progress, "—", PrintChunkType.Other, total, total, 0,
                               PrintProgressStatus.SessionCompleted,
                               string.Format("All {0} chunks printed.", total));

                Log(string.Format("All {0} chunks printed successfully.", total));
                return PrintSessionResult.Ok(state.CompletedCount, total);
            }
            finally
            {
                SafeDispose(currentSocket);
            }
        }

        // =====================================================================
        //  PROGRESS HELPER
        // =====================================================================

        private static void ReportProgress(
            IProgress<PrintProgress> progress,
            string chunkName,
            PrintChunkType chunkType,
            int chunkIndex,
            int total,
            int attempt,
            PrintProgressStatus status,
            string message)
        {
            progress?.Report(new PrintProgress
            {
                ChunkName = chunkName,
                ChunkType = chunkType,
                ChunkIndex = chunkIndex,
                TotalChunks = total,
                AttemptNumber = attempt,
                Status = status,
                Message = message
            });
        }

        /// <summary>
        /// Builds a bridge <see cref="IProgress{PrintProgress}"/> that maps the
        /// internal chunk model to the public <see cref="PrintProgress"/> shape
        /// expected by <see cref="IPrinterService"/> callers.
        /// </summary>
        private static IProgress<PrintProgress> BuildBridgeProgress(
            IProgress<PrintProgress> outer, string sessionId)
        {
            if (outer == null) return null;
            return new Progress<PrintProgress>(p =>
            {
                p.SessionId = sessionId;
                outer.Report(p);
            });
        }

        private static PrintChunkType SectionToChunkType(PrintSection section)
        {
            switch (section)
            {
                case PrintSection.Init:
                case PrintSection.Logo:
                case PrintSection.Header:
                    return PrintChunkType.Header;

                case PrintSection.Body:
                case PrintSection.Totals:
                    return PrintChunkType.Body;

                default:
                    return PrintChunkType.Footer;
            }
        }

        // =====================================================================
        //  CHUNK BUILDERS
        // =====================================================================

        private List<PrintChunk> BuildReceiptChunks(
            ReceiptData receipt, string logoAssetName, string watermarkText)
        {
            var chunks = new List<PrintChunk>();

            // ── Init ─────────────────────────────────────────────────────────
            // Only reset the printer. No divider, no blank line, no alignment.
            // The logo chunk (or header if no logo) starts immediately after.
            chunks.Add(Chunk(PrintSection.Init, "Init", ms =>
            {
                ms.Write(CMD_INIT);
            }));

            // ── Logo ──────────────────────────────────────────────────────────
            // No CMD_LF before or after the image — the raster command advances
            // paper by exactly the image height. CMD_ALIGN_CENTER is set inside
            // TryBuildLogoCommand so the image is centred, but no blank lines.
            if (!string.IsNullOrWhiteSpace(logoAssetName))
            {
                var logoCmd = TryBuildLogoCommand(logoAssetName, maxWidth: 180);
                if (logoCmd != null)
                    chunks.Add(new PrintChunk(PrintSection.Logo, "Logo", logoCmd));
            }

            // ── Header + body + totals ────────────────────────────────────────
            // Preferred: ONE composited raster with the watermark drawn underneath
            // the text (ported from the YIRS Hospital SDK). If rendering fails for
            // any reason we fall back to the original plain-text chunks so a
            // receipt still prints.
            byte[] bodyRaster = null;
            try { bodyRaster = BuildBodyRaster(receipt, watermarkText); }
            catch (Exception ex)
            {
                Log(string.Format("Raster body failed, using text fallback – {0}", ex.Message));
            }

            if (bodyRaster != null)
                chunks.Add(new PrintChunk(PrintSection.Body, "Body",
                    ConcatBytes(CHUNK_PREAMBLE, bodyRaster)));
            else
                AddTextBodyChunks(chunks, receipt);

            // ── QR code (raster – works on printers that ignore GS ( k) ──────
            if (!string.IsNullOrWhiteSpace(receipt.BarcodeLabel))
            {
                byte[] qrCmd = null;
                try { qrCmd = BuildQrRasterCommand(receipt.BarcodeLabel.Trim()); }
                catch (Exception ex)
                {
                    Log(string.Format("QR raster failed, using native QR – {0}", ex.Message));
                }

                if (qrCmd != null)
                    chunks.Add(new PrintChunk(PrintSection.Footer, "Qr",
                        ConcatBytes(CHUNK_PREAMBLE, qrCmd)));
                else
                    chunks.Add(Chunk(PrintSection.Footer, "Qr", ms =>
                    {
                        ms.Write(CHUNK_PREAMBLE);
                        ms.Write(CMD_ALIGN_CENTER);
                        ms.Write(BuildQRCodeCommand(receipt.BarcodeLabel));
                        ms.Write(CMD_ALIGN_LEFT);
                    }));
            }

            // ── Footer ────────────────────────────────────────────────────────
            chunks.Add(Chunk(PrintSection.Footer, "Footer", ms =>
            {
                ms.Write(CHUNK_PREAMBLE);
                ms.Write(CMD_ALIGN_CENTER);
                ms.Write(CMD_BOLD_ON);
                ms.WriteText((receipt.FooterLine2 ?? "POWERED BY OSOFTPAY") + "\n");
                ms.Write(CMD_BOLD_OFF);
                ms.WriteText(Divider('=', _charsPerLine) + "\n");
                ms.Write(CMD_ALIGN_LEFT);
            }));

            // ── Feed & Cut ────────────────────────────────────────────────────
            chunks.Add(Chunk(PrintSection.FeedAndCut, "FeedAndCut", ms =>
            {
                ms.Write(CMD_FEED_CUT);
            }));

            return chunks;
        }

        /// <summary>
        /// Original plain-text header / body / totals chunks. Used only when the
        /// raster body cannot be rendered.
        /// </summary>
        private void AddTextBodyChunks(List<PrintChunk> chunks, ReceiptData receipt)
        {
            // ── Header ────────────────────────────────────────────────────────
            chunks.Add(Chunk(PrintSection.Header, "Header", ms =>
            {
                ms.Write(CHUNK_PREAMBLE);
                ms.Write(CMD_ALIGN_CENTER);
                ms.Write(CMD_BOLD_ON);
                ms.WriteText(receipt.StoreName + "\n");
                ms.Write(CMD_BOLD_OFF);
                if (!string.IsNullOrWhiteSpace(receipt.StorePhone))
                    ms.WriteText(receipt.StorePhone + "\n");
                ms.WriteText(Divider('=', _charsPerLine) + "\n");

                // "OFFICIAL RECEIPT" title — double-width, centred
                ms.Write(CMD_ALIGN_CENTER);
                ms.Write(CMD_BOLD_ON);
                ms.Write(CMD_DWIDTH_ON);
                ms.WriteText("OFFICIAL RECEIPT\n");
                ms.Write(CMD_DWIDTH_OFF);
                ms.Write(CMD_BOLD_OFF);

                // Data rows — left-aligned, no blank line above them
                ms.Write(CMD_ALIGN_LEFT);
                ms.WriteText(Divider('=', _charsPerLine) + "\n");
                ms.WriteText(Col("Date", receipt.PrintDate.ToString("dd/MM/yyyy HH:mm:ss"), _charsPerLine) + "\n");
                ms.WriteText(Col("Ref", receipt.ReceiptNumber, _charsPerLine) + "\n");
                ms.WriteText(Col("Agent", receipt.AgentName, _charsPerLine) + "\n");
                ms.WriteText(Col("Point", receipt.CollectionPoint, _charsPerLine) + "\n");
                ms.WriteText(Divider('-', _charsPerLine) + "\n");
            }));

            // ── Body lines ────────────────────────────────────────────────────
            for (int i = 0; i < receipt.Items.Count; i++)
            {
                var item = receipt.Items[i];

                chunks.Add(Chunk(PrintSection.Body, string.Format("Body_Line{0}", i), ms =>
                {
                    ms.Write(CHUNK_PREAMBLE);
                    if (item.Amount == 0m && !string.IsNullOrWhiteSpace(item.SubText))
                    {
                        ms.Write(CMD_ALIGN_CENTER);
                        ms.WriteText(item.Description + ": " + item.SubText + "\n");
                        ms.Write(CMD_ALIGN_LEFT);
                    }
                    else
                    {
                        ms.WriteText(ColTwoRight(
                            item.Description,
                            "N" + item.Amount.ToString("###,###.00"),
                            _charsPerLine) + "\n");

                        if (!string.IsNullOrWhiteSpace(item.SubText))
                        {
                            ms.Write(CMD_FONT_SMALL);
                            ms.WriteText("  " + item.SubText + "\n");
                            ms.Write(CMD_FONT_NORMAL);
                        }
                    }
                }));
            }

            // ── Totals ────────────────────────────────────────────────────────
            chunks.Add(Chunk(PrintSection.Totals, "Totals", ms =>
            {
                ms.Write(CHUNK_PREAMBLE);
                ms.WriteText(Divider('-', _charsPerLine) + "\n");
                ms.Write(CMD_BOLD_ON);
                if (receipt.AmountPaid > 0m)
                    ms.WriteText(ColTwoRight(
                        "AMOUNT PAID",
                        "N" + receipt.AmountPaid.ToString("###,###.00"),
                        _charsPerLine) + "\n");
                ms.Write(CMD_BOLD_OFF);
                ms.WriteText(Divider('=', _charsPerLine) + "\n");
            }));

        }

        // =====================================================================
        //  RASTER BODY  –  header → items → totals drawn as ONE bitmap with the
        //  watermark painted first, so it sits physically underneath the text.
        //  (Ported from the YIRS Hospital SDK.)
        // =====================================================================

        private sealed class BodyStyle
        {
            public int Width;
            public Paint Normal;
            public Paint Bold;
            public Paint Small;
            public Paint Banner;
            public float Line;
            public float SmallLine;
            public float BannerLine;
            public int SmallChars;
        }

        private byte[] BuildBodyRaster(ReceiptData receipt, string watermarkText)
        {
            int width = (_printerDots / 8) * 8;

            var s = new BodyStyle { Width = width };

            s.Normal = new Paint(PaintFlags.AntiAlias);
            s.Normal.SetTypeface(Typeface.Monospace);
            CalibrateMonospace(s.Normal, _charsPerLine, width);

            s.Bold = new Paint(PaintFlags.AntiAlias);
            s.Bold.SetTypeface(Typeface.Create(Typeface.Monospace, TypefaceStyle.Bold));
            s.Bold.TextSize = s.Normal.TextSize;

            s.Small = new Paint(PaintFlags.AntiAlias);
            s.Small.SetTypeface(Typeface.Monospace);
            s.Small.TextSize = s.Normal.TextSize * 0.85f;

            s.Banner = new Paint(PaintFlags.AntiAlias);
            s.Banner.SetTypeface(Typeface.Create(Typeface.Monospace, TypefaceStyle.Bold));
            s.Banner.TextSize = s.Normal.TextSize * BANNER_SIZE_RATIO;
            string bannerText = receipt.ReceiptBannerText ?? "OFFICIAL RECEIPT";
            float bannerMaxWidth = width - 8f;
            while (s.Banner.TextSize > s.Normal.TextSize &&
                   s.Banner.MeasureText(bannerText) > bannerMaxWidth)
                s.Banner.TextSize -= 1f;

            s.Line = MonoLineHeight(s.Normal);
            s.SmallLine = MonoLineHeight(s.Small);
            s.BannerLine = MonoLineHeight(s.Banner);
            s.SmallChars = Math.Max(10, (int)((width - 8) / s.Small.MeasureText("0")));

            try
            {
                // Pass 1 – measure only (canvas = null) to get the total height.
                int height = LayoutBody(null, receipt, s);

                Bitmap bmp = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888);
                try
                {
                    Canvas canvas = new Canvas(bmp);
                    canvas.DrawColor(Android.Graphics.Color.White);

                    // Watermark first → everything drawn afterwards sits on top of it.
                    DrawWatermarkBackground(canvas, width, height, watermarkText);

                    // Pass 2 – draw the real content over the watermark.
                    LayoutBody(canvas, receipt, s);

                    byte[] raster = ConvertToMonochromeFloydSteinberg(bmp, width, height);
                    return WrapRasterInBands(raster, width, height);
                }
                finally { bmp.Recycle(); }
            }
            finally
            {
                s.Normal.Dispose();
                s.Bold.Dispose();
                s.Small.Dispose();
                s.Banner.Dispose();
            }
        }

        /// <summary>
        /// Lays out the receipt body top to bottom. With a null canvas it only
        /// measures and returns the height; with a canvas it draws the same
        /// content, so the measured and drawn heights can never drift apart.
        /// </summary>
        private int LayoutBody(Canvas canvas, ReceiptData receipt, BodyStyle s)
        {
            int w = s.Width;
            int y = 4;

            // Store name + contact line (FakeBold survives 1-bit dithering better).
            s.Bold.FakeBoldText = true;
            y = DrawCenteredWrapped(canvas, receipt.StoreName, s.Bold, w, y, s.Line, _charsPerLine);
            if (!string.IsNullOrWhiteSpace(receipt.StorePhone))
                y = DrawCenteredWrapped(canvas, receipt.StorePhone, s.Bold, w, y, s.Line, _charsPerLine);
            s.Bold.FakeBoldText = false;
            y = DrawCentered(canvas, Divider('=', _charsPerLine), s.Normal, w, y, s.Line);

            // Banner
            y = DrawCentered(canvas, receipt.ReceiptBannerText ?? "OFFICIAL RECEIPT", s.Banner, w, y, s.BannerLine);
            y = DrawCentered(canvas, Divider('=', _charsPerLine), s.Normal, w, y, s.Line);

            // Metadata
            y = DrawLeft(canvas, Col("Date", receipt.PrintDate.ToString("dd/MM/yyyy HH:mm:ss"), _charsPerLine), s.Normal, y, s.Line);
            y = DrawLeft(canvas, Col("Ref", receipt.ReceiptNumber, _charsPerLine), s.Normal, y, s.Line);
            y = DrawLeft(canvas, Col("Agent", receipt.AgentName, _charsPerLine), s.Normal, y, s.Line);
            y = DrawLeft(canvas, Col("Point", receipt.CollectionPoint, _charsPerLine), s.Normal, y, s.Line);
            y = DrawCentered(canvas, Divider('-', _charsPerLine), s.Normal, w, y, s.Line);

            // Items
            if (receipt.Items != null)
            {
                foreach (var item in receipt.Items)
                {
                    if (item == null) continue;

                    if (item.Amount == 0m && !string.IsNullOrWhiteSpace(item.SubText))
                    {
                        // Info-only row: "Label: value", centred.
                        y = DrawCenteredWrapped(canvas, item.Description + ": " + item.SubText,
                            s.Normal, w, y, s.Line, _charsPerLine);
                    }
                    else
                    {
                        y = DrawLeft(canvas, ColTwoRight(
                            item.Description ?? string.Empty,
                            "N" + item.Amount.ToString("###,###.00"),
                            _charsPerLine), s.Normal, y, s.Line);

                        if (!string.IsNullOrWhiteSpace(item.SubText))
                        {
                            foreach (var line in Wrap(item.SubText, s.SmallChars - 2))
                                y = DrawLeft(canvas, "  " + line, s.Small, y, s.SmallLine);
                        }
                    }
                }
            }

            y = DrawCentered(canvas, Divider('-', _charsPerLine), s.Normal, w, y, s.Line);

            // Totals
            if (receipt.AmountPaid > 0m)
                y = DrawLeft(canvas, ColTwoRight("AMOUNT PAID",
                    "N" + receipt.AmountPaid.ToString("###,###.00"), _charsPerLine), s.Bold, y, s.Line);

            y = DrawCentered(canvas, Divider('=', _charsPerLine), s.Normal, w, y, s.Line);

            y += BODY_PADDING / 4;
            return y;
        }

        private static int DrawCentered(Canvas canvas, string text, Paint paint, int width, int y, float lineHeight)
        {
            text = text ?? string.Empty;
            if (canvas != null)
            {
                float tw = paint.MeasureText(text);
                canvas.DrawText(text, (width - tw) / 2f, y + lineHeight * 0.8f, paint);
            }
            return y + (int)Math.Ceiling(lineHeight);
        }

        private static int DrawCenteredWrapped(
            Canvas canvas, string text, Paint paint, int width, int y, float lineHeight, int maxChars)
        {
            foreach (var line in Wrap(text, maxChars))
                y = DrawCentered(canvas, line, paint, width, y, lineHeight);
            return y;
        }

        private static int DrawLeft(Canvas canvas, string text, Paint paint, int y, float lineHeight)
        {
            if (canvas != null)
                canvas.DrawText(text ?? string.Empty, 4f, y + lineHeight * 0.8f, paint);
            return y + (int)Math.Ceiling(lineHeight);
        }

        /// <summary>Word-wraps <paramref name="text"/> to at most <paramref name="maxChars"/> per line.</summary>
        private static List<string> Wrap(string text, int maxChars)
        {
            var lines = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return lines;
            maxChars = Math.Max(1, maxChars);

            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.Length > maxChars)
            {
                int cut = text.LastIndexOf(' ', maxChars);
                if (cut <= 0) cut = maxChars;
                lines.Add(text.Substring(0, cut).TrimEnd());
                text = text.Substring(cut).TrimStart();
            }
            if (text.Length > 0) lines.Add(text);
            return lines;
        }

        /// <summary>Sizes a monospace Paint so <paramref name="charsPerLine"/> characters fill the paper width.</summary>
        private static void CalibrateMonospace(Paint paint, int charsPerLine, int widthDots)
        {
            paint.TextSize = 24f;
            float measured = paint.MeasureText(new string('0', charsPerLine));
            paint.TextSize = 24f * (widthDots * 0.96f) / measured;
        }

        private static float MonoLineHeight(Paint paint)
        {
            var fm = paint.GetFontMetrics();
            return (fm.Descent - fm.Ascent) * 1.2f;
        }

        // =====================================================================
        //  WATERMARK  –  large faint text, rotated -90°, drawn behind the body
        // =====================================================================

        private static void DrawWatermarkBackground(Canvas canvas, int width, int height, string text)
        {
            if (canvas == null || string.IsNullOrWhiteSpace(text)) return;

            Paint textPaint = null;
            try
            {
                canvas.Save();

                // Centre of the receipt, then rotate so the text runs along the slip.
                canvas.Translate(width / 2f, height / 2f);
                canvas.Rotate(-90);

                textPaint = new Paint { AntiAlias = true };
                // After rotation the text runs along the receipt's full length.
                float textSize = FitWatermarkTextSize(textPaint, text, height);
                textPaint.TextSize = textSize;
                textPaint.SetARGB(255, WATERMARK_GRAY, WATERMARK_GRAY, WATERMARK_GRAY);

                float tw = textPaint.MeasureText(text);
                canvas.DrawText(text, -tw / 2f, textSize * 0.35f, textPaint);

                canvas.Restore();
            }
            catch (Exception ex)
            {
                Log(string.Format("Watermark skipped – {0}", ex.Message));
            }
            finally { textPaint?.Dispose(); }
        }

        /// <summary>Shrinks the watermark until it fits on one line within ~88% of <paramref name="maxWidthDots"/>.</summary>
        private static float FitWatermarkTextSize(Paint paint, string text, int maxWidthDots)
        {
            float size = WATERMARK_TEXT_MAX_SIZE;
            float targetWidth = maxWidthDots * 0.88f;

            paint.TextSize = size;
            while (size > WATERMARK_TEXT_MIN_SIZE && paint.MeasureText(text) > targetWidth)
            {
                size -= 2f;
                paint.TextSize = size;
            }
            return size;
        }

        // =====================================================================
        //  RASTER HELPERS
        // =====================================================================

        /// <summary>
        /// Floyd-Steinberg dithering – renders the faint watermark grey as a
        /// halftone pattern instead of dropping it. Bit set = black dot.
        /// </summary>
        private static byte[] ConvertToMonochromeFloydSteinberg(Bitmap bmp, int w, int h)
        {
            int bpr = w / 8;
            byte[] result = new byte[bpr * h];
            var pixels = new int[w * h];
            bmp.GetPixels(pixels, 0, w, 0, 0, w, h);

            float[] gray = new float[w * h];
            for (int i = 0; i < w * h; i++)
            {
                int px = pixels[i];
                int a = (px >> 24) & 0xFF;
                int r = (px >> 16) & 0xFF;
                int g = (px >> 8) & 0xFF;
                int bl = px & 0xFF;
                gray[i] = a < 128 ? 255f : (0.299f * r + 0.587f * g + 0.114f * bl);
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float old = gray[y * w + x];
                    float neo = old < 128f ? 0f : 255f;
                    gray[y * w + x] = neo;
                    float err = old - neo;

                    if (x + 1 < w) gray[y * w + x + 1] += err * 7f / 16f;
                    if (x > 0 && y + 1 < h) gray[(y + 1) * w + x - 1] += err * 3f / 16f;
                    if (y + 1 < h) gray[(y + 1) * w + x] += err * 5f / 16f;
                    if (x + 1 < w && y + 1 < h) gray[(y + 1) * w + x + 1] += err * 1f / 16f;

                    if (neo < 128f)
                        result[y * bpr + x / 8] |= (byte)(1 << (7 - x % 8));
                }
            }
            return result;
        }

        /// <summary>
        /// Splits one tall raster into GS v 0 bands sent back to back. Several
        /// Bluetooth thermal printers reject or overflow on one very tall block.
        /// </summary>
        private static byte[] WrapRasterInBands(byte[] raster, int width, int height)
        {
            int widthBytes = width / 8;
            var ms = new MemoryStream(raster.Length + 64);
            try
            {
                int y = 0;
                while (y < height)
                {
                    int bandH = Math.Min(RASTER_BAND_HEIGHT, height - y);
                    ms.Write(new byte[]
                    {
                        0x1D, 0x76, 0x30, 0x00,
                        (byte)(widthBytes & 0xFF), (byte)((widthBytes >> 8) & 0xFF),
                        (byte)(bandH & 0xFF), (byte)((bandH >> 8) & 0xFF)
                    });
                    ms.Write(raster, y * widthBytes, bandH * widthBytes);
                    y += bandH;
                }
                return ms.ToArray();
            }
            finally { ms.Dispose(); }
        }

        // =====================================================================
        //  QR CODE  –  raster (GS v 0), centred, no dependency on GS ( k
        // =====================================================================

        private byte[] BuildQrRasterCommand(string data)
        {
            bool[,] qr = QrEncoder.Encode(data);
            int modules = qr.GetLength(0);
            const int QUIET = 4;                           // quiet zone, in modules
            int cell = Math.Max(1, Math.Min(4, _printerDots / (modules + QUIET * 2)));
            int qrDots = (modules + QUIET * 2) * cell;

            int widthBytes = (_printerDots + 7) / 8;
            int rowDots = widthBytes * 8;
            int leftPad = Math.Max(0, (rowDots - qrDots) / 2);

            // Every row is padded to the full paper width, so centring never
            // depends on GS L / alignment support.
            var raster = new byte[widthBytes * qrDots];
            for (int y = 0; y < qrDots; y++)
            {
                int my = y / cell - QUIET;
                if (my < 0 || my >= modules) continue;
                int rowBase = y * widthBytes;
                for (int x = 0; x < qrDots; x++)
                {
                    int mx = x / cell - QUIET;
                    if (mx < 0 || mx >= modules || !qr[mx, my]) continue;
                    int px = leftPad + x;
                    if (px >= rowDots) continue;           // never overrun the row
                    raster[rowBase + px / 8] |= (byte)(0x80 >> (px % 8));
                }
            }

            var ms = new MemoryStream();
            try
            {
                const int BAND = 64;
                for (int y0 = 0; y0 < qrDots; y0 += BAND)
                {
                    int h = Math.Min(BAND, qrDots - y0);
                    ms.Write(new byte[]
                    {
                        0x1D, 0x76, 0x30, 0x00,
                        (byte)(widthBytes & 0xFF), (byte)(widthBytes >> 8),
                        (byte)(h & 0xFF), (byte)(h >> 8)
                    });
                    ms.Write(raster, y0 * widthBytes, h * widthBytes);
                }
                ms.Write(CMD_LF);
                return ms.ToArray();
            }
            finally { ms.Dispose(); }
        }

        // ── Test page ─────────────────────────────────────────────────────────

        private List<PrintChunk> BuildTestChunks()
        {
            var chunks = new List<PrintChunk>();

            chunks.Add(Chunk(PrintSection.Init, "Init", ms =>
            {
                ms.Write(CMD_INIT);
            }));

            chunks.Add(Chunk(PrintSection.Header, "Header", ms =>
            {
                ms.Write(CHUNK_PREAMBLE);
                ms.Write(CMD_ALIGN_CENTER);
                ms.WriteText("OSOFT INTEGRATED RESOURCES LTD\n");
                ms.WriteText(Divider('=', _charsPerLine) + "\n");
                ms.Write(CMD_BOLD_ON);
                ms.WriteText("PRINTER STATUS: ONLINE\n");
                ms.Write(CMD_BOLD_OFF);
                ms.WriteText(Divider('-', _charsPerLine) + "\n");
            }));

            chunks.Add(Chunk(PrintSection.FeedAndCut, "FeedAndCut", ms =>
            {
                ms.Write(CMD_FEED_CUT);
            }));

            return chunks;
        }

        // =====================================================================
        //  QR CODE COMMAND
        // =====================================================================

        private static byte[] BuildQRCodeCommand(
            string data, byte cellSize = 3, byte errorLevel = 77)
        {
            byte[] dataBytes = Encoding.UTF8.GetBytes(data);
            int storeLen = dataBytes.Length + 3;
            byte pL = (byte)(storeLen & 0xFF);
            byte pH = (byte)((storeLen >> 8) & 0xFF);

            var ms = new MemoryStream();
            try
            {
                ms.Write(new byte[] { 0x1D, 0x28, 0x6B, 4, 0, 49, 65, 50, 0 });
                ms.Write(new byte[] { 0x1D, 0x28, 0x6B, 3, 0, 49, 67, cellSize });
                ms.Write(new byte[] { 0x1D, 0x28, 0x6B, 3, 0, 49, 69, errorLevel });
                ms.Write(new byte[] { 0x1D, 0x28, 0x6B, pL, pH, 49, 80, 48 });
                ms.Write(dataBytes);
                ms.Write(new byte[] { 0x1D, 0x28, 0x6B, 3, 0, 49, 81, 48 });
                ms.Write(new byte[] { 0x0A });
                return ms.ToArray();
            }
            finally { ms.Dispose(); }
        }

        // =====================================================================
        //  LOGO  –  raster image builder
        // =====================================================================

        private byte[] TryBuildLogoCommand(string assetName, int maxWidth = 180)
        {
            try
            {
                var context = Android.App.Application.Context;
                Bitmap src;
                using (var stream = context.Assets.Open(assetName))
                    src = BitmapFactory.DecodeStream(stream);

                if (src == null) { Log(string.Format("Asset '{0}' could not be decoded.", assetName)); return null; }

                var scaled = ScaleToFitPaper(src, maxWidth);
                src.Recycle();

                int widthDots = scaled.Width;
                int heightDots = scaled.Height;
                int widthBytes = widthDots / 8;
                byte[] raster = ConvertToMonochrome(scaled, widthDots, heightDots);
                scaled.Recycle();

                // Centre the logo by padding every row to the FULL paper width with
                // the logo in the middle. Many printers ignore ESC a (alignment) for
                // raster images, which left the logo stuck to the left edge. A
                // full-width raster centres identically on every printer (same
                // technique as the body and QR code).
                int rowBytes = (_printerDots + 7) / 8;
                int copyBytes = Math.Min(widthBytes, rowBytes);
                int leftPadBytes = Math.Max(0, (rowBytes - copyBytes) / 2);

                var padded = new byte[rowBytes * heightDots];
                for (int y = 0; y < heightDots; y++)
                    Buffer.BlockCopy(raster, y * widthBytes, padded, y * rowBytes + leftPadBytes, copyBytes);

                var ms = new MemoryStream();
                try
                {
                    ms.Write(CMD_ALIGN_LEFT);
                    ms.Write(CMD_LF);
                    ms.Write(WrapRasterInBands(padded, rowBytes * 8, heightDots));
                    ms.Write(CMD_LF);
                    return ms.ToArray();
                }
                finally { ms.Dispose(); }
            }
            catch (Exception ex) { Log(string.Format("Logo skipped – {0}", ex.Message)); return null; }
        }

        private Bitmap ScaleToFitPaper(Bitmap source, int maxWidth = 384)
        {
            int srcW = source.Width;
            int srcH = source.Height;
            int targetW = (Math.Min(srcW, maxWidth) / 8) * 8;
            if (targetW == srcW && srcW % 8 == 0) return source;
            float scale = (float)targetW / srcW;
            int targetH = Math.Max(1, (int)(srcH * scale));
            return Bitmap.CreateScaledBitmap(source, targetW, targetH, true);
        }

        private static byte[] ConvertToMonochrome(Bitmap bmp, int w, int h)
        {
            int bytesPerRow = w / 8;
            byte[] result = new byte[bytesPerRow * h];
            var pixels = new int[w * h];
            bmp.GetPixels(pixels, 0, w, 0, 0, w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int pixel = pixels[y * w + x];
                    if (((pixel >> 24) & 0xFF) < 128) continue;
                    float luma = 0.299f * ((pixel >> 16) & 0xFF)
                               + 0.587f * ((pixel >> 8) & 0xFF)
                               + 0.114f * (pixel & 0xFF);
                    if (luma < MONO_THRESHOLD)
                        result[y * bytesPerRow + x / 8] |= (byte)(1 << (7 - x % 8));
                }
            return result;
        }

        // =====================================================================
        //  BLUETOOTH – SOCKET MANAGEMENT
        // =====================================================================

        /// <summary>
        /// Three-strategy connection (ported from the YIRS Hospital SDK) so both
        /// built-in POS printers (which may lack an SDP record) and standard
        /// external printers connect.
        ///   A – SPP UUID            : most external printers
        ///   B – channel-1 RFCOMM    : built-in / no-SDP devices
        ///   C – insecure SPP UUID   : older POS firmware
        /// </summary>
        private async Task<BluetoothSocket> OpenSocketAsync(
            BluetoothDevice device, CancellationToken token, int timeoutMs = CONNECT_TIMEOUT_MS)
        {
            // Discovery slows or blocks RFCOMM connects; make sure it is off.
            try { BluetoothAdapter.DefaultAdapter?.CancelDiscovery(); } catch { }

            string errA = null, errB = null;

            // ── A: standard SPP UUID ──────────────────────────────────────
            BluetoothSocket socket = null;
            try
            {
                socket = device.CreateRfcommSocketToServiceRecord(UUID.FromString(SPP_UUID));
                await ConnectWithTimeoutAsync(socket, timeoutMs, token);
                Log("Connected via SPP UUID.");
                return socket;
            }
            catch (OperationCanceledException) { SafeDispose(socket); throw; }
            catch (Exception ex)
            {
                errA = ex.Message;
                Log(string.Format("SPP UUID failed ({0}); trying channel-1.", ex.Message));
                SafeDispose(socket);
            }

            // ── B: channel-1 RFCOMM (built-in printer / no SDP) ──────────
            BluetoothSocket ch1Socket = null;
            try
            {
                var method = device.Class.GetMethod("createRfcommSocket", Java.Lang.Integer.Type);
                ch1Socket = (BluetoothSocket)method.Invoke(device, 1);
                await ConnectWithTimeoutAsync(ch1Socket, timeoutMs, token);
                Log("Connected via channel-1 RFCOMM.");
                return ch1Socket;
            }
            catch (OperationCanceledException) { SafeDispose(ch1Socket); throw; }
            catch (Exception ex)
            {
                errB = ex.Message;
                Log(string.Format("Channel-1 failed ({0}); trying insecure UUID.", ex.Message));
                SafeDispose(ch1Socket);
            }

            // ── C: insecure RFCOMM (some older POS firmware) ─────────────
            BluetoothSocket insecureSocket = null;
            try
            {
                insecureSocket = device.CreateInsecureRfcommSocketToServiceRecord(
                    UUID.FromString(SPP_UUID));
                await ConnectWithTimeoutAsync(insecureSocket, timeoutMs, token);
                Log("Connected via insecure SPP UUID.");
                return insecureSocket;
            }
            catch (OperationCanceledException) { SafeDispose(insecureSocket); throw; }
            catch (Exception ex)
            {
                SafeDispose(insecureSocket);
                throw new PrinterException(
                    string.Format(
                        "Could not connect to printer after 3 attempts.\n" +
                        "  SPP:      {0}\n  Channel1: {1}\n  Insecure: {2}\n\n" +
                        "• Built-in printer: confirm the Bluetooth Printer toggle is ON in the POS Setup app.\n" +
                        "• External printer: confirm it is switched on and in range.",
                        errA, errB, ex.Message));
            }
        }

        private static async Task ConnectWithTimeoutAsync(
            BluetoothSocket socket, int timeoutMs, CancellationToken token)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                cts.CancelAfter(timeoutMs);
                var connectTask = socket.ConnectAsync();
                var completed = await Task.WhenAny(
                    connectTask, Task.Delay(System.Threading.Timeout.Infinite, cts.Token));
                if (completed != connectTask)
                    throw new PrinterException("Bluetooth connection timed out.");
                await connectTask;
            }
            catch (OperationCanceledException)
            {
                throw new PrinterException("Bluetooth connection timed out.");
            }
            finally { cts.Dispose(); }
        }

        private static async Task SendChunkedAsync(
            Stream output, byte[] data, CancellationToken token)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(CHUNK_SIZE, data.Length - offset);
                await output.WriteAsync(data, offset, count, token);
                await output.FlushAsync(token);
                offset += count;
                if (INTER_CHUNK_DELAY > 0 && offset < data.Length)
                    await Task.Delay(INTER_CHUNK_DELAY, token);
            }
        }

        // =====================================================================
        //  DEVICE DISCOVERY + PERMISSIONS
        // =====================================================================

        /// <summary>
        /// Priority: (1) built-in POS printer by fixed MAC, (2) external printer
        /// by well-known device name. Shared with <see cref="PrinterDiagnosticsService"/>.
        /// </summary>
        internal static BluetoothDevice FindPrinterDevice(BluetoothAdapter adapter)
        {
            var bonded = adapter?.BondedDevices;
            if (bonded == null) return null;

            foreach (var d in bonded)
            {
                if (string.Equals(d.Address, BUILTIN_PRINTER_MAC, StringComparison.OrdinalIgnoreCase))
                {
                    Log(string.Format("Built-in POS printer found → {0}", d.Address));
                    return d;
                }
            }

            foreach (var d in bonded)
            {
                if (SupportedPrinters.Contains(d.Name ?? string.Empty))
                {
                    Log(string.Format("External printer found → {0} ({1})", d.Name, d.Address));
                    return d;
                }
            }

            return null;
        }

        /// <summary>True when the bonded device is the built-in printer or a known printer name.</summary>
        internal static bool IsSupportedPrinter(BluetoothDevice device)
        {
            if (device == null) return false;
            return string.Equals(device.Address, BUILTIN_PRINTER_MAC, StringComparison.OrdinalIgnoreCase)
                   || SupportedPrinters.Contains(device.Name ?? string.Empty);
        }

        private void EnsureBluetoothReady()
        {
            var adapter = BluetoothAdapter.DefaultAdapter
                ?? throw new PrinterException("Device has no Bluetooth adapter.");
            if (!adapter.IsEnabled)
                throw new PrinterException("Bluetooth is turned off. Enable it and retry.");
            if (FindPrinterDevice(adapter) == null)
                throw new PrinterException(
                    "No paired printer found.\n" +
                    "• Built-in POS printer: enable the Bluetooth Printer option in the POS Setup app " +
                    "so it appears in the Android paired list.\n" +
                    "• External printer: pair it in Android Settings → Bluetooth first.");
        }

        private static async Task RequireBluetoothPermissionsAsync()
        {
            bool granted = await BluetoothPermissionHelper.RequestAsync();
            if (!granted)
                throw new PrinterException(
                    "Bluetooth permission denied. " +
                    "On Android 12+ go to App Settings → Permissions → Nearby devices and allow.");
        }

        // =====================================================================
        //  LAYOUT HELPERS
        // =====================================================================

        private static PrintChunk Chunk(
            PrintSection section, string label, Action<MemoryStream> fill)
        {
            var ms = new MemoryStream(512);
            try { fill(ms); return new PrintChunk(section, label, ms.ToArray()); }
            finally { ms.Dispose(); }
        }

        private static string Col(string label, string value, int width)
        {
            string full = string.Format("{0,-8}: {1}", label, value);
            return full.Length <= width ? full : full.Substring(0, width);
        }

        private static string ColTwoRight(string left, string right, int width)
        {
            int rightWidth = right.Length;
            int leftWidth = Math.Max(1, width - rightWidth);
            if (left.Length > leftWidth - 1)
            {
                int trimLength = Math.Max(0, leftWidth - 3);
                left = left.Substring(0, trimLength) + "..";
            }
            return left.PadRight(leftWidth) + right;
        }

        private static string Divider(char ch, int len) => new string(ch, len);

        private static byte[] ConcatBytes(params byte[][] arrays)
        {
            var ms = new MemoryStream();
            foreach (var a in arrays) ms.Write(a, 0, a.Length);
            return ms.ToArray();
        }

        private void ClearActiveJob()
        {
            _activeJobState?.Reset();
            _activeJobState = null;
            _activeJobChunks = null;
            _activeJobId = null;
        }

        private static void SafeDispose(IDisposable obj)
        {
            try { obj?.Dispose(); } catch { }
        }

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine(string.Format("[BluetoothPrinterService] {0}", msg));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ClearActiveJob();
            GC.SuppressFinalize(this);
        }

        public async Task<bool> PrintRawBytesAsync(byte[] data)
        {
            // Pseudocode:
            // 1. Reject null or empty data.
            // 2. Request Bluetooth permissions and validate the adapter/printer.
            // 3. Open a Bluetooth socket with the existing connection fallback logic.
            // 4. Send the original bytes without converting them to text.
            // 5. Dispose the socket and return whether printing succeeded.

            if (data == null || data.Length == 0)
                return false;

            BluetoothSocket socket = null;
            CancellationTokenSource cts = null;

            try
            {
                await RequireBluetoothPermissionsAsync();
                EnsureBluetoothReady();

                var device = FindPrinterDevice(BluetoothAdapter.DefaultAdapter);
                if (device == null)
                    return false;

                cts = new CancellationTokenSource();
                cts.CancelAfter(PRINT_TIMEOUT_MS);

                socket = await OpenSocketAsync(device, cts.Token);
                await SendChunkedAsync(socket.OutputStream, data, cts.Token);
                await Task.Delay(FLUSH_SETTLE_MS, cts.Token);

                return true;
            }
            catch (Exception ex)
            {
                Log(string.Format("Raw print failed: {0}", ex.Message));
                return false;
            }
            finally
            {
                if (cts != null)
                    cts.Dispose();

                SafeDispose(socket);
            }
        }

    }






    // =========================================================================
    //  STREAM EXTENSIONS
    // =========================================================================

    internal static class StreamExtensions
    {
        private static readonly Encoding PrintEncoding = Encoding.UTF8;

        public static void Write(this MemoryStream ms, byte[] data)
            => ms.Write(data, 0, data.Length);

        public static void WriteText(this MemoryStream ms, string text)
        {
            byte[] bytes = PrintEncoding.GetBytes(text);
            ms.Write(bytes, 0, bytes.Length);
        }
    }

    // =========================================================================
    //  DATA MODELS
    // =========================================================================


    public sealed class ReceiptData
    {
        public string StoreName { get; set; } = "RICKBOLWA LIMITED SERVICE";
        public string StoreSubTitle { get; set; }
        public string StoreAddress { get; set; } = "Rickbolwa Limited Service";
        public string StorePhone { get; set; } = "Contact us: 09070701616,07017639494";
        public string ReceiptBannerText { get; set; } = "OFFICIAL RECEIPT";
        public string ReceiptNumber { get; set; } = "N/A";
        public string AgentName { get; set; }
        public string CollectionPoint { get; set; }
        public string Consultant { get; set; }
        public string SuperAgent { get; set; }
        public DateTime PrintDate { get; set; } = DateTime.Now;
        public List<ReceiptItem> Items { get; set; } = new List<ReceiptItem>();
        public decimal TotalAmount { get; set; }
        public decimal AmountPaid { get; set; }
        public decimal AmountLeft { get; set; }

        public string FooterLine1 { get; set; } = "Thank You!";
        public string FooterLine2 { get; set; } = "POWERED BY OSOFTPAY";

        /// <summary>
        /// Full verification URL encoded as a QR code on the receipt.
        /// Set to null or empty to skip the QR block entirely.
        /// </summary>
        public string BarcodeLabel { get; set; } =
            "https://rickbolwa.osoftpay.net/Singlecollections/Verify?TransactId=";

    }

    public sealed class ReceiptItem
    {
        public string Description { get; set; }
        public decimal Amount { get; set; }
        public string SubText { get; set; }
    }

    // ══════════════════════════════════════════════════════════════
    //  CUSTOM EXCEPTION
    // ══════════════════════════════════════════════════════════════

    public sealed class PrinterException : Exception
    {
        public PrinterException(string message) : base(message) { }
        public PrinterException(string message, Exception inner) : base(message, inner) { }
    }
}