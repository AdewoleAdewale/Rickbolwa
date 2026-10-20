using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Xamarin.Essentials;
using Formatting = Newtonsoft.Json.Formatting;

namespace Rickbolwa.Services
{
    // ══════════════════════════════════════════════════════════════
    //  PRINT JOB STATUS
    // ══════════════════════════════════════════════════════════════

    public enum PrintJobStatus
    {
        Pending,        // queued, not yet attempted
        InProgress,     // currently being sent
        PartialSuccess, // some chunks sent; waiting for resume
        Completed,      // all chunks confirmed sent
        Failed          // exhausted all retries; manual intervention needed
    }

    // ══════════════════════════════════════════════════════════════
    //  DURABLE PRINT JOB
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// A serialisable record that pairs a <see cref="ReceiptData"/> payload
    /// with progress tracking fields. Persisted as a JSON file so it survives
    /// app restarts. The <see cref="NextChunkIndex"/> field is the resume cursor:
    /// when the job is retried, chunks 0..(NextChunkIndex-1) are skipped.
    /// </summary>
    public sealed class PrintJob
    {
        // ── Identity ──────────────────────────────────────────────
        [JsonProperty("id")]
        public string JobId { get; set; } = Guid.NewGuid().ToString("N").ToUpper();

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("updatedAt")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // ── Payload ───────────────────────────────────────────────
        [JsonProperty("receipt")]
        public ReceiptData Receipt { get; set; }

        [JsonProperty("logoAssetName")]
        public string LogoAssetName { get; set; } = "Logo.png";

        [JsonProperty("use80mm")]
        public bool Use80mm { get; set; } = false;

        // ── Progress ──────────────────────────────────────────────

        /// <summary>
        /// Zero-based index of the next chunk to transmit.
        /// Persisted after each chunk succeeds so a crash only
        /// requires the remaining chunks to be resent.
        /// </summary>
        [JsonProperty("nextChunkIndex")]
        public int NextChunkIndex { get; set; } = 0;

        [JsonProperty("totalChunks")]
        public int TotalChunks { get; set; } = 0; // Header + Body + Footer

        [JsonProperty("status")]
        public PrintJobStatus Status { get; set; } = PrintJobStatus.Pending;

        [JsonProperty("attemptCount")]
        public int AttemptCount { get; set; } = 0;

        [JsonProperty("lastError")]
        public string LastError { get; set; }

        // ── Convenience ───────────────────────────────────────────
        [JsonIgnore]
        public bool IsCompleted => Status == PrintJobStatus.Completed;

        [JsonIgnore]
        public bool IsResumable =>
            Status == PrintJobStatus.PartialSuccess && NextChunkIndex > 0;
    }

    // ══════════════════════════════════════════════════════════════
    //  FILE HELPERS  (.NET Standard 2.0 / C# 7.3 compatible)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Polyfills for File.WriteAllTextAsync / ReadAllTextAsync which are
    /// only available in .NET Standard 2.1+.  Xamarin targets 2.0, so we
    /// wrap synchronous IO in Task.Run to keep callers await-able.
    /// </summary>
    internal static class FileHelper
    {
        public static Task WriteAllTextAsync(string path, string contents)
            => Task.Run(() => File.WriteAllText(path, contents));

        public static Task<string> ReadAllTextAsync(string path)
            => Task.Run(() => File.ReadAllText(path));
    }

    // ══════════════════════════════════════════════════════════════
    //  PRINT JOB STORE  (lightweight JSON file persistence)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Persists <see cref="PrintJob"/> records to the app's private data
    /// directory as individual JSON files: one file per job, named by
    /// <c>{JobId}.printjob.json</c>.
    ///
    /// Thread-safety: all public methods acquire a <see cref="SemaphoreSlim"/>
    /// so concurrent calls from the UI thread and background retry tasks are safe.
    /// </summary>
    public sealed class PrintJobStore
    {
        private readonly string _directory;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private const string FileExt = ".printjob.json";

        public PrintJobStore()
        {
            // FileSystem.AppDataDirectory resolves to the app's private sandbox on Android.
            _directory = FileSystem.AppDataDirectory;
        }

        // ── CRUD ──────────────────────────────────────────────────

        public async Task SaveAsync(PrintJob job)
        {
            await _lock.WaitAsync();
            try
            {
                job.UpdatedAt = DateTime.UtcNow;
                string json = JsonConvert.SerializeObject(job, Formatting.Indented);
                // Use polyfill — File.WriteAllTextAsync not available in .NET Std 2.0
                await FileHelper.WriteAllTextAsync(FilePath(job.JobId), json);
            }
            finally { _lock.Release(); }
        }

        public async Task<PrintJob> LoadAsync(string jobId)
        {
            await _lock.WaitAsync();
            try
            {
                string path = FilePath(jobId);
                if (!File.Exists(path)) return null;
                string json = await FileHelper.ReadAllTextAsync(path);
                return JsonConvert.DeserializeObject<PrintJob>(json);
            }
            finally { _lock.Release(); }
        }

        public async Task<List<PrintJob>> LoadAllAsync()
        {
            await _lock.WaitAsync();
            try
            {
                var jobs = new List<PrintJob>();
                foreach (var file in Directory.GetFiles(_directory, string.Format("*{0}", FileExt)))
                {
                    try
                    {
                        string json = await FileHelper.ReadAllTextAsync(file);
                        var job = JsonConvert.DeserializeObject<PrintJob>(json);
                        if (job != null) jobs.Add(job);
                    }
                    catch { /* corrupt file — skip */ }
                }
                return jobs.OrderBy(j => j.CreatedAt).ToList();
            }
            finally { _lock.Release(); }
        }

        /// <summary>Returns all jobs that are still pending or partially completed.</summary>
        public async Task<List<PrintJob>> LoadUnfinishedAsync()
        {
            var all = await LoadAllAsync();
            return all.Where(j =>
                j.Status == PrintJobStatus.Pending ||
                j.Status == PrintJobStatus.PartialSuccess ||
                j.Status == PrintJobStatus.InProgress)
            .ToList();
        }

        public async Task DeleteAsync(string jobId)
        {
            await _lock.WaitAsync();
            try
            {
                string path = FilePath(jobId);
                if (File.Exists(path)) File.Delete(path);
            }
            finally { _lock.Release(); }
        }

        /// <summary>
        /// Removes all jobs older than <paramref name="maxAge"/> whose status
        /// is Completed or Failed. Call this on app start to keep the directory tidy.
        /// </summary>
        public async Task PruneAsync(TimeSpan maxAge)
        {
            var all = await LoadAllAsync();
            var cutoff = DateTime.UtcNow - maxAge;

            foreach (var job in all)
            {
                if ((job.Status == PrintJobStatus.Completed ||
                     job.Status == PrintJobStatus.Failed) &&
                     job.UpdatedAt < cutoff)
                {
                    await DeleteAsync(job.JobId);
                }
            }
        }

        private string FilePath(string jobId) =>
            Path.Combine(_directory, jobId + FileExt);
    }

    // ══════════════════════════════════════════════════════════════
    //  PRINT JOB MANAGER  (orchestrates queue, persistence, retry)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// High-level façade that combines <see cref="IPrinterService"/>,
    /// <see cref="PrintJobStore"/>, and <see cref="PrintRetryPolicy"/> into a
    /// single "fire and forget (durably)" API.
    ///
    /// Typical usage:
    /// <code>
    ///   // After a successful payment:
    ///   var job = await _printJobManager.EnqueueAsync(receiptData);
    ///   await _printJobManager.ExecuteAsync(job.JobId, progressHandler);
    ///
    ///   // On app resume — retry anything interrupted last time:
    ///   await _printJobManager.ResumeUnfinishedJobsAsync(progressHandler);
    /// </code>
    /// </summary>
    public class PrintJobManager : IDisposable
    {
        private readonly IPrinterService _printer;
        private readonly PrintJobStore _store;
        private readonly PrintRetryPolicy _policy;
        private readonly SemaphoreSlim _executionLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public PrintJobManager(
            IPrinterService printer,
            PrintJobStore store = null,
            PrintRetryPolicy policy = null)
        {
            _printer = printer ?? throw new ArgumentNullException(nameof(printer));
            _store = store ?? new PrintJobStore();
            _policy = policy ?? PrintRetryPolicy.Default;
        }

        // ── Enqueue ───────────────────────────────────────────────

        /// <summary>
        /// Serialises <paramref name="receipt"/> to disk as a pending job and
        /// returns the job record. Call <see cref="ExecuteAsync"/> next to
        /// actually send it to the printer.
        /// </summary>
        public async Task<PrintJob> EnqueueAsync(
            ReceiptData receipt,
            string logoAssetName = "Logo.png",
            bool use80mm = false)
        {
            var job = new PrintJob
            {
                Receipt = receipt,
                LogoAssetName = logoAssetName,
                Use80mm = use80mm,
                Status = PrintJobStatus.Pending
            };
            await _store.SaveAsync(job);
            Log(string.Format("Enqueued job {0}.", job.JobId));
            return job;
        }

        // ── Execute ───────────────────────────────────────────────

        /// <summary>
        /// Executes the job identified by <paramref name="jobId"/>.
        /// If the job was previously partially completed (status =
        /// <see cref="PrintJobStatus.PartialSuccess"/>) execution resumes
        /// from <see cref="PrintJob.NextChunkIndex"/>, skipping already-sent chunks.
        /// </summary>
        public async Task ExecuteAsync(
            string jobId,
            IProgress<PrintProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            // Only one job at a time — BT serial port is exclusive
            await _executionLock.WaitAsync(cancellationToken);
            try
            {
                var job = await _store.LoadAsync(jobId);
                if (job == null)
                    throw new InvalidOperationException(string.Format("Print job {0} not found in store.", jobId));

                if (job.IsCompleted)
                {
                    Log(string.Format("Job {0} already completed — skipping.", jobId));
                    return;
                }

                await ExecuteJobAsync(job, progress, cancellationToken);
            }
            finally
            {
                _executionLock.Release();
            }
        }


        public Task DeleteJobAsync(string jobId)
        => _store.DeleteAsync(jobId);

        // ── Resume unfinished jobs ────────────────────────────────

        /// <summary>
        /// Loads all unfinished jobs from the store and attempts to execute
        /// them in creation order. Call this during app startup or when the
        /// user re-enables Bluetooth.
        /// </summary>
        public async Task<ResumeResult> ResumeUnfinishedJobsAsync(
            IProgress<PrintProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            var unfinished = await _store.LoadUnfinishedAsync();
            if (unfinished.Count == 0)
            {
                Log("ResumeUnfinished: nothing to resume.");
                return new ResumeResult(0, 0);
            }

            Log(string.Format("ResumeUnfinished: {0} job(s) found.", unfinished.Count));

            int completed = 0;
            int failed = 0;

            foreach (var job in unfinished)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    await ExecuteAsync(job.JobId, progress, cancellationToken);
                    completed++;
                }
                catch (Exception ex)
                {
                    Log(string.Format("ResumeUnfinished: job {0} failed – {1}", job.JobId, ex.Message));
                    failed++;
                }
            }

            return new ResumeResult(completed, failed);
        }

        // ── Core execution ────────────────────────────────────────

        private async Task ExecuteJobAsync(
            PrintJob job,
            IProgress<PrintProgress> progress,
            CancellationToken cancellationToken)
        {
            job.Status = PrintJobStatus.InProgress;
            job.AttemptCount++;
            await _store.SaveAsync(job);

            // Build the progress adapter that also updates the persisted job
            var wrappedProgress = new Progress<PrintProgress>(async p =>
            {
                // Forward to caller
                progress?.Report(p);

                // Persist cursor after each chunk completion
                if (p.Status == PrintProgressStatus.ChunkCompleted)
                {
                    job.NextChunkIndex = p.ChunkIndex; // p.ChunkIndex is already incremented
                    job.Status = PrintJobStatus.PartialSuccess;
                    await _store.SaveAsync(job);
                    Log(string.Format("Job {0}: cursor saved at chunk {1}.", job.JobId, job.NextChunkIndex));
                }
            });

            try
            {
                await _printer.PrintReceiptAsync(
                    job.Receipt,
                    job.LogoAssetName,
                    Rickbolwa.Helper.BrandConfig.ReceiptWatermark,
                    _policy,
                    wrappedProgress,
                    cancellationToken);

                // Confirmed complete
                job.Status = PrintJobStatus.Completed;
                job.NextChunkIndex = job.TotalChunks;
                job.LastError = null;
                await _store.SaveAsync(job);
                Log(string.Format("Job {0}: completed.", job.JobId));
            }
            catch (OperationCanceledException)
            {
                // Leave status as PartialSuccess so it can be resumed
                job.Status = PrintJobStatus.PartialSuccess;
                job.LastError = "Cancelled.";
                await _store.SaveAsync(job);
                throw;
            }
            catch (Exception ex)
            {
                job.Status = job.NextChunkIndex > 0
                                ? PrintJobStatus.PartialSuccess   // can resume
                                : PrintJobStatus.Failed;           // never started
                job.LastError = ex.Message;
                await _store.SaveAsync(job);
                Log(string.Format("Job {0}: failed at chunk {1} – {2}", job.JobId, job.NextChunkIndex, ex.Message));
                throw;
            }
        }

        // ── Diagnostics ───────────────────────────────────────────

        /// <summary>Returns a summary of all stored jobs for a diagnostics screen.</summary>
        public async Task<List<PrintJob>> GetAllJobsAsync()
            => await _store.LoadAllAsync();

        /// <summary>
        /// Removes completed and failed jobs older than 48 hours.
        /// Safe to call on every app launch.
        /// </summary>
        public Task PruneOldJobsAsync()
            => _store.PruneAsync(TimeSpan.FromHours(48));

        private static void Log(string msg)
            => System.Diagnostics.Debug.WriteLine(string.Format("[PrintJobManager] {0}", msg));

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _executionLock.Dispose();
            _printer.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  RESUME RESULT  (replaces tuple deconstruction — C# 7.3 safe)
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Return value of <see cref="PrintJobManager.ResumeUnfinishedJobsAsync"/>.
    /// Using a named class instead of a ValueTuple avoids the C# 8.0
    /// deconstruction syntax that is unavailable in C# 7.3 / .NET Std 2.0.
    /// </summary>
    public sealed class ResumeResult
    {
        public int Completed { get; }
        public int Failed { get; }

        public ResumeResult(int completed, int failed)
        {
            Completed = completed;
            Failed = failed;
        }
    }
}
