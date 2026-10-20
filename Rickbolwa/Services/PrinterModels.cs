using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;
using Xamarin.Essentials;
using static Android.Provider.SyncStateContract;

namespace Rickbolwa.Services
{
    // ═════════════════════════════════════════════════════════════
    //  PLATFORM-NEUTRAL PRINTING MODELS
    //  Extracted from BluetoothPrinterService.cs so the shared
    //  (netstandard) project no longer references Android assemblies.
    //  The Android-specific service lives in
    //  BornoGeneral.Android/Services/BluetoothPrinterService.cs.
    // ═════════════════════════════════════════════════════════════

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




    public sealed class ReceiptData
    {
        public string StoreName { get; set; } = Helper.BrandConfig.ReceiptStoreName;
        public string StoreSubTitle { get; set; }
        public string StoreAddress { get; set; } = Helper.BrandConfig.ReceiptAddress;
        public string StorePhone { get; set; } = Helper.BrandConfig.ReceiptPhone;
        public string ReceiptNumber { get; set; } = "N/A";
        public string AgentName { get; set; }
        public string CollectionPoint { get; set; }
        public string Consultant { get; set; }
        public string SuperAgent { get; set; }
        public DateTime PrintDate { get; set; } = DateTime.Now;
        public List<ReceiptItem> Items { get; set; } = new List<ReceiptItem>();
        public decimal TotalAmount { get; set; }
        public decimal AmountPaid { get; set; }

        /// <summary>Remaining balance after payment.</summary>
        public decimal AmountLeft { get; set; }

        public string FooterLine1 { get; set; } = "Thank You!";
        public string FooterLine2 { get; set; } = Helper.BrandConfig.ReceiptFooterLine2;

        /// <summary>
        /// Full verification URL encoded as a QR code.
        /// Set to null or empty to skip the QR block entirely.
        /// </summary>
        public string BarcodeLabel { get; set; } =
            Helper.BrandConfig.VerifyReceiptUrl;
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
