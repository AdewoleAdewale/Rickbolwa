using Acr.UserDialogs;
using Rickbolwa.Helper;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;

namespace Rickbolwa.Services
{
    /// <summary>
    /// ══════════════════════════════════════════════════════════════════
    ///  THE ONLY WAY PAGES SHOULD PRINT.
    ///
    ///  Replaces every copy-pasted `CallPrinter(string)` raw-Bluetooth
    ///  block with a single call:
    ///
    ///      var receipt = ReceiptPrinter.CreateBrandedReceipt();
    ///      receipt.ReceiptNumber = transactId;
    ///      receipt.AgentName     = SessionService.Current?.Name;
    ///      receipt.Items.Add(new ReceiptItem { Description = service, Amount = amount });
    ///      receipt.TotalAmount = amount;
    ///      receipt.AmountPaid  = amount;
    ///      await ReceiptPrinter.PrintAsync(receipt);
    ///
    ///  What you get for free:
    ///    • Android 12+ runtime Bluetooth permission handling
    ///    • printer availability check with a friendly prompt
    ///    • chunked transmission with checkpoint / resume
    ///    • the job is persisted to disk first — if Bluetooth drops or
    ///      the app dies, App.PrintJobManager retries it on next launch
    ///    • a progress dialog and consistent error messaging
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static class ReceiptPrinter
    {
        /// <summary>
        /// New ReceiptData pre-filled from BrandConfig so every module prints
        /// an identically branded receipt. Rebranding receipts = editing
        /// BrandConfig only.
        /// </summary>
        public static ReceiptData CreateBrandedReceipt()
        {
            return new ReceiptData
            {
                StoreName = BrandConfig.ReceiptStoreName,
                StoreAddress = BrandConfig.ReceiptAddress,
                StorePhone = BrandConfig.ReceiptPhone,
                FooterLine1 = BrandConfig.ReceiptFooterLine1,
                FooterLine2 = BrandConfig.ReceiptFooterLine2,
                BarcodeLabel = BrandConfig.VerifyReceiptUrl,
                PrintDate = DateTime.Now
            };
        }

        /// <summary>
        /// Durably prints <paramref name="receipt"/>. Returns true when the
        /// receipt fully printed; false when it was queued but could not be
        /// completed now (it will be retried automatically on next launch or
        /// via <see cref="RetryPendingAsync"/>).
        /// </summary>
        public static async Task<bool> PrintAsync(ReceiptData receipt)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));

            // 1 ── Runtime Bluetooth permissions (Android 12+)
            var granted = await BluetoothPermissionHelper.RequestAsync();
            if (!granted)
            {
                await ShowAlertAsync("Bluetooth Permission",
                    "Printing needs Bluetooth permission. Please allow it in Settings and try again.");
                return false;
            }

            // 2 ── Persist the job FIRST so nothing is ever lost
            var job = await App.PrintJobManager.EnqueueAsync(
                receipt, BrandConfig.ReceiptLogoAsset);

            // 3 ── Availability check with a friendly prompt
            var available = await App.Printer.IsPrinterAvailableAsync();
            if (!available)
            {
                var retry = await UserDialogs.Instance.ConfirmAsync(
                    "No paired printer found.\n\nTurn on your printer and Bluetooth, then tap RETRY. " +
                    "The receipt is saved and will also retry automatically later.",
                    "🖨️ Printer Not Found", "RETRY", "LATER");

                if (!retry) return false;

                available = await App.Printer.IsPrinterAvailableAsync();
                if (!available)
                {
                    UserDialogs.Instance.Toast("Receipt saved — it will print automatically once the printer is available.");
                    return false;
                }
            }

            // 4 ── Execute with progress feedback
            using (var dialog = UserDialogs.Instance.Loading("🖨️ Printing receipt…", null, null, true, MaskType.Black))
            {
                var progress = new Progress<PrintProgress>(p =>
                {
                    if (!string.IsNullOrEmpty(p.ChunkName))
                        dialog.Title = string.Format("🖨️ Printing… {0}", p.ChunkName);
                });

                try
                {
                    await App.PrintJobManager.ExecuteAsync(job.JobId, progress);
                    UserDialogs.Instance.Toast("✅ Receipt printed");
                    return true;
                }
                catch (Exception)
                {
                    // Job stays on disk in PartialSuccess/Pending state.
                    await ShowAlertAsync("Print Interrupted",
                        "The receipt could not finish printing. It has been saved and will " +
                        "resume automatically. You can also reprint it from History.");
                    return false;
                }
            }
        }

        /// <summary>
        /// Retries every unfinished job on disk. App.OnResume calls this;
        /// dashboards may also expose it behind a "Retry pending receipts" action.
        /// </summary>
        public static async Task RetryPendingAsync()
        {
            try
            {
                if (await App.Printer.IsPrinterAvailableAsync())
                    await App.PrintJobManager.ResumeUnfinishedJobsAsync();
            }
            catch
            {
                // Silent — resume is opportunistic; jobs remain persisted.
            }
        }

        private static Task ShowAlertAsync(string title, string message)
        {
            var tcs = new TaskCompletionSource<bool>();
            Device.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    await Application.Current.MainPage.DisplayAlert(title, message, "OK");
                    tcs.TrySetResult(true);
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }
    }
}
