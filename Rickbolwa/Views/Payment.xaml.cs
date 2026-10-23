using Acr.UserDialogs;
using Android.Accounts;
using DIPS.Xamarin.UI.Controls.Sheet;
using Newtonsoft.Json;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xamarin.Essentials;
using Xamarin.Forms;
using Xamarin.Forms.PlatformConfiguration.iOSSpecific;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class Payment : ContentPage
    {
        // ───────────────────────── Endpoints ─────────────────────────
        private const string BaseUrl = "https://rickbolwa.osoftpay.net";
        private const string PaymentUrl = BaseUrl + "/Api/SingleCollections/Hulage";

        private const string SuccessCode = "00";

        // One shared client — per-call HttpClient instances leak sockets on Android.
        private static readonly HttpClient Http = CreateHttpClient();

        private bool _isProcessing;
        private bool _isPrinting;
        private bool _isFormValid;

        private string _destination = string.Empty;
        private decimal _expectedAmount;

        private ReceiptData _lastReceiptData;

        public Payment()
        {
            InitializeComponent();

            try
            {
                ConfigureSSL();

                sheetBehavior.IsOpen = false;
                SuccessOverlay.IsVisible = false;
                FailureOverlay.IsVisible = false;
                LoadingOverlay.IsVisible = false;
                TrackUserActivity();
                LoadVerifiedDetails();
                Validate();
            }
            catch (Exception ex)
            {
                Log("ctor", ex);
            }
        }

        private void TrackUserActivity()
        {
            try
            {
                var tapGesture = new TapGestureRecognizer();
                tapGesture.Tapped += (s, e) => SessionManager.Instance.UpdateActivity();
                if (this.Content != null)
                    this.Content.GestureRecognizers.Add(tapGesture);
            }
            catch (Exception ex)
            {
                LogError("TrackUserActivity", ex);
            }
        }

        private void LogError(string method, Exception ex)
        {
            try
            {
                Debug.WriteLine($"[ERROR] {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {method}");
                Debug.WriteLine($"[ERROR] Message: {ex?.Message}");
                Debug.WriteLine($"[ERROR] StackTrace: {ex?.StackTrace}");

                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ROCKBOLWA", "Logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

                string logFile = Path.Combine(logDir, $"error_log_{DateTime.Now:yyyy-MM-dd}.txt");
                File.AppendAllText(logFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {method}: {ex?.Message}\n{ex?.StackTrace}\n\n");
            }
            catch { }
        }


        // ───────────────────────── Verified details ─────────────────────────

        /// <summary>
        /// Every field except the PIN comes from the verified vehicle record.
        /// Destination To is resolved from Verify, falling back to the vehicle's LGA
        /// only if the verify response did not carry a destination.
        /// </summary>
        private void LoadVerifiedDetails()
        {
            try
            {
                Servicename.Text = SafeString(Verify.vehicleTypess, "—");
                PayerId.Text = SafeString(Verify.plateNumberss, "—");

                _destination = ResolveDestination();
                DestinationTo.Text = SafeString(_destination, "—");

                var hasDestination = !string.IsNullOrWhiteSpace(_destination);
                destinationBadge.Text = hasDestination ? "VERIFIED" : "MISSING";
                destinationBadge.TextColor = hasDestination ? Color.Black : Color.ForestGreen;
                destinationError.IsVisible = !hasDestination;

                _expectedAmount = ParseAmount(Verify.amountss);
                Amount.Text = _expectedAmount > 0
                    ? _expectedAmount.ToString("N2", CultureInfo.InvariantCulture)
                    : SafeString(Verify.amountss);

                UpdateAmountHeadline();
            }
            catch (Exception ex)
            {
                Log("LoadVerifiedDetails", ex);
            }
        }

        private static string ResolveDestination()
        {
            try
            {
                // Verify.destinationToss is the primary source; lga is the legacy fallback.
                var destination = SafeString(Verify.lgass);

                if (string.IsNullOrWhiteSpace(destination))
                    destination = SafeString(Verify.lgass);

                return destination;
            }
            catch (Exception ex)
            {
                Log("ResolveDestination", ex);
                return string.Empty;
            }
        }

        // ───────────────────────── Networking setup ─────────────────────────

        private static HttpClient CreateHttpClient()
        {
            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (m, c, ch, er) => true
                };

                try
                {
                    handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                         | System.Security.Authentication.SslProtocols.Tls11;
                }
                catch (Exception ex)
                {
                    Log("SslProtocols", ex);
                }

                return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
            }
            catch (Exception ex)
            {
                Log("CreateHttpClient", ex);
                return new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            }
        }

        private void ConfigureSSL()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
                ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, er) => true;
                ServicePointManager.DefaultConnectionLimit = 10;
                ServicePointManager.Expect100Continue = false;
            }
            catch (Exception ex)
            {
                Log("ConfigureSSL", ex);
            }
        }

        // ───────────────────────── Validation / UI state ─────────────────────────

        private void OnPinChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var pin = SafeText(PIN);
                var ok = pin.Length == 4 && pin.All(char.IsDigit);

                pinBadge.Text = ok ? "✓ VALID" : "4 DIGITS";
                pinBadge.TextColor = ok ? Color.FromHex("#00893E") : Color.FromHex("#FF6B6B");

                pinError.Text = "PIN must be exactly 4 digits.";
                pinError.IsVisible = pin.Length > 0 && !ok;

                Validate();
            }
            catch (Exception ex) { Log("OnPinChanged", ex); }
        }

        private void OnAmountChanged(object sender, TextChangedEventArgs e)
        {
            try { UpdateAmountHeadline(); }
            catch (Exception ex) { Log("OnAmountChanged", ex); }
        }

        private void Pin_Unfocused(object sender, FocusEventArgs e)
        {
            try { Validate(); }
            catch (Exception ex) { Log("Pin_Unfocused", ex); }
        }

        private void Validate()
        {
            try
            {
                var pin = SafeText(PIN);
                var pinOk = pin.Length == 4 && pin.All(char.IsDigit);
                var destOk = !string.IsNullOrWhiteSpace(_destination);

                _isFormValid = pinOk && destOk;

                makepaymentbutton.BackgroundGradientStartColor = _isFormValid ? Color.FromHex("#004225") : Color.FromHex("#B9C7BF");
                makepaymentbutton.BackgroundGradientEndColor = _isFormValid ? Color.FromHex("#00AA55") : Color.FromHex("#9FB0A6");
                makepaymentbutton.Opacity = _isFormValid ? 1 : 0.65;
                makepaymentbutton.InputTransparent = !_isFormValid || _isProcessing;

                payIcon.Text = _isFormValid ? "+" : "🔒";

                if (!_isProcessing)
                {
                    payLabel.Text = _isFormValid
                        ? "PROCESS PAYMENT"
                        : (!destOk ? "DESTINATION MISSING" : "ENTER PIN TO CONTINUE");
                }
            }
            catch (Exception ex)
            {
                Log("Validate", ex);
            }
        }

        private void UpdateAmountHeadline()
        {
            try
            {
                var value = ParseAmount(SafeText(Amount));

                if (value > 0)
                {
                    AmountHeadline.Text = "₦" + value.ToString("N2", CultureInfo.InvariantCulture);
                    AmountSubLabel.Text = SafeString(Verify.vehicleTypess, "Haulage collection");
                }
                else
                {
                    AmountHeadline.Text = "₦0.00";
                    AmountSubLabel.Text = "Final amount is confirmed by the server";
                }
            }
            catch (Exception ex) { Log("UpdateAmountHeadline", ex); }
        }

        private void SetLoadingState(bool loading)
        {
            try
            {
                LoadingOverlay.IsVisible = loading;

                payBusy.IsVisible = loading;
                payBusy.IsRunning = loading;
                payArrow.IsVisible = !loading;
                payLabel.Text = loading ? "PROCESSING…" : (_isFormValid ? "PROCESS PAYMENT" : "ENTER PIN TO CONTINUE");

                makepaymentbutton.InputTransparent = loading || !_isFormValid;
                PIN.IsEnabled = !loading;
            }
            catch (Exception ex) { Log("SetLoadingState", ex); }
        }

        // ───────────────────────── Payment ─────────────────────────

        private async void TapGestureRecognizer_Tapped(object sender, EventArgs e)
        {
            try
            {
                if (_isProcessing) return;
                await ProcessPaymentAsync();
            }
            catch (Exception ex)
            {
                Log("TapGestureRecognizer_Tapped", ex);
                await ShowFailurePopup("UNEXPECTED ERROR",
                    "Something went wrong before the request was sent. Please try again.");
            }
        }

        private async Task ProcessPaymentAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_destination))
                {
                    await ShowFailurePopup("DESTINATION MISSING",
                        "This vehicle record did not return a destination. Go back and re-verify the plate number before taking payment.");
                    return;
                }

                var pin = SafeText(PIN);

                if (pin.Length != 4 || !pin.All(char.IsDigit))
                {
                    await ShowFailurePopup("INVALID PIN",
                        "Your transaction PIN must be exactly 4 digits.");
                    return;
                }

                if (!string.IsNullOrEmpty(MainPage.Pin) && pin != MainPage.Pin)
                {
                    await ShowFailurePopup("INCORRECT PIN",
                        "The transaction PIN you entered does not match the PIN on this device. Please try again.");
                    return;
                }

                _isProcessing = true;
                SetLoadingState(true);

                var plate = SafeString(Verify.plateNumberss);
                var amount = SafeText(Amount); // Or _expectedAmount.ToString() if Amount control isn't present

                var nvc = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("ServiceName", SafeString(Verify.vehicleTypess)),
            new KeyValuePair<string, string>("Email", SafeString(MainPage.ValidUserMail)),
            new KeyValuePair<string, string>("Amount", amount),
            new KeyValuePair<string, string>("Pin", pin),
            new KeyValuePair<string, string>("Payer", plate),
            new KeyValuePair<string, string>("StateTo", _destination) // Change to CatNameSelected if using a different field
        };

                // Log parameters safely without exposing the user's PIN
                System.Diagnostics.Debug.WriteLine(
                    $"[HaulagePayment] Request: {JsonConvert.SerializeObject(nvc.Where(kv => kv.Key != "Pin"))}");

                string body;
                int statusCode;

                using (var content = new FormUrlEncodedContent(nvc))
                using (var response = await Http.PostAsync(PaymentUrl, content).ConfigureAwait(true))
                {
                    body = await SafeReadAsync(response).ConfigureAwait(true);
                    statusCode = (int)response.StatusCode;
                }

                System.Diagnostics.Debug.WriteLine($"[HaulagePayment] Response ({statusCode}): {body}");

                StateCollectionResponseObject resp = null;

                try
                {
                    if (!string.IsNullOrWhiteSpace(body))
                        resp = JsonConvert.DeserializeObject<StateCollectionResponseObject>(body);
                }
                catch (Exception ex)
                {
                    Log("Deserialize", ex);
                }

                if (resp == null)
                {
                    await ShowFailurePopup("UNEXPECTED RESPONSE",
                        $"The server returned data we could not read (HTTP {statusCode}). Please try again, and check History before retrying if your wallet was debited.");
                    return;
                }

                if (IsSuccess(resp.RespondCode))
                {
                    var receipt = BuildReceiptData(resp);
                    _lastReceiptData = receipt;

                    await ShowSuccessPopup(resp);

                    // Fire and forget — printing must never block or crash the payment flow.
                    _ = AttemptPrintAsync(receipt, isReprint: false);
                }
                else
                {
                    await ShowFailurePopup(TitleForCode(resp.RespondCode),
                        FirstNonEmpty(resp.Message, resp.ResponseMessage,
                            $"The transaction was declined (code {SafeString(resp.RespondCode, "—")})."),
                        detail: DifferingDetail(resp),
                        code: resp.RespondCode,
                        reference: resp.TransactionNo,
                        amount: resp.TotalAmount > 0 ? resp.TotalAmount : _expectedAmount);
                }
            }
            catch (TaskCanceledException)
            {
                await ShowFailurePopup("REQUEST TIMED OUT",
                    "The server did not respond in time. Do not retry immediately — check History first to confirm whether the payment went through.");
            }
            catch (HttpRequestException ex)
            {
                Log("ProcessPayment/Http", ex);
                await ShowFailurePopup("NETWORK ERROR",
                    "We could not reach the payment server. Confirm you have an active internet connection and try again.");
            }
            catch (JsonException ex)
            {
                Log("ProcessPayment/Json", ex);
                await ShowFailurePopup("UNEXPECTED RESPONSE",
                    "The server responded in a format we did not expect. Please try again or contact support.");
            }
            catch (Exception ex)
            {
                Log("ProcessPayment", ex);
                await ShowFailurePopup("TRANSACTION FAILED",
                    "An unexpected error occurred. If your wallet was debited, verify the transaction in History before retrying.");
            }
            finally
            {
                _isProcessing = false;
                SetLoadingState(false);
            }
        }
        private static async Task<string> SafeReadAsync(HttpResponseMessage response)
        {
            try
            {
                if (response?.Content == null) return string.Empty;
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("SafeRead", ex);
                return string.Empty;
            }
        }

        private static bool IsSuccess(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;

            var normalised = code.Trim();
            return normalised == SuccessCode || normalised == "0" || normalised == "oo";
        }

        private static string TitleForCode(string code)
        {
            switch ((code ?? string.Empty).Trim())
            {
                case "06": return "INSUFFICIENT WALLET BALANCE";
                case "51": return "INSUFFICIENT FUNDS";
                case "55": return "INCORRECT PIN";
                case "12": return "INVALID TRANSACTION";
                case "91": return "SERVICE UNAVAILABLE";
                default: return "TRANSACTION FAILED";
            }
        }

        private static string HintForCode(string code)
        {
            switch ((code ?? string.Empty).Trim())
            {
                case "06":
                case "51":
                    return "The Super Agent wallet funding this collection does not have enough balance. Contact your supervisor or the agency office to top up, then retry.";
                case "55":
                    return "Re-enter your transaction PIN carefully. If you have forgotten it, reset it from your profile.";
                case "91":
                    return "The service is temporarily unavailable. Wait a few minutes and try again.";
                default:
                    return "Verify the details and try again. If your wallet was debited, check History before retrying so you do not pay twice.";
            }
        }

        /// <summary>
        /// Returns responseMessage only when it adds information beyond message,
        /// so the error sheet never shows the same sentence twice.
        /// </summary>
        private static string DifferingDetail(StateCollectionResponseObject resp)
        {
            try
            {
                var message = SafeString(resp?.Message);
                var responseMessage = SafeString(resp?.ResponseMessage);

                if (string.IsNullOrWhiteSpace(responseMessage)) return null;
                if (string.Equals(message, responseMessage, StringComparison.OrdinalIgnoreCase)) return null;

                return responseMessage;
            }
            catch (Exception ex)
            {
                Log("DifferingDetail", ex);
                return null;
            }
        }

        // ───────────────────────── Popups ─────────────────────────

        private Task ShowSuccessPopup(StateCollectionResponseObject resp)
        {
            return Device.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    // Server figure wins; the verified amount is the fallback.
                    var paid = resp.TotalAmount > 0 ? resp.TotalAmount : _expectedAmount;
                    var formatted = "₦" + paid.ToString("N2", CultureInfo.InvariantCulture);

                    PopupAmount.Text = formatted;
                    PopupAmountRow.Text = formatted;

                    PopupRef.Text = FirstNonEmpty(resp.TransactionNo, resp.TId, "N/A");
                    PopupVehicle.Text = FirstNonEmpty(resp.Vehicle, Verify.vehicleTypess, "—");
                    PopupPlate.Text = FirstNonEmpty(resp.VehicleNo, Verify.plateNumberss, "—");
                    PopupLGA.Text = FirstNonEmpty(resp.Destination, _destination, "—");
                    PopupAgent.Text = FirstNonEmpty(resp.AgentName, MainPage.Name, "—");
                    PopupDate.Text = DateTime.Now.ToString("dd MMM yyyy, hh:mm tt");
                    PopupMessage.Text = FirstNonEmpty(resp.Message, resp.ResponseMessage, "Transaction completed successfully.");

                    var paymentRef = SafeString(resp.PaymentReference);
                    PopupPaymentRef.Text = paymentRef;
                    PopupPaymentRefRow.IsVisible = !string.IsNullOrWhiteSpace(paymentRef);

                    var expiry = FormatDate(resp.ExpDate);
                    PopupExpiry.Text = expiry;
                    PopupExpiryRow.IsVisible = !string.IsNullOrWhiteSpace(expiry);

                    PrintingStatusLabel.Text = "🖨️  Sending receipt to printer…";
                    PopupReprintView.IsVisible = false;

                    FailureOverlay.IsVisible = false;
                    SuccessOverlay.IsVisible = true;
                }
                catch (Exception ex)
                {
                    Log("ShowSuccessPopup", ex);
                    Toast("Payment succeeded, but the receipt view could not be rendered.", 5);
                }
            });
        }

        private Task ShowFailurePopup(string title, string message,
            string detail = null, string code = null, string reference = null, decimal? amount = null)
        {
            return Device.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    FailureTitle.Text = string.IsNullOrWhiteSpace(title) ? "TRANSACTION FAILED" : title;
                    FailureMessage.Text = string.IsNullOrWhiteSpace(message)
                        ? "The transaction could not be completed."
                        : message;

                    var attempted = amount ?? _expectedAmount;
                    FailureAmount.Text = "₦" + attempted.ToString("N2", CultureInfo.InvariantCulture);
                    FailureVehicle.Text = $"{SafeString(Verify.plateNumberss, "—")}  •  {SafeString(Verify.vehicleTypess, "—")}";

                    FailureDetail.Text = detail ?? string.Empty;
                    FailureDetailRow.IsVisible = !string.IsNullOrWhiteSpace(detail);

                    var hasCode = !string.IsNullOrWhiteSpace(code);
                    FailureCode.Text = hasCode ? $"RESPONSE CODE {code.Trim()}" : string.Empty;
                    FailureCodeView.IsVisible = hasCode;

                    FailureHint.Text = HintForCode(code);

                    var hasRef = !string.IsNullOrWhiteSpace(reference);
                    FailureRefLabel.Text = hasRef ? $"Ref: {reference}" : string.Empty;
                    FailureRefLabel.IsVisible = hasRef;

                    FailureIcon.Text = IsWalletCode(code) ? "💳" : "❌";

                    SuccessOverlay.IsVisible = false;
                    FailureOverlay.IsVisible = true;
                }
                catch (Exception ex)
                {
                    Log("ShowFailurePopup", ex);
                    Toast(message ?? "Transaction failed.", 6);
                }
            });
        }

        private static bool IsWalletCode(string code)
        {
            var c = (code ?? string.Empty).Trim();
            return c == "06" || c == "51";
        }

        private void SuccessContinue_Tapped(object sender, EventArgs e)
        {
            try
            {
                SuccessOverlay.IsVisible = false;
                _ = Navigation.PushAsync(new Verify());
            }
            catch (Exception ex) { Log("SuccessContinue_Tapped", ex); }
        }

        private void FailureRetry_Tapped(object sender, EventArgs e)
        {
            try
            {
                FailureOverlay.IsVisible = false;
                PIN.Text = string.Empty;
                Validate();
            }
            catch (Exception ex) { Log("FailureRetry_Tapped", ex); }
        }

        private void sheetBehavior_ActionClicked(object sender, EventArgs e)
        {
            try { sheetBehavior.IsOpen = false; }
            catch (Exception ex) { Log("sheetBehavior_ActionClicked", ex); }
        }

        private async void Button_Clicked(object sender, EventArgs e)
        {
            try
            {
                FailureOverlay.IsVisible = false;
                await Navigation.PushAsync(new Views.Verify());
            }
            catch (Exception ex) { Log("Button_Clicked", ex); }
        }

        // ───────────────────────── Receipt ─────────────────────────

        private ReceiptData BuildReceiptData(StateCollectionResponseObject resp, bool isReprint = false)
        {
            try
            {
                var amount = resp != null && resp.TotalAmount > 0 ? resp.TotalAmount : _expectedAmount;
                var reference = FirstNonEmpty(resp?.TransactionNo, resp?.TId, "N/A");

                var verifyUrl = $"https://rickbolwa.osoftpay.net/Singlecollections/Verify?TransactId={Uri.EscapeDataString(reference)}";

                var items = new List<ReceiptItem>
                {
                    new ReceiptItem { Description = "AGENT NAME",      Amount = 0m, SubText = FirstNonEmpty(resp?.AgentName, MainPage.Name, "—") },
                    new ReceiptItem { Description = "VEHICLE TYPE",    Amount = 0m, SubText = FirstNonEmpty(resp?.Vehicle, Verify.vehicleTypess, "—") },
                    new ReceiptItem { Description = "VEHICLE NO", Amount = 0m, SubText = FirstNonEmpty(resp?.VehicleNo, Verify.plateNumberss, "—") },
                    new ReceiptItem { Description = "AMOUNT PAID",     Amount = amount, SubText = "₦" + amount.ToString("N2", CultureInfo.InvariantCulture) }
                };

                var paymentRef = SafeString(resp?.PaymentReference);
                if (!string.IsNullOrWhiteSpace(paymentRef))
                    items.Add(new ReceiptItem { Description = "PAYMENT REF", Amount = 0m, SubText = paymentRef });

                var expiry = FormatDate(resp?.ExpDate);
                if (!string.IsNullOrWhiteSpace(expiry))
                    items.Add(new ReceiptItem { Description = "VALID UNTIL", Amount = 0m, SubText = expiry });

                return new ReceiptData
                {
                    StoreName = App.RevenueServiceName ?? "RICKBOLWA LIMITED",
                    StorePhone = "Contact us: +234 810 046 6363",
                    ReceiptNumber = reference,
                    AgentName = FirstNonEmpty(resp?.AgentName, MainPage.Name, "—"),
                    CollectionPoint = MainPage.CollectionPoint,
                    AmountPaid = amount,
                    PrintDate = DateTime.Now,
                    Items = items,
                    FooterLine1 = isReprint ? "*** REPRINTED RECEIPT ***" : (App.ThankYouMessage ?? "Thank You!"),
                    FooterLine2 = isReprint
                        ? $"Reprinted: {DateTime.Now:dd MMM yyyy HH:mm} | POWERED BY OSOFTPAY"
                        : "POWERED BY OSOFTPAY",
                    BarcodeLabel = verifyUrl
                };
            }
            catch (Exception ex)
            {
                Log("BuildReceiptData", ex);

                return new ReceiptData
                {
                    StoreName = "RICKBOLWA LIMITED",
                    ReceiptNumber = SafeString(resp?.TransactionNo, "N/A"),
                    CollectionPoint = MainPage.CollectionPoint,
                    AmountPaid = resp != null && resp.TotalAmount > 0 ? resp.TotalAmount : _expectedAmount,
                    PrintDate = DateTime.Now,
                    Items = new List<ReceiptItem>(),
                    FooterLine1 = "Thank You!",
                    FooterLine2 = "POWERED BY OSOFTPAY"
                };
            }
        }

        // ───────────────────────── Printing ─────────────────────────

        private async Task AttemptPrintAsync(ReceiptData receipt, bool isReprint)
        {
            if (receipt == null || _isPrinting) return;

            _isPrinting = true;

            try
            {
                bool granted;

                try
                {
                    granted = await BluetoothPermissionHelper.RequestAsync();
                }
                catch (Exception ex)
                {
                    Log("BluetoothPermission", ex);
                    granted = false;
                }

                if (!granted)
                {
                    SetPrintFailed("⚠️  Bluetooth permission denied — tap Reprint after granting it");
                    return;
                }

                var job = await App.PrintJobManager.EnqueueAsync(receipt, logoAssetName: "Logo.png");

                var progress = new Progress<PrintProgress>(p =>
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            switch (p.Status)
                            {
                                case PrintProgressStatus.SessionCompleted:
                                    HideReprintButton();
                                    PopupReprintView.IsVisible = false;
                                    if (SuccessOverlay.IsVisible)
                                        PrintingStatusLabel.Text = "✅  Receipt printed successfully";
                                    Toast(isReprint ? "Receipt reprinted." : "Receipt printed.", 3);
                                    break;

                                case PrintProgressStatus.ChunkFailed:
                                    SetPrintFailed("⚠️  Print failed — tap Reprint to retry");
                                    break;
                            }
                        }
                        catch (Exception ex) { Log("PrintProgress", ex); }
                    }));

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                {
                    try
                    {
                        await App.PrintJobManager.ExecuteAsync(job.JobId, progress, cts.Token);

                        try { await App.PrintJobManager.DeleteJobAsync(job.JobId); }
                        catch (Exception ex) { Log("DeleteJob", ex); }

                        MainThread.BeginInvokeOnMainThread(HideReprintButton);
                    }
                    catch (System.OperationCanceledException)
                    {
                        SetPrintFailed("⚠️  Printer timed out — tap Reprint to retry");
                    }
                    catch (Exception ex)
                    {
                        Log("PrintExecute", ex);
                        SetPrintFailed("⚠️  Print failed — tap Reprint to retry");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("AttemptPrint", ex);
                SetPrintFailed("⚠️  Printer unavailable — tap Reprint to retry");
            }
            finally
            {
                _isPrinting = false;
            }
        }

        private void SetPrintFailed(string message)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    ShowReprintButton();
                    PopupReprintView.IsVisible = SuccessOverlay.IsVisible;

                    if (SuccessOverlay.IsVisible)
                        PrintingStatusLabel.Text = message;
                    else
                        Toast(message, 5);
                }
                catch (Exception ex) { Log("SetPrintFailed", ex); }
            });
        }

        private async void PopupReprint_Tapped(object sender, EventArgs e)
        {
            try
            {
                PrintingStatusLabel.Text = "🖨️  Reprinting…";
                await ReprintAsync();
            }
            catch (Exception ex) { Log("PopupReprint_Tapped", ex); }
        }

        private async void OnReprintClicked(object sender, EventArgs e)
        {
            try
            {
                ReprintButton.IsEnabled = false;
                ReprintButton.Text = "Reprinting…";

                await ReprintAsync();
            }
            catch (Exception ex) { Log("OnReprintClicked", ex); }
            finally
            {
                try
                {
                    ReprintButton.IsEnabled = true;
                    ReprintButton.Text = "REPRINT RECEIPT";
                }
                catch (Exception ex) { Log("OnReprintClicked/reset", ex); }
            }
        }

        private async Task ReprintAsync()
        {
            try
            {
                if (_lastReceiptData == null)
                {
                    Toast("No receipt data available to reprint.", 4);
                    return;
                }

                var reprint = new ReceiptData
                {
                    StoreName = _lastReceiptData.StoreName,
                    StorePhone = _lastReceiptData.StorePhone,
                    ReceiptNumber = _lastReceiptData.ReceiptNumber,
                    AgentName = _lastReceiptData.AgentName,
                    CollectionPoint = _lastReceiptData.CollectionPoint,
                    PrintDate = DateTime.Now,
                    Items = _lastReceiptData.Items,
                    AmountPaid = _lastReceiptData.AmountPaid,
                    BarcodeLabel = _lastReceiptData.BarcodeLabel,
                    FooterLine1 = "*** REPRINTED RECEIPT ***",
                    FooterLine2 = $"Reprinted: {DateTime.Now:dd MMM yyyy HH:mm} | POWERED BY OSOFTPAY"
                };

                await AttemptPrintAsync(reprint, isReprint: true);
            }
            catch (Exception ex)
            {
                Log("ReprintAsync", ex);
                Toast("Reprint could not be started.", 4);
            }
        }

        private void ShowReprintButton() { try { ReprintButtonView.IsVisible = true; } catch (Exception ex) { Log("ShowReprint", ex); } }
        private void HideReprintButton() { try { ReprintButtonView.IsVisible = false; } catch (Exception ex) { Log("HideReprint", ex); } }


        // ───────────────────────── Helpers ─────────────────────────
        private static decimal ParseAmount(object raw)
        {
            try
            {
                if (raw == null) return 0m;

                if (raw is decimal dec) return dec;
                if (raw is double dbl) return (decimal)dbl;
                if (raw is int i) return i;
                if (raw is long l) return l;

                var text = raw.ToString().Replace(",", "").Replace("₦", "").Trim();

                return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;
            }
            catch (Exception ex)
            {
                Log("ParseAmount", ex);
                return 0m;
            }
        }

        private static string FormatDate(string raw)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                    return utc.ToLocalTime().ToString("dd MMM yyyy, hh:mm tt");

                return DateTime.TryParse(raw, out var local)
                    ? local.ToString("dd MMM yyyy, hh:mm tt")
                    : raw;
            }
            catch (Exception ex)
            {
                Log("FormatDate", ex);
                return string.Empty;
            }
        }

        private static string FirstNonEmpty(params string[] candidates)
        {
            try
            {
                foreach (var candidate in candidates ?? new string[0])
                    if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
            }
            catch (Exception ex) { Log("FirstNonEmpty", ex); }

            return string.Empty;
        }

        private static string SafeString(string value, string fallback = "")
            => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static string SafeText(InputView input)
        {
            try { return input?.Text?.Trim() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static void Toast(string message, int seconds = 3)
        {
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try { UserDialogs.Instance.Toast(message, TimeSpan.FromSeconds(seconds)); }
                    catch (Exception ex) { Log("Toast/inner", ex); }
                });
            }
            catch (Exception ex) { Log("Toast", ex); }
        }

        private static void Log(string scope, Exception ex)
            => System.Diagnostics.Debug.WriteLine($"[HaulagePayment:{scope}] {ex?.GetType().Name}: {ex?.Message}");
    }

    internal class GetSateCatData { public string name { get; set; } }

    /// <summary>
    /// Raw JSON request body for /Api/SingleCollections/Hulage.
    /// </summary>
    internal class HaulagePaymentRequest
    {
        [JsonProperty("payer")] public string Payer { get; set; }
        [JsonProperty("email")] public string Email { get; set; }
        [JsonProperty("pin")] public string Pin { get; set; }
        [JsonProperty("serviceName")] public string ServiceName { get; set; }
        [JsonProperty("vehicleNo")] public string VehicleNo { get; set; }
        [JsonProperty("stateTo")] public string StateTo { get; set; }
    }

    /// <summary>
    /// Mirrors the camelCase JSON contract returned by /Api/SingleCollections/Hulage.
    /// </summary>
    internal class StateCollectionResponseObject
    {
        [JsonProperty("respondCode")] public string RespondCode { get; set; }
        [JsonProperty("transactionNo")] public string TransactionNo { get; set; }
        [JsonProperty("department")] public string Department { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("responseMessage")] public string ResponseMessage { get; set; }
        [JsonProperty("noofDaysPaid")] public int? NoofDaysPaid { get; set; }
        [JsonProperty("expDate")] public string ExpDate { get; set; }
        [JsonProperty("payerId")] public string PayerId { get; set; }
        [JsonProperty("totalAmount")] public decimal TotalAmount { get; set; }
        [JsonProperty("breakdown")] public string Breakdown { get; set; }
        [JsonProperty("vehicleNo")] public string VehicleNo { get; set; }
        [JsonProperty("marketName")] public string MarketName { get; set; }
        [JsonProperty("shopOwner")] public string ShopOwner { get; set; }
        [JsonProperty("tId")] public string TId { get; set; }
        [JsonProperty("paymentMethod")] public string PaymentMethod { get; set; }
        [JsonProperty("paymentReference")] public string PaymentReference { get; set; }
        [JsonProperty("agentName")] public string AgentName { get; set; }
        [JsonProperty("vehicle")] public string Vehicle { get; set; }
        [JsonProperty("destination")] public string Destination { get; set; }
        [JsonProperty("differences")] public decimal Differences { get; set; }
        [JsonProperty("noofSeat")] public int NoofSeat { get; set; }
        [JsonProperty("amountRemitted")] public decimal AmountRemitted { get; set; }

        // Retained so older code paths referencing the nested object still compile.
        [JsonProperty("addSinglecollect")] public AddSinglecollect addSinglecollect { get; set; }
        [JsonProperty("printCode")] public string PrintCode { get; set; }
    }

    internal class AddSinglecollect
    {
        [JsonProperty("transactionNo")] public string TransactionNo { get; set; }
    }
}