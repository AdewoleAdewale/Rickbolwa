using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Java.Util;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rickbolwa.Droid.Services
{
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
                "Q2i"
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

                BluetoothSocket socket = null;
                try
                {
                    socket = device.CreateRfcommSocketToServiceRecord(UUID.FromString(SPP_UUID));
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    try
                    {
                        cts.CancelAfter(CONNECT_TIMEOUT_MS);
                        var connectTask = socket.ConnectAsync();
                        var completed = await Task.WhenAny(
                            connectTask, Task.Delay(System.Threading.Timeout.Infinite, cts.Token));
                        if (completed != connectTask) return false;
                        await connectTask;
                        return socket.IsConnected;
                    }
                    finally { cts.Dispose(); }
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
            string watermarkText = " RICKBOLWA",
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
                    () => BuildReceiptChunks(receipt, logoAssetName), cancellationToken);
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
                        catch (System.OperationCanceledException)
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

        private List<PrintChunk> BuildReceiptChunks(ReceiptData receipt, string logoAssetName)
        {
            var chunks = new List<PrintChunk>();

            chunks.Add(Chunk(PrintSection.Init, "Init", ms =>
            {
                ms.Write(CMD_INIT);
                ms.Write(CMD_ALIGN_CENTER);
                ms.WriteText(Divider('=', _charsPerLine) + "\n");
            }));

            if (!string.IsNullOrWhiteSpace(logoAssetName))
            {
                var logoCmd = TryBuildLogoCommand(logoAssetName, maxWidth: 180);
                if (logoCmd != null)
                    chunks.Add(new PrintChunk(PrintSection.Logo, "Logo", logoCmd));
            }

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
                ms.Write(CMD_ALIGN_CENTER);
                ms.Write(CMD_BOLD_ON);
                ms.Write(CMD_DWIDTH_ON);
                ms.WriteText("OFFICIAL RECEIPT\n");
                ms.Write(CMD_DWIDTH_OFF);
                ms.Write(CMD_BOLD_OFF);
                ms.Write(CMD_ALIGN_LEFT);
                ms.WriteText(Divider('=', _charsPerLine) + "\n");
                ms.WriteText(Col("Date", receipt.PrintDate.ToString("dd/MM/yyyy HH:mm:ss"), _charsPerLine) + "\n");
                ms.WriteText(Col("Ref", receipt.ReceiptNumber, _charsPerLine) + "\n");
                ms.WriteText(Col("Agent", receipt.AgentName, _charsPerLine) + "\n");
                ms.WriteText(Col("Point", receipt.CollectionPoint, _charsPerLine) + "\n");
                ms.WriteText(Divider('-', _charsPerLine) + "\n");
            }));

            for (int i = 0; i < receipt.Items.Count; i++)
            {
                var item = receipt.Items[i];
                int idx = i;

                chunks.Add(Chunk(PrintSection.Body, string.Format("Body_Line{0}", idx), ms =>
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

            chunks.Add(Chunk(PrintSection.Footer, "Footer", ms =>
            {
                ms.Write(CHUNK_PREAMBLE);
                if (!string.IsNullOrWhiteSpace(receipt.BarcodeLabel))
                {
                    ms.Write(CMD_ALIGN_CENTER);
                    ms.Write(BuildQRCodeCommand(receipt.BarcodeLabel));
                    ms.WriteText(Divider('-', _charsPerLine) + "\n");
                }
                ms.Write(CMD_ALIGN_CENTER);
                ms.Write(CMD_LF);
                ms.Write(CMD_BOLD_ON);
                ms.WriteText((receipt.FooterLine2 ?? " POWERED BY OSOFTPAY ") + "\n");
                ms.Write(CMD_BOLD_OFF);
                ms.WriteText(Divider('=', _charsPerLine) + "\n");
                ms.Write(CMD_ALIGN_LEFT);
            }));

            chunks.Add(Chunk(PrintSection.FeedAndCut, "FeedAndCut", ms =>
            {
                ms.Write(CMD_LF);
                ms.Write(CMD_LF);
                ms.Write(CMD_FEED_CUT);
            }));

            return chunks;
        }

        private List<PrintChunk> BuildTestChunks()
        {
            var chunks = new List<PrintChunk>();

            chunks.Add(Chunk(PrintSection.Init, "Init", ms => ms.Write(CMD_INIT)));

            var logoCmd = TryBuildLogoCommand("Logo.png", maxWidth: 150);
            if (logoCmd != null)
                chunks.Add(new PrintChunk(PrintSection.Logo, "Logo", logoCmd));

            chunks.Add(Chunk(PrintSection.Header, "Header", ms =>
            {
                ms.Write(CHUNK_PREAMBLE);
                ms.Write(CMD_ALIGN_CENTER);
                ms.Write(CMD_BOLD_ON);
                ms.Write(CMD_DHEIGHT_ON);
                ms.WriteText((App.RevenueServiceName ?? "BORNO STATE INTERNAL REVENUE SERVICE") + "\n");
                ms.Write(CMD_DHEIGHT_OFF);
                ms.Write(CMD_BOLD_OFF);
                ms.WriteText("OSOFT INTEGRATED RESOURCES LTD\n");
                ms.WriteText(Divider('=', _charsPerLine) + "\n");
                ms.Write(CMD_LF);
                ms.Write(CMD_BOLD_ON);
                ms.WriteText("PRINTER STATUS: ONLINE\n");
                ms.Write(CMD_BOLD_OFF);
                ms.WriteText(Divider('-', _charsPerLine) + "\n");
            }));
            chunks.Add(Chunk(PrintSection.FeedAndCut, "FeedAndCut", ms =>
            {
                ms.Write(CMD_ALIGN_LEFT);
                ms.Write(CMD_LF);
                ms.Write(CMD_LF);
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

                var ms = new MemoryStream();
                try
                {
                    ms.Write(CMD_ALIGN_CENTER);
                    ms.Write(CMD_LF);
                    byte xL = (byte)(widthBytes & 0xFF);
                    byte xH = (byte)((widthBytes >> 8) & 0xFF);
                    byte yL = (byte)(heightDots & 0xFF);
                    byte yH = (byte)((heightDots >> 8) & 0xFF);
                    ms.Write(new byte[] { 0x1D, 0x76, 0x30, 0x00, xL, xH, yL, yH });
                    ms.Write(raster, 0, raster.Length);
                    ms.Write(CMD_ALIGN_LEFT);
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

        private async Task<BluetoothSocket> OpenSocketAsync(
            BluetoothDevice device, CancellationToken token)
        {
            BluetoothSocket socket = null;
            try
            {
                socket = device.CreateRfcommSocketToServiceRecord(UUID.FromString(SPP_UUID));
                await ConnectWithTimeoutAsync(socket, CONNECT_TIMEOUT_MS, token);
                return socket;
            }
            catch (Exception primaryEx)
            {
                Log(string.Format("Primary socket failed ({0}); trying insecure fallback.", primaryEx.Message));
                SafeDispose(socket);
                try
                {
                    var method = device.Class.GetMethod("createRfcommSocket", Java.Lang.Integer.Type);
                    var fbSocket = (BluetoothSocket)method.Invoke(device, 1);
                    await ConnectWithTimeoutAsync(fbSocket, CONNECT_TIMEOUT_MS, token);
                    return fbSocket;
                }
                catch (Exception fbEx)
                {
                    throw new PrinterException(
                        string.Format("Could not connect to printer.\n  Primary:  {0}\n  Fallback: {1}",
                            primaryEx.Message, fbEx.Message));
                }
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
            catch (System.OperationCanceledException)
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

        private static BluetoothDevice FindPrinterDevice(BluetoothAdapter adapter)
            => adapter?.BondedDevices?
               .FirstOrDefault(d => SupportedPrinters.Contains(d.Name ?? string.Empty));

        private void EnsureBluetoothReady()
        {
            var adapter = BluetoothAdapter.DefaultAdapter
                ?? throw new PrinterException("Device has no Bluetooth adapter.");
            if (!adapter.IsEnabled)
                throw new PrinterException("Bluetooth is turned off. Enable it and retry.");
            if (FindPrinterDevice(adapter) == null)
                throw new PrinterException(
                    "No paired printer found. Pair the printer in Android Settings first.");
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


}
