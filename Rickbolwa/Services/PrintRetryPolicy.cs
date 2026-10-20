using System;
using System.Collections.Generic;
using System.Text;

namespace Rickbolwa.Services
{
    // ══════════════════════════════════════════════════════════════
    //  PRINT RETRY POLICY
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Controls how many times a chunk is retried and how long to wait
    /// between attempts. Pass a custom instance to override defaults.
    /// </summary>
    public sealed class PrintRetryPolicy
    {
        /// <summary>Maximum send attempts per chunk (first attempt + retries).</summary>
        public int MaxAttempts { get; set; } = 3;

        /// <summary>Base delay in milliseconds between retry attempts.</summary>
        public int RetryDelayMs { get; set; } = 1_500;

        /// <summary>
        /// When true, the delay doubles with each failed attempt
        /// (1 500 ms → 3 000 ms → 6 000 ms …).
        /// </summary>
        public bool UseExponentialBackoff { get; set; } = true;

        /// <summary>
        /// Returns the delay to wait before attempt number <paramref name="attemptNumber"/>
        /// (1-based). Attempt 1 = first try, so no pre-delay.
        /// </summary>
        public int GetDelayMs(int attemptNumber)
        {
            if (attemptNumber <= 1) return 0;
            if (!UseExponentialBackoff) return RetryDelayMs;
            // attempt 2 → 1×base, attempt 3 → 2×base, attempt 4 → 4×base …
            return RetryDelayMs * (int)Math.Pow(2, attemptNumber - 2);
        }

        /// <summary>Sensible production defaults.</summary>
        public static PrintRetryPolicy Default => new PrintRetryPolicy
        {
            MaxAttempts = 3,
            RetryDelayMs = 1_500,
            UseExponentialBackoff = true
        };

        /// <summary>Single-attempt policy for unit tests.</summary>
        public static PrintRetryPolicy NoRetry => new PrintRetryPolicy
        {
            MaxAttempts = 1,
            RetryDelayMs = 0,
            UseExponentialBackoff = false
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  PRINT CHUNK TYPE
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Logical role of a print chunk within the receipt layout.
    /// Used in progress reports so the UI can display a meaningful label.
    /// </summary>
    public enum PrintChunkType
    {
        /// <summary>Logo image and initialisation commands.</summary>
        Header = 0,

        /// <summary>Line items and transaction details.</summary>
        Body = 1,

        /// <summary>Totals, QR code, footer text and paper cut.</summary>
        Footer = 2,

        /// <summary>General / uncategorised chunk.</summary>
        Other = 99
    }

    // ══════════════════════════════════════════════════════════════
    //  PRINT PROGRESS STATUS
    // ══════════════════════════════════════════════════════════════

    /// <summary>Granular status codes emitted via <see cref="IProgress{PrintProgress}"/>.</summary>
    public enum PrintProgressStatus
    {
        /// <summary>The print session has just started.</summary>
        SessionStarted,

        /// <summary>A chunk is about to be transmitted.</summary>
        ChunkStarted,

        /// <summary>A chunk was sent and confirmed by the printer.</summary>
        ChunkCompleted,

        /// <summary>A chunk failed and will be retried.</summary>
        ChunkRetrying,

        /// <summary>A chunk exhausted all retry attempts.</summary>
        ChunkFailed,

        /// <summary>All chunks were sent; the session finished cleanly.</summary>
        SessionCompleted,

        /// <summary>The session was cancelled by the caller.</summary>
        SessionCancelled,

        /// <summary>The session terminated with an unrecoverable error.</summary>
        SessionFailed
    }

    // ══════════════════════════════════════════════════════════════
    //  PRINT PROGRESS
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Payload emitted to <see cref="IProgress{PrintProgress}"/> after every
    /// meaningful state transition during a print session. Immutable once created.
    /// </summary>
    public sealed class PrintProgress
    {
        // ── Session identity ──────────────────────────────────────

        /// <summary>Short random ID that groups all events for a single session.</summary>
        public string SessionId { get; set; }

        // ── Current chunk ─────────────────────────────────────────

        /// <summary>Human-readable name of the chunk being processed.</summary>
        public string ChunkName { get; set; }

        /// <summary>Logical role of the current chunk.</summary>
        public PrintChunkType ChunkType { get; set; }

        /// <summary>
        /// Number of chunks confirmed <em>so far</em> (after this event).
        /// When <see cref="Status"/> is <see cref="PrintProgressStatus.ChunkCompleted"/>
        /// this equals the 1-based index of the chunk just sent.
        /// </summary>
        public int ChunkIndex { get; set; }

        /// <summary>Total number of chunks in the job.</summary>
        public int TotalChunks { get; set; }

        // ── Attempt info ──────────────────────────────────────────

        /// <summary>Current attempt number (1-based).</summary>
        public int AttemptNumber { get; set; }

        // ── Status ────────────────────────────────────────────────

        /// <summary>What just happened.</summary>
        public PrintProgressStatus Status { get; set; }

        /// <summary>Optional human-readable detail for logs / UI.</summary>
        public string Message { get; set; }

        // ── Convenience ───────────────────────────────────────────

        /// <summary>0–1 fraction of chunks confirmed so far.</summary>
        public double FractionComplete =>
            TotalChunks > 0 ? (double)ChunkIndex / TotalChunks : 0;

        public override string ToString() =>
            $"[{Status}] {ChunkName} ({ChunkIndex}/{TotalChunks}) attempt {AttemptNumber}: {Message}";
    }
}