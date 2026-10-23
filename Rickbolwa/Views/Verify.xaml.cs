using Acr.UserDialogs;
using Android.Accounts;
using Newtonsoft.Json;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class Verify : ContentPage
    {
        public static string nameofDriverss { get; set; }
        public static string plateNumberss { get; set; }
        public static string ownerPhoness { get; set; }
        public static string statess { get; set; }
        public static string lgass { get; set; }
        public static string vehicleTypess { get; set; }
        public static string messagess { get; set; }
        public static string statusCodess { get; set; }
        public static string amountss { get; set; }
        public static string recordedByss { get; set; }
        public static string dateRecordedss { get; set; }

        public Verify()
        {
            InitializeComponent();
            ConfigureSSL();
            TrackUserActivity();

            // Restore previous result if it exists
            if (plateNumberss != null)
            {
                PopulateResultPanel();
                ShowResultPanel();
            }
            else
            {
                EmptyState.IsVisible = true;
                ResultPanel.IsVisible = false;
            }
        }

        private void PopulateResultPanel()
        {
            nameofDrivers.Text = nameofDriverss ?? "—";
            plateNumbers.Text = plateNumberss ?? "—";
            lga.Text = lgass ?? "—";
            Amount.Text = amountss ?? "—";
            vehicleTypes.Text = vehicleTypess ?? "—";
            messages.Text = messagess ?? "—";
            DateRecorded.Text = dateRecordedss ?? "—";
            ownerPhones.Text = ownerPhoness ?? "—";

            SessionManager.Instance.UpdateActivity();
            // Status badge colour
            bool paid = (statusCodess == "00");
            StatusBadge.BackgroundColor = paid ? Color.FromHex("#00FF8820") : Color.FromHex("#FFAA4420");
            StatusBadge.BorderColor = paid ? Color.FromHex("#00FF8860") : Color.FromHex("#FFAA4460");
            StatusBadgeText.Text = paid ? "PAID" : "PENDING";
            StatusBadgeText.TextColor = paid ? Color.Red : Color.Blue;
        }

        private void ShowResultPanel()
        {
            EmptyState.IsVisible = false;
            ResultPanel.IsVisible = true;
            makePaymentPanel.IsVisible = plateNumberss != null;
        }

        private void ConfigureSSL()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            ServicePointManager.ServerCertificateValidationCallback =
                new RemoteCertificateValidationCallback(ValidateServerCertificate);
            ServicePointManager.DefaultConnectionLimit = 10;
            ServicePointManager.Expect100Continue = true;
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate,
            X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None) return true;
            System.Diagnostics.Debug.WriteLine($"SSL Error: {sslPolicyErrors}");
            return true;
        }

        protected override bool OnBackButtonPressed()
        {
            Device.BeginInvokeOnMainThread(async () =>
            {
                using (UserDialogs.Instance.Loading("Please wait…", null, null, true))
                {
                    await Task.Delay(10);

                    SessionManager.Instance.UpdateActivity();
                    await Navigation.PushModalAsync(new Views.DashBoard());
                }
            });
            return true;
        }

        // ─── Clear search ────────────────────────────────────────────────────

        private void ClearSearch_Tapped(object sender, EventArgs e)
        {
            Search.Text = string.Empty;
            ResultPanel.IsVisible = false;
            EmptyState.IsVisible = true;
        }

        // ─── Search / Verify ─────────────────────────────────────────────────

        private async void TapGestureRecognizer_Tapped_1(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Search.Text))
            {
                UserDialogs.Instance.Toast("Please enter a plate number.", TimeSpan.FromSeconds(3));

                SessionManager.Instance.UpdateActivity();
                return;
            }

            using (UserDialogs.Instance.Loading("Verifying plate number…", null, null, true))
            {
                await Task.Delay(600);

                string url = "https://rickbolwa.osoftpay.net/api/HulageVehicles/verify?PlateNumber="
                             + Uri.EscapeDataString(Search.Text.Trim().ToUpper());

                try
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (m, c, ch, e2) => true,
                        SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                     | System.Security.Authentication.SslProtocols.Tls11
                    };

                    using (var client = new HttpClient(handler))
                    {
                        client.DefaultRequestHeaders.Accept.Add(
                            new MediaTypeWithQualityHeaderValue("application/json"));

                        using (var response = await client.GetAsync(url))
                        {
                            var json = await response.Content.ReadAsStringAsync();
                            var result = JsonConvert.DeserializeObject<HaulageVerifyResponse>(json);

                            if (result != null && !string.IsNullOrEmpty(result.plateNumber))
                            {
                                nameofDriverss = result.nameofDriver;
                                plateNumberss = result.plateNumber;
                                lgass = result.lga;
                                amountss = result.amount;
                                vehicleTypess = result.vehicleType;
                                ownerPhoness = result.ownerPhone;
                                messagess = result.message;
                                statusCodess = result.statusCode;
                                recordedByss = result.recordedBy;
                                dateRecordedss = result.dateRecorded;

                                await Application.Current.SavePropertiesAsync();

                                Device.BeginInvokeOnMainThread(() =>
                                {
                                    PopulateResultPanel();
                                    ShowResultPanel();
                                });
                            }
                            else
                            {
                                Device.BeginInvokeOnMainThread(() =>
                                {
                                    ResultPanel.IsVisible = false;
                                    EmptyState.IsVisible = true;
                                });

                                UserDialogs.Instance.Toast(
                                    !string.IsNullOrWhiteSpace(result?.message)
                                        ? result.message
                                        : $"No vehicle found for plate: {Search.Text.Trim().ToUpper()}",
                                    TimeSpan.FromSeconds(5));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    UserDialogs.Instance.Toast(
                        "Network error — please check your connection and try again.",
                        TimeSpan.FromSeconds(6));
                    System.Diagnostics.Debug.WriteLine($"[HaulageVerify] {ex.Message}");
                }
            }
        }
        private async void make_Clicked(object sender, EventArgs e)
            => await Navigation.PushAsync(new Payment());

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

        internal class HaulageVerifyResponse
        {
            public string nameofDriver { get; set; }
            public string plateNumber { get; set; }
            public string ownerPhone { get; set; }
            public string state { get; set; }
            public string lga { get; set; }
            public string vehicleType { get; set; }
            public string message { get; set; }
            public string statusCode { get; set; }
            public string amount { get; set; }
            public string recordedBy { get; set; }
            public string dateRecorded { get; set; }
        }
    }
}