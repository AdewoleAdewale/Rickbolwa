using Acr.UserDialogs;
using DIPS.Xamarin.UI.Controls.Sheet;
using Newtonsoft.Json;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class Registeration : ContentPage
    {
        // ───────────────────────── Endpoints (same contract as the Borno haulage module) ─────────────────────────
        private const string BaseUrl = "https://rickbolwa.osoftpay.net";
        private const string VehicleTypeUrl = BaseUrl + "/api/HulageVehicles/VehicleType";
        private const string LgaUrl = BaseUrl + "/api/HulageVehicles/getlga";
        private const string RegisterUrl = BaseUrl + "/api/HulageVehicles/VehicleReg";

        // Driver name, plate number, owner phone, vehicle category, LGA.
        private const int TotalRequiredFields = 5;

        // Null values from the API must not break deserialisation of the whole list.
        private static readonly JsonSerializerSettings LenientJson = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        // Single shared client — avoids socket exhaustion from per-call HttpClient instances.
        private static readonly HttpClient Http = CreateHttpClient();

        private List<VehicleCatData> _vehicleTypes = new List<VehicleCatData>();
        private List<LGACatData> _lgas = new List<LGACatData>();

        private readonly RegistrationResult _result = new RegistrationResult();

        private bool _referenceDataLoaded;
        private bool _isSubmitting;
        private bool _isFormValid;

        public Registeration()
        {
            InitializeComponent();

            try
            {
                BindingContext = _result;
                sheetBehavior.IsOpen = false;
                failedenumeration.IsOpen = false;
                ConfigureSsl();
                TrackUserActivity();
                Validate(showErrors: false);
            }
            catch (Exception ex)
            {
                Log("ctor", ex);
            }
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();

            try
            {
                if (!_referenceDataLoaded || _lgas.Count == 0 || _vehicleTypes.Count == 0)
                    _ = LoadReferenceDataAsync();
            }
            catch (Exception ex)
            {
                Log("OnAppearing", ex);
            }
        }

        // ───────────────────────── Networking setup ─────────────────────────

        private static HttpClient CreateHttpClient()
        {
            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        (m, c, ch, er) => ApiClient.ValidateCertificate(m, c, ch, er)
                };

                try
                {
                    handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                         | System.Security.Authentication.SslProtocols.Tls11;
                }
                catch (Exception ex)
                {
                    // Some Android handlers do not allow explicit protocol selection.
                    Log("SslProtocols", ex);
                }

                return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            }
            catch (Exception ex)
            {
                Log("CreateHttpClient", ex);
                return new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            }
        }

        private void ConfigureSsl()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
                ServicePointManager.ServerCertificateValidationCallback =
                    (s, cert, chain, errors) => ApiClient.ValidateCertificate(s, cert, chain, errors);
                ServicePointManager.DefaultConnectionLimit = 10;
                ServicePointManager.Expect100Continue = false;
            }
            catch (Exception ex)
            {
                Log("ConfigureSsl", ex);
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
                    "RICKBOLWA", "Logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

                string logFile = Path.Combine(logDir, $"error_log_{DateTime.Now:yyyy-MM-dd}.txt");
                File.AppendAllText(logFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {method}: {ex?.Message}\n{ex?.StackTrace}\n\n");
            }
            catch { }
        }


        // ───────────────────────── Reference data ─────────────────────────

        private async Task LoadReferenceDataAsync()
        {
            var busyShown = false;

            try
            {
                try { UserDialogs.Instance.ShowLoading("Loading form data…"); busyShown = true; }
                catch (Exception ex) { Log("ShowLoading", ex); }

                var vehicleTask = GetAsync<List<VehicleCatData>>(VehicleTypeUrl);
                var lgaTask = GetAsync<List<LGACatData>>(LgaUrl);

                await Task.WhenAll(vehicleTask, lgaTask).ConfigureAwait(true);

                _vehicleTypes = vehicleTask.Result ?? new List<VehicleCatData>();
                _lgas = lgaTask.Result ?? new List<LGACatData>();

                BindPickers();

                _referenceDataLoaded = _vehicleTypes.Count > 0 && _lgas.Count > 0;

                if (!_referenceDataLoaded)
                    Toast("Some form data could not be loaded. Pull back and re-open the page to retry.", 5);
            }
            catch (Exception ex)
            {
                Log("LoadReferenceDataAsync", ex);
                Toast("Unable to load form data. Check your internet connection.", 5);
            }
            finally
            {
                if (busyShown)
                {
                    try { UserDialogs.Instance.HideLoading(); } catch (Exception ex) { Log("HideLoading", ex); }
                }

                Validate(showErrors: false);
            }
        }

        private void BindPickers()
        {
            try
            {
                picker.ItemDisplayBinding = new Binding("vehicleType");
                picker.ItemsSource = _vehicleTypes.ToList();

                picker2.ItemDisplayBinding = new Binding("lgaName");
                picker2.ItemsSource = _lgas.ToList();
            }
            catch (Exception ex)
            {
                Log("BindPickers", ex);
            }
        }

        private static async Task<T> GetAsync<T>(string url) where T : class
        {
            try
            {
                using (var response = await Http.GetAsync(url).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Log("GetAsync", new Exception($"{url} → {(int)response.StatusCode}"));
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json)) return null;

                    return JsonConvert.DeserializeObject<T>(json, LenientJson);
                }
            }
            catch (Exception ex)
            {
                Log($"GetAsync({url})", ex);
                return null;
            }
        }

        // ───────────────────────── Live validation ─────────────────────────

        private void OnFieldChanged(object sender, TextChangedEventArgs e)
        {
            try { Validate(showErrors: true); }
            catch (Exception ex) { Log("OnFieldChanged", ex); }
        }

        private void OnPickerChanged(object sender, EventArgs e)
        {
            try
            {
                UpdateFeePreview();
                Validate(showErrors: true);
            }
            catch (Exception ex) { Log("OnPickerChanged", ex); }
        }

        /// <summary>
        /// Single source of truth for form state. Recomputes every field, refreshes the
        /// badges/error labels, the progress tracker and the enabled state of the submit button.
        /// </summary>
        private void Validate(bool showErrors)
        {
            try
            {
                var driver = SafeText(drivername);
                var plate = SafeText(Platenumber);
                var phone = SafeText(Ownernumber);

                var vehicle = picker?.SelectedItem as VehicleCatData;
                var lga = picker2?.SelectedItem as LGACatData;

                var driverOk = driver.Length >= 3;
                var plateOk = plate.Replace(" ", "").Replace("-", "").Length >= 5;
                var phoneOk = phone.Length >= 10 && phone.Length <= 11 && phone.All(char.IsDigit);
                var vehicleOk = vehicle != null && !string.IsNullOrWhiteSpace(vehicle.vehicleType);
                var lgaOk = lga != null && !string.IsNullOrWhiteSpace(lga.lgaName);

                SetFieldState(driverBadge, driverError, driverOk, showErrors && driver.Length > 0,
                    "Enter the driver's full name (minimum 3 characters).");
                SetFieldState(plateBadge, plateError, plateOk, showErrors && plate.Length > 0,
                    "Plate number looks too short. e.g. ABC124XA");
                SetFieldState(phoneBadge, phoneError, phoneOk, showErrors && phone.Length > 0,
                    "Phone number must be 10–11 digits, numbers only.");
                SetFieldState(vehicleBadge, vehicleError, vehicleOk, false, string.Empty);
                SetFieldState(lgaBadge, lgaError, lgaOk, false, string.Empty);

                var completed = new[] { driverOk, plateOk, phoneOk, vehicleOk, lgaOk }.Count(v => v);
                _isFormValid = completed == TotalRequiredFields;

                UpdateSubmitState(completed);
            }
            catch (Exception ex)
            {
                Log("Validate", ex);
            }
        }

        private void SetFieldState(Label badge, Label error, bool isValid, bool showError, string message)
        {
            try
            {
                if (badge != null)
                {
                    badge.Text = isValid ? "✓ VALID" : "REQUIRED";
                    badge.TextColor = isValid ? Color.FromHex("#00893E") : Color.FromHex("#FF6B6B");
                }

                if (error != null)
                {
                    error.Text = message ?? string.Empty;
                    error.IsVisible = !isValid && showError && !string.IsNullOrEmpty(message);
                }
            }
            catch (Exception ex) { Log("SetFieldState", ex); }
        }

        private void UpdateSubmitState(int completed, bool animate = true)
        {
            try
            {
                if (progressLabel != null)
                    progressLabel.Text = $"{completed} OF {TotalRequiredFields} COMPLETED";

                if (formProgress != null)
                {
                    var value = (double)completed / TotalRequiredFields;
                    if (animate) _ = formProgress.ProgressTo(value, 220, Easing.CubicOut);
                    else formProgress.Progress = value;
                }

                if (statusHint != null)
                {
                    statusHint.Text = _isFormValid
                        ? "All required details captured. You can submit now."
                        : $"{TotalRequiredFields - completed} field(s) still required before submission.";
                    statusHint.TextColor = _isFormValid ? Color.FromHex("#00893E") : Color.FromHex("#6B7B72");
                }

                if (submitButton != null)
                {
                    submitButton.BackgroundGradientStartColor = _isFormValid ? Color.FromHex("#004225") : Color.FromHex("#B9C7BF");
                    submitButton.BackgroundGradientEndColor = _isFormValid ? Color.FromHex("#00AA55") : Color.FromHex("#9FB0A6");
                    submitButton.Opacity = _isFormValid ? 1 : 0.65;
                    submitButton.InputTransparent = !_isFormValid || _isSubmitting;
                }

                if (submitIcon != null) submitIcon.Text = _isFormValid ? "+" : "🔒";
                if (submitLabel != null && !_isSubmitting)
                    submitLabel.Text = _isFormValid ? "REGISTER VEHICLE" : "COMPLETE ALL FIELDS";
            }
            catch (Exception ex) { Log("UpdateSubmitState", ex); }
        }

        private void UpdateFeePreview()
        {
            try
            {
                var vehicle = picker?.SelectedItem as VehicleCatData;

                if (vehicle == null)
                {
                    feeLabel.Text = "—";
                    feeSubLabel.Text = "Select a vehicle category";
                    return;
                }

                var fee = ParseAmount(vehicle.amount);

                if (fee > 0m)
                {
                    feeLabel.Text = "₦" + fee.ToString("N2", CultureInfo.InvariantCulture);
                    feeSubLabel.Text = vehicle.vehicleType;
                }
                else
                {
                    // The vehicle-type list did not carry a fee — it is confirmed at verification/payment.
                    feeLabel.Text = "—";
                    feeSubLabel.Text = $"{vehicle.vehicleType} • fee confirmed at payment";
                }
            }
            catch (Exception ex) { Log("UpdateFeePreview", ex); }
        }

        // ───────────────────────── Submit ─────────────────────────

        private async void TapGestureRecognizer_Tapped(object sender, EventArgs e)
        {
            try
            {
                if (_isSubmitting) return;

                SessionManager.Instance.UpdateActivity();
                Validate(showErrors: true);

                if (!_isFormValid)
                {
                    Toast(FirstMissingFieldMessage(), 4);
                    return;
                }

                var recordedBy = (MainPage.ValidUserMail ?? string.Empty).Trim();

                if (recordedBy.Length == 0)
                {
                    ShowFailure("SESSION EXPIRED",
                        "Your login session is no longer valid. Please log out and sign in again before registering a vehicle.");
                    return;
                }

                var vehicle = picker.SelectedItem as VehicleCatData;
                var lga = picker2.SelectedItem as LGACatData;

                var payload = new VechicleRegistrationObject
                {
                    NameofDriver = SafeText(drivername),
                    PlateNumber = SafeText(Platenumber).ToUpperInvariant().Replace(" ", ""),
                    OwnerPhone = SafeText(Ownernumber),
                    LGA = lga?.lgaName ?? string.Empty,
                    VehicleType = vehicle?.vehicleType ?? string.Empty,
                    RecordedBy = recordedBy
                };

                // Same form-urlencoded contract the Borno haulage registration uses.
                var form = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("NameofDriver", payload.NameofDriver),
                    new KeyValuePair<string, string>("PlateNumber", payload.PlateNumber),
                    new KeyValuePair<string, string>("OwnerPhone", payload.OwnerPhone),
                    new KeyValuePair<string, string>("LGA", payload.LGA),
                    new KeyValuePair<string, string>("VehicleType", payload.VehicleType),
                    new KeyValuePair<string, string>("RecordedBy", payload.RecordedBy)
                };

                SetSubmitting(true);

                System.Diagnostics.Debug.WriteLine(
                    "[Reg] Request: " + string.Join("&", form.Select(kv => kv.Key + "=" + kv.Value)));

                using (var content = new FormUrlEncodedContent(form))
                using (var response = await Http.PostAsync(RegisterUrl, content).ConfigureAwait(true))
                {
                    var body = string.Empty;

                    try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(true); }
                    catch (Exception ex) { Log("ReadResponse", ex); }

                    System.Diagnostics.Debug.WriteLine($"[Reg] Response ({(int)response.StatusCode}): {body}");

                    VechicleRegisterationResponse parsed = null;

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(body))
                            parsed = JsonConvert.DeserializeObject<VechicleRegisterationResponse>(body, LenientJson);
                    }
                    catch (Exception ex)
                    {
                        Log("DeserializeResponse", ex);
                    }

                    if (response.IsSuccessStatusCode && parsed != null && IsSuccessCode(parsed.statusCode))
                        ShowSuccess(parsed, payload);
                    else
                        ShowFailure(
                            title: "REGISTRATION FAILED",
                            message: !string.IsNullOrWhiteSpace(parsed?.message)
                                ? parsed.message
                                : $"The server rejected the request (HTTP {(int)response.StatusCode}). Please confirm the plate number is not already registered and try again.");
                }
            }
            catch (TaskCanceledException)
            {
                ShowFailure("REQUEST TIMED OUT",
                    "The server did not respond in time. Check your internet connection and try again.");
            }
            catch (HttpRequestException ex)
            {
                Log("Submit/Http", ex);
                ShowFailure("NETWORK ERROR",
                    "We could not reach the registration server. Confirm you have an active internet connection.");
            }
            catch (JsonException ex)
            {
                Log("Submit/Json", ex);
                ShowFailure("UNEXPECTED RESPONSE",
                    "The server returned data in an unexpected format. Please contact support if this persists.");
            }
            catch (Exception ex)
            {
                Log("Submit", ex);
                ShowFailure("SOMETHING WENT WRONG",
                    "An unexpected error occurred while registering the vehicle. Please try again.");
            }
            finally
            {
                SetSubmitting(false);
            }
        }

        private void SetSubmitting(bool busy)
        {
            try
            {
                _isSubmitting = busy;

                if (submitBusy != null) { submitBusy.IsVisible = busy; submitBusy.IsRunning = busy; }
                if (submitArrow != null) submitArrow.IsVisible = !busy;
                if (submitLabel != null)
                    submitLabel.Text = busy ? "SUBMITTING…" : (_isFormValid ? "REGISTER VEHICLE" : "COMPLETE ALL FIELDS");
                if (submitButton != null) submitButton.InputTransparent = busy || !_isFormValid;
            }
            catch (Exception ex) { Log("SetSubmitting", ex); }
        }

        private static bool IsSuccessCode(string statusCode)
        {
            if (string.IsNullOrWhiteSpace(statusCode)) return false;

            // The Borno haulage API answers "oo" (letters) on a successful registration;
            // "00" / "0" are accepted too in case the server is corrected later.
            var code = statusCode.Trim().ToLowerInvariant();
            return code == "00" || code == "0" || code == "oo";
        }

        private string FirstMissingFieldMessage()
        {
            try
            {
                if (SafeText(drivername).Length < 3) return "Enter the driver's full name.";
                if (SafeText(Platenumber).Length < 5) return "Enter a valid plate number.";
                var phone = SafeText(Ownernumber);
                if (phone.Length < 10 || phone.Length > 11 || !phone.All(char.IsDigit))
                    return "Enter a valid 10–11 digit phone number.";
                if (!(picker?.SelectedItem is VehicleCatData)) return "Select a vehicle category.";
                if (!(picker2?.SelectedItem is LGACatData)) return "Select the Local Government Area.";
            }
            catch (Exception ex) { Log("FirstMissingFieldMessage", ex); }

            return "Please complete all required fields.";
        }

        // ───────────────────────── Result presentation ─────────────────────────

        private void ShowSuccess(VechicleRegisterationResponse response, VechicleRegistrationObject request)
        {
            try
            {
                _result.DriverName = Fallback(response.nameofDriver, request.NameofDriver);
                _result.PlateNumber = Fallback(response.plateNumber, request.PlateNumber);
                _result.OwnerPhone = Fallback(response.ownerPhone, request.OwnerPhone);
                _result.VehicleType = Fallback(response.vehicleType, request.VehicleType);
                _result.RecordedBy = Fallback(response.recordedBy, request.RecordedBy);
                _result.Message = Fallback(response.message, "The vehicle has been registered successfully.");

                var lga = Fallback(response.lga, request.LGA);
                var state = Fallback(response.state, string.Empty);
                _result.LgaState = string.IsNullOrWhiteSpace(state) ? lga : $"{lga}, {state}";

                _result.Amount = FormatAmount(Fallback(response.amount, response.totalAmount));
                _result.DateRecorded = FormatDate(response.dateRecorded);
                PublishToVerifyContext(response, request);

                Device.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        failedenumeration.IsOpen = false;
                        sheetBehavior.IsOpen = true;
                    }
                    catch (Exception ex) { Log("ShowSuccess/Sheet", ex); }
                });
            }
            catch (Exception ex)
            {
                Log("ShowSuccess", ex);
                Toast("Vehicle registered, but the receipt could not be displayed.", 4);
            }
        }

        private void ShowFailure(string title, string message)
        {
            try
            {
                _result.ErrorTitle = string.IsNullOrWhiteSpace(title) ? "REGISTRATION FAILED" : title;
                _result.ErrorMessage = string.IsNullOrWhiteSpace(message)
                    ? "The registration could not be completed. Please verify your details and try again."
                    : message;

                Device.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        sheetBehavior.IsOpen = false;
                        failedenumeration.IsOpen = true;
                    }
                    catch (Exception ex) { Log("ShowFailure/Sheet", ex); }
                });
            }
            catch (Exception ex) { Log("ShowFailure", ex); }
        }

        private static decimal ParseAmount(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0m;

            var cleaned = raw.Replace(",", "").Replace("₦", "").Trim();

            return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m;
        }

        private static string FormatAmount(string raw)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

                var cleaned = raw.Replace(",", "").Replace("₦", "").Trim();

                if (decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                    return "₦" + value.ToString("N2", CultureInfo.InvariantCulture);

                return "₦" + raw;
            }
            catch (Exception ex)
            {
                Log("FormatAmount", ex);
                return string.Empty;
            }
        }

        private static string FormatDate(string raw)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return DateTime.Now.ToString("dd MMM yyyy, hh:mm tt");

                if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                    return utc.ToLocalTime().ToString("dd MMM yyyy, hh:mm tt");

                if (DateTime.TryParse(raw, out var local))
                    return local.ToString("dd MMM yyyy, hh:mm tt");

                return raw;
            }
            catch (Exception ex)
            {
                Log("FormatDate", ex);
                return raw ?? string.Empty;
            }
        }

        // ───────────────────────── Sheet callbacks ─────────────────────────

        private void sheetBehavior_ActionClicked(object sender, EventArgs e)
        {
            try { sheetBehavior.IsOpen = false; }
            catch (Exception ex) { Log("sheetBehavior_ActionClicked", ex); }
        }

        private void failedenumeration_ActionClicked(object sender, EventArgs e)
        {
            try { failedenumeration.IsOpen = false; }
            catch (Exception ex) { Log("failedenumeration_ActionClicked", ex); }
        }

        /// <summary>
        /// Resets the form in place instead of pushing a new page — avoids stacking pages
        /// (and re-downloading reference data) every time the operator registers another vehicle.
        /// </summary>
        private async void tryagain_Clicked(object sender, EventArgs e)
        {
            try
            {
                sheetBehavior.IsOpen = false;
                failedenumeration.IsOpen = false;

                await Task.Delay(250);

                drivername.Text = string.Empty;
                Platenumber.Text = string.Empty;
                Ownernumber.Text = string.Empty;

                picker.SelectedIndex = -1;
                picker2.SelectedIndex = -1;

                UpdateFeePreview();
                Validate(showErrors: false);

                SessionManager.Instance.UpdateActivity();
                Toast("Form cleared. You can register another vehicle.", 3);
            }
            catch (Exception ex)
            {
                Log("tryagain_Clicked", ex);
            }
        }

        // ───────────────────────── Helpers ─────────────────────────
        /// <summary>
        /// Copies the registration result into the Verify static context. Payments reads
        /// exclusively from these fields, so this is what makes "register → pay" work
        /// without altering the Verify or Payments modules.
        /// </summary>
        private static void PublishToVerifyContext(VechicleRegisterationResponse response,
                                                   VechicleRegistrationObject request)
        {
            try
            {
                Verify.nameofDriverss = Fallback(response.nameofDriver, request.NameofDriver);
                Verify.plateNumberss = Fallback(response.plateNumber, request.PlateNumber);
                Verify.ownerPhoness = Fallback(response.ownerPhone, request.OwnerPhone);
                Verify.statess = Fallback(response.state, string.Empty);
                Verify.lgass = Fallback(response.lga, request.LGA);
                Verify.vehicleTypess = Fallback(response.vehicleType, request.VehicleType);
                Verify.amountss = Fallback(response.amount, response.totalAmount);
                Verify.messagess = Fallback(response.message, "Vehicle registered successfully.");
                Verify.recordedByss = Fallback(response.recordedBy, request.RecordedBy);
                Verify.dateRecordedss = response.dateRecorded ?? string.Empty;

                // Deliberately NOT response.statusCode. On this endpoint "00"/"oo" means the
                // registration succeeded, but Verify renders statusCodess == "00" as the
                // PAID badge — and this vehicle has not paid yet. Leaving it blank keeps
                // the badge on PENDING until a payment actually goes through.
                Verify.statusCodess = string.Empty;
            }
            catch (Exception ex)
            {
                Log("PublishToVerifyContext", ex);
            }
        }

        /// <summary>
        /// Route B: straight from the registration receipt into the payment page.
        /// Route A (Verify → Payments) is unaffected.
        /// </summary>
        private async void proceedToPayment_Tapped(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Verify.plateNumberss))
                {
                    Toast("Registration details are not available for payment. Please verify the plate number instead.", 5);
                    return;
                }

                sheetBehavior.IsOpen = false;
                failedenumeration.IsOpen = false;

                await Task.Delay(200);   // let the sheet finish closing before pushing

                SessionManager.Instance.UpdateActivity();
                await Navigation.PushAsync(new Views.Payment());
            }
            catch (Exception ex)
            {
                Log("proceedToPayment_Tapped", ex);
                Toast("Could not open the payment page. Please try from the Verify page.", 5);
            }
        }
        private static string SafeText(InputView entry)
        {
            try { return entry?.Text?.Trim() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string Fallback(string primary, string secondary)
            => !string.IsNullOrWhiteSpace(primary) ? primary : (secondary ?? string.Empty);

        private static void Toast(string message, int seconds = 3)
        {
            try
            {
                Device.BeginInvokeOnMainThread(() =>
                {
                    try { UserDialogs.Instance.Toast(message, TimeSpan.FromSeconds(seconds)); }
                    catch (Exception ex) { Log("Toast/inner", ex); }
                });
            }
            catch (Exception ex) { Log("Toast", ex); }
        }

        private static void Log(string scope, Exception ex)
            => System.Diagnostics.Debug.WriteLine($"[Reg:{scope}] {ex?.GetType().Name}: {ex?.Message}");
    }

    // ═════════════════════════ Bindable result model ═════════════════════════

    public class RegistrationResult : INotifyPropertyChanged
    {
        private string _driverName = "—";
        private string _plateNumber = "—";
        private string _ownerPhone = "—";
        private string _vehicleType = "—";
        private string _lgaState = "—";
        private string _amount = "₦0.00";
        private string _dateRecorded = "—";
        private string _recordedBy = "—";
        private string _message = "The vehicle has been registered successfully.";
        private string _errorTitle = "REGISTRATION FAILED";
        private string _errorMessage = "The registration could not be completed. Please verify your details and try again.";

        public string DriverName { get => _driverName; set => Set(ref _driverName, value); }
        public string PlateNumber { get => _plateNumber; set => Set(ref _plateNumber, value); }
        public string OwnerPhone { get => _ownerPhone; set => Set(ref _ownerPhone, value); }
        public string VehicleType { get => _vehicleType; set => Set(ref _vehicleType, value); }
        public string LgaState { get => _lgaState; set => Set(ref _lgaState, value); }
        public string Amount { get => _amount; set => Set(ref _amount, value); }
        public string DateRecorded { get => _dateRecorded; set => Set(ref _dateRecorded, value); }
        public string RecordedBy { get => _recordedBy; set => Set(ref _recordedBy, value); }
        public string Message { get => _message; set => Set(ref _message, value); }
        public string ErrorTitle { get => _errorTitle; set => Set(ref _errorTitle, value); }
        public string ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set(ref string field, string value, [CallerMemberName] string name = null)
        {
            if (field == value) return;
            field = string.IsNullOrWhiteSpace(value) ? "—" : value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // ═════════════════════════ DTOs (same shapes as the Borno haulage module) ═════════════════════════

    internal class VehicleCatData
    {
        public string vehicleType { get; set; }

        // Optional. Kept as a string so a number, a quoted number or a missing value all deserialise.
        public string amount { get; set; }
    }

    internal class LGACatData
    {
        public string lgaName { get; set; }
    }

    internal class VechicleRegisterationResponse
    {
        public string nameofDriver { get; set; }
        public string plateNumber { get; set; }
        public string ownerPhone { get; set; }
        public string state { get; set; }
        public string totalAmount { get; set; }
        public string lga { get; set; }
        public string vehicleType { get; set; }
        public string message { get; set; }
        public string statusCode { get; set; }
        public string amount { get; set; }
        public string recordedBy { get; set; }
        public string dateRecorded { get; set; }
    }

    internal class VechicleRegistrationObject
    {
        public string NameofDriver { get; set; }
        public string PlateNumber { get; set; }
        public string OwnerPhone { get; set; }
        public string LGA { get; set; }
        public string VehicleType { get; set; }
        public string RecordedBy { get; set; }
    }
}