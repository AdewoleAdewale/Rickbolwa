using Acr.UserDialogs;
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

        // Same state list the Borno haulage payment loads into its picker.
        private const string StateUrl = BaseUrl + "/api/taskpayers/getstate";

        private static readonly string[] FallbackStates =
        {
            "Abia","Adamawa","Akwa Ibom","Anambra","Bauchi","Bayelsa","Benue","Borno","Cross River",
            "Delta","Ebonyi","Edo","Ekiti","Enugu","FCT","Gombe","Imo","Jigawa","Kaduna","Kano",
            "Katsina","Kebbi","Kogi","Kwara","Lagos","Nasarawa","Niger","Ogun","Ondo","Osun","Oyo",
            "Plateau","Rivers","Sokoto","Taraba","Yobe","Zamfara"
        };

        private List<GetSateCatData> _states = new List<GetSateCatData>();
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
                _ = LoadStatesAsync();
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
        /// Service name, plate and amount come from the verified vehicle record. The destination
        /// state is picked here (as in the Borno haulage payment). If the verified amount is zero
        /// the Amount field becomes editable, again matching Borno.
        /// </summary>
        private void LoadVerifiedDetails()
        {
            try
            {
                Servicename.Text = SafeString(Verify.vehicleTypess, "—");
                PayerId.Text = SafeString(Verify.plateNumberss, "—");

                _expectedAmount = ParseAmount(Verify.amountss);
                Amount.Text = _expectedAmount > 0
                    ? _expectedAmount.ToString("N2", CultureInfo.InvariantCulture)
                    : SafeString(Verify.amountss, "0");

                Amount.IsReadOnly = _expectedAmount > 0;
                Amount.IsEnabled = true;

                UpdateAmountHeadline();
            }
            catch (Exception ex)
            {
                Log("LoadVerifiedDetails", ex);
            }
        }

        private async Task LoadStatesAsync()
        {
            List<GetSateCatData> items = null;

            try
            {
                using (var response = await Http.GetAsync(StateUrl).ConfigureAwait(true))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await SafeReadAsync(response).ConfigureAwait(true);
                        if (!string.IsNullOrWhiteSpace(json))
                            items = JsonConvert.DeserializeObject<List<GetSateCatData>>(json,
                                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    }
                }
            }
            catch (Exception ex)
            {
                Log("LoadStates", ex);
            }

            // If the endpoint is missing or empty, fall back to the standard Nigerian state list
            // so the operator is never blocked from taking payment.
            if (items == null || items.Count == 0 || items.All(i => string.IsNullOrWhiteSpace(i?.name)))
                items = FallbackStates.Select(n => new GetSateCatData { name = n }).ToList();

            _states = items.Where(i => !string.IsNullOrWhiteSpace(i?.name)).ToList();

            try
            {
                picker.ItemDisplayBinding = new Binding("name");
                picker.ItemsSource = _states;
            }
            catch (Exception ex)
            {
                Log("BindStates", ex);
            }

            Validate();
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            try
            {
                var selected = picker.SelectedItem as GetSateCatData;
                _destination = selected?.name?.Trim() ?? string.Empty;

                var ok = !string.IsNullOrWhiteSpace(_destination);
                destinationBadge.Text = ok ? "✓ SELECTED" : "REQUIRED";
                destinationBadge.TextColor = ok ? Color.FromHex("#00893E") : Color.FromHex("#FF6B6B");
                destinationError.IsVisible = false;

                Validate();
            }
            catch (Exception ex) { Log("OnStateChanged", ex); }
        }

        // ───────────────────────── Networking setup ─────────────────────────

        private static HttpClient CreateHttpClient()
        {
            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (m, c, ch, er) => ApiClient.ValidateCertificate(m, c, ch, er)
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
                ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, er) => ApiClient.ValidateCertificate(s, c, ch, er);
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
            try { UpdateAmountHeadline(); Validate(); }
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
                var amountOk = ParseAmount(SafeText(Amount)) > 0;

                _isFormValid = pinOk && destOk && amountOk;

                makepaymentbutton.BackgroundGradientStartColor = _isFormValid ? Color.FromHex("#004225") : Color.FromHex("#B9C7BF");
                makepaymentbutton.BackgroundGradientEndColor = _isFormValid ? Color.FromHex("#00AA55") : Color.FromHex("#9FB0A6");
                makepaymentbutton.Opacity = _isFormValid ? 1 : 0.65;
                makepaymentbutton.InputTransparent = !_isFormValid || _isProcessing;

                payIcon.Text = _isFormValid ? "+" : "🔒";

                if (!_isProcessing)
                {
                    payLabel.Text = _isFormValid
                        ? "PROCESS PAYMENT"
                        : (!destOk ? "SELECT DESTINATION" : (!amountOk ? "ENTER AMOUNT" : "ENTER PIN TO CONTINUE"));
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
                payLabel.Text = loading ? "PROCESSING…" : (_isFormValid ? "PROCESS PAYMENT" : "COMPLETE ALL FIELDS");

                makepaymentbutton.InputTransparent = loading || !_isFormValid;
                PIN.IsEnabled = !loading;
                picker.IsEnabled = !loading;
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
                    await ShowFailurePopup("DESTINATION REQUIRED",
                        "Select the destination state before processing the payment.");
                    return;
                }

                var amount = ParseAmount(SafeText(Amount));

                if (amount <= 0)
                {
                    await ShowFailurePopup("INVALID AMOUNT", "Zero is an invalid amount to process.");
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

                var email = SafeString(MainPage.ValidUserMail);

                if (string.IsNullOrWhiteSpace(email))
                {
                    await ShowFailurePopup("SESSION EXPIRED",
                        "Your login session is no longer valid. Please log out and sign in again.");
                    return;
                }

                _isProcessing = true;
                SetLoadingState(true);

                var plate = SafeString(Verify.plateNumberss);

                // Same form-urlencoded contract as the Borno haulage payment.
                var form = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("ServiceName", SafeString(Verify.vehicleTypess)),
                    new KeyValuePair<string, string>("Email", email),
                    new KeyValuePair<string, string>("Amount", amount.ToString("0.##", CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("Pin", pin),
                    new KeyValuePair<string, string>("Payer", plate),
                    new KeyValuePair<string, string>("StateTo", _destination)
                };

                System.Diagnostics.Debug.WriteLine(
                    "[HaulagePayment] Request: " + string.Join("&", form.Where(kv => kv.Key != "Pin").Select(kv => kv.Key + "=" + kv.Value)));

                string body;
                int statusCode;

                using (var content = new FormUrlEncodedContent(form))
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
                        resp = JsonConvert.DeserializeObject<StateCollectionResponseObject>(body,
                            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
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
                    var receipt = BuildReceiptData(resp, amount);
                    _lastReceiptData = receipt;

                    await ShowSuccessPopup(resp, amount);

                    // Fire and forget — printing must never block or crash the payment flow.
                    _ = AttemptPrintAsync(receipt, isReprint: false);
                }
                else
                {
                    await ShowFailurePopup(TitleForCode(resp.RespondCode),
                        FirstNonEmpty(resp.Message,
                            $"The transaction was declined (code {SafeString(resp.RespondCode, "—")})."),
                        code: resp.RespondCode,
                        reference: resp.TransactionNo,
                        amount: amount);
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

            // Borno haulage payment treats only "00" as success.
            return code.Trim() == SuccessCode;
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

        // ───────────────────────── Popups ─────────────────────────

        private Task ShowSuccessPopup(StateCollectionResponseObject resp, decimal amount)
        {
            return Device.InvokeOnMainThreadAsync(() =>
            {
                try
                {
                    var paid = amount;
                    var formatted = "₦" + paid.ToString("N2", CultureInfo.InvariantCulture);

                    PopupAmount.Text = formatted;
                    PopupAmountRow.Text = formatted;

                    PopupRef.Text = FirstNonEmpty(resp.TransactionNo, resp.addSinglecollect?.TransactionNo, "N/A");
                    PopupVehicle.Text = FirstNonEmpty(Verify.vehicleTypess, "—");
                    PopupPlate.Text = FirstNonEmpty(Verify.plateNumberss, "—");
                    PopupLGA.Text = FirstNonEmpty(_destination, "—");
                    PopupAgent.Text = FirstNonEmpty(MainPage.Name, "—");
                    PopupDate.Text = DateTime.Now.ToString("dd MMM yyyy, hh:mm tt");
                    PopupMessage.Text = FirstNonEmpty(resp.Message, "Transaction completed successfully.");

                    // The Borno response carries no payment reference or expiry date.
                    PopupPaymentRefRow.IsVisible = false;
                    PopupExpiryRow.IsVisible = false;

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

        private ReceiptData BuildReceiptData(StateCollectionResponseObject resp, decimal amount, bool isReprint = false)
        {
            try
            {
                var reference = FirstNonEmpty(resp?.TransactionNo, resp?.addSinglecollect?.TransactionNo, "N/A");

                var verifyUrl = $"https://rickbolwa.osoftpay.net/Singlecollections/Verify?TransactId={Uri.EscapeDataString(reference)}";

                var items = new List<ReceiptItem>
                {
                    new ReceiptItem { Description = "AGENT NAME",   Amount = 0m, SubText = FirstNonEmpty(MainPage.Name, "—") },
                    new ReceiptItem { Description = "VEHICLE TYPE", Amount = 0m, SubText = FirstNonEmpty(Verify.vehicleTypess, "—") },
                    new ReceiptItem { Description = "VEHICLE NO",   Amount = 0m, SubText = FirstNonEmpty(Verify.plateNumberss, "—") },
                    new ReceiptItem { Description = "DESTINATION",  Amount = 0m, SubText = FirstNonEmpty(_destination, "—") },
                    new ReceiptItem { Description = "AMOUNT PAID",  Amount = amount, SubText = "₦" + amount.ToString("N2", CultureInfo.InvariantCulture) }
                };

                return new ReceiptData
                {
                    StoreName = App.RevenueServiceName ?? "RICKBOLWA LIMITED",
                    StorePhone = "Contact us: +234 810 046 6363",
                    ReceiptNumber = reference,
                    AgentName = FirstNonEmpty(MainPage.Name, "—"),
                    CollectionPoint = MainPage.CollectionPoint,
                    TotalAmount = amount,
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
                    TotalAmount = amount,
                    AmountPaid = amount,
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
                    TotalAmount = _lastReceiptData.TotalAmount,
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
    /// Response of /api/SingleCollections/Hulage — same shape the Borno haulage module reads.
    /// Json.NET matching is case-insensitive, so the PascalCase names bind to camelCase JSON.
    /// </summary>
    internal class StateCollectionResponseObject
    {
        public string RespondCode { get; set; }
        public string Message { get; set; }
        public AddSinglecollect addSinglecollect { get; set; }
        public string PrintCode { get; set; }
        public string TransactionNo { get; set; }
    }

    internal class AddSinglecollect
    {
        public string TransactionNo { get; set; }
    }
}