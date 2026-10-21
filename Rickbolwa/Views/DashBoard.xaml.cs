using Acr.UserDialogs;
using Newtonsoft.Json;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;
using Xamarin.Forms.PlatformConfiguration.iOSSpecific;
using Xamarin.Forms.Xaml;
using static Android.Provider.CalendarContract;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class DashBoard : ContentPage
    {
        class HistoryData
        {
            public string transactionId { get; set; }
            public string businessName { get; set; }
            public string serviceName { get; set; }
            public string payerId { get; set; }
            public decimal amount { get; set; }
            public string dateRecorded { get; set; }
        }

        private readonly BluetoothPrinterService _printer =
            new BluetoothPrinterService(use80mm: false);

        public DashBoard()
        {
            InitializeComponent();

            // ── Match the names that actually exist in the XAML (Document 6) ──
            WelcomeMessage.Text = "WELCOME, " + (MainPage.Name ?? "AGENT");
            realdate.Text = MainPage.Super_Agent ?? "—";   // Super Agent card
            Agents.Text = MainPage.CollectionPoint ?? "—";   // Collection Point card
            realdates.Text = DateTime.Now.ToString("ddd, dd MMM yyyy");
            TrackUserActivity();
            LoadRecentTransaction();
            On<Xamarin.Forms.PlatformConfiguration.iOS>().SetUseSafeArea(true);
            ConfigureSSL();
        }



        // ─────────────────────────────────────────────────────────────────
        //  SSL
        // ─────────────────────────────────────────────────────────────────
        private void ConfigureSSL()
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            ServicePointManager.ServerCertificateValidationCallback =
                new RemoteCertificateValidationCallback(ValidateServerCertificate);
            ServicePointManager.DefaultConnectionLimit = 10;
            ServicePointManager.Expect100Continue = true;
        }

        private bool ValidateServerCertificate(object sender, X509Certificate certificate,
            X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None) return true;
            Debug.WriteLine($"SSL Error: {sslPolicyErrors}");
            return true;
        }

        // ─────────────────────────────────────────────────────────────────
        //  LOAD RECENT TRANSACTIONS
        //  Uses the original XAML element names: TodayTxnCount, TodayAmount,
        //  TodayVehicles, listView  (all defined in Document 6)
        // ─────────────────────────────────────────────────────────────────
        private async void LoadRecentTransaction()
        {
            using (UserDialogs.Instance.Loading("Loading transactions…", null, null, true))
            {
                await Task.Delay(500);

                string from = DateTime.Today.AddDays(-3).ToString("MM/dd/yyyy");
                string to = DateTime.Today.AddDays(1).ToString("MM/dd/yyyy");
                string url = "https://rickbolwa.osoftpay.net/api/TaskPayers/gettransaction"
                            + $"?Email={MainPage.ValidUserMail}"
                            + $"&SearchFrom={from}&SearchTo={to}";

                try
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (m, c, ch, e) => true,
                        SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                     | System.Security.Authentication.SslProtocols.Tls11
                    };

                    using (var client = new HttpClient(handler))
                    using (var response = await client.GetAsync(url))
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var items = JsonConvert.DeserializeObject<List<HistoryData>>(json)
                                    ?? new List<HistoryData>();

                        // Today's subset for the summary strip
                        var today = items.Where(x =>
                        {
                            if (DateTime.TryParse(x.dateRecorded, out var d))
                                return d.Date == DateTime.Today;
                            return false;
                        }).ToList();

                        // Summary strip  ── uses the names from Document 6 XAML
                        Device.BeginInvokeOnMainThread(() =>
                        {
                            TodayTxnCount.Text = today.Count.ToString();
                            TodayAmount.Text = today.Sum(x => x.amount).ToString("N0");
                            TodayVehicles.Text = items.Count.ToString();
                        });

                        // Recent list (latest 5)  ── listView is defined in Document 6
                        var sorted = items
                            .OrderByDescending(x => x.dateRecorded)
                            .Take(5)
                            .ToList();

                        Device.BeginInvokeOnMainThread(() =>
                            listView.ItemsSource = sorted);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Dashboard] LoadTxn error: {ex.Message}");
                    Device.BeginInvokeOnMainThread(() =>
                    {
                        TodayTxnCount.Text = "—";
                        TodayAmount.Text = "—";
                        TodayVehicles.Text = "—";
                    });
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  NAVIGATION HANDLERS
        // ─────────────────────────────────────────────────────────────────
        private async void TapGestureRecognizer_Tapped(object sender, EventArgs e)
        {
            using (UserDialogs.Instance.Loading("Loading…", null, null, true))
            {
                SessionManager.Instance.UpdateActivity();
                await Task.Delay(400);
                await Navigation.PushAsync(new Verify());
            }
        }

        private async void TapGestureRecognizer_Tapped_1(object sender, EventArgs e)
            =>

            await Navigation.PushAsync(new History());

        private void TapGestureRecognizer_Tapped_8(object sender, EventArgs e)
        {
            Device.BeginInvokeOnMainThread(async () =>
            {
                string action = await DisplayActionSheet(
                    "ACCOUNT SETTINGS", "CANCEL", null,
                    "CHANGE PASSWORD", "CHANGE PIN");

                if (action == "CHANGE PASSWORD")
                    await Navigation.PushAsync(new Views.ChangePassword());
                else if (action == "CHANGE PIN")
                    await Navigation.PushAsync(new Views.ChangeTransferPIN());
            });
        }

        private async void TapGestureRecognizer_Tapped_5(object sender, EventArgs e)
        {
            using (UserDialogs.Instance.Loading("Loading…", null, null, true))
            {
                await Task.Delay(400);
                SessionManager.Instance.UpdateActivity();
                await Navigation.PushAsync(new Registeration());
            }
        }

        private void TapGestureRecognizer_Tapped_2(object sender, EventArgs e)
            => CallPrinterAsync();



        private async void TapGestureRecognizer_Tapped_7(object sender, EventArgs e)
        {
            try
            {
                var confirmed = await DisplayAlert("Logout",
                    "Are you sure you want to logout?", "Yes", "No");
                if (!confirmed) return;

                SessionManager.Instance.StopSession();
                await SecureStorageService.ClearCredentialsAsync();

                Device.BeginInvokeOnMainThread(() => App.SetRoot(new MainPage()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Haulage:Logout] {ex.Message}");
            }
        }


        private void TapGestureRecognizer_Tapped_3(object sender, EventArgs e) { }

        // ─────────────────────────────────────────────────────────────────
        //  PRINTER
        // ─────────────────────────────────────────────────────────────────
        private async Task CallPrinterAsync()
        {
            try
            {
                using (UserDialogs.Instance.Loading("Connecting to Printer…", null, null, true))
                {
                    var printTask = _printer.PrintTestPageAsync();
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
                    var finished = await Task.WhenAny(printTask, timeoutTask);

                    if (finished == timeoutTask)
                    {
                        await DisplayAlert("Printer Error",
                            "Print timed out. Ensure the printer is on and paired.", "OK");
                        return;
                    }
                    await printTask;
                }
                await DisplayAlert("Print Test", "Test page sent successfully.", "OK");
            }
            catch (PrinterException pex)
            {
                await DisplayAlert("Printer Error", pex.Message, "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Printer Error", "Could not connect to printer.", "OK");
                Debug.WriteLine($"[Dashboard] Printer: {ex}");
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  BACK BUTTON
        // ─────────────────────────────────────────────────────────────────
        protected override bool OnBackButtonPressed()
        {
            Device.BeginInvokeOnMainThread(async () =>
            {
                using (UserDialogs.Instance.Loading("Please wait…", null, null, true))
                {
                    await Task.Delay(10);
                    await Navigation.PushModalAsync(new DashBoard());
                }
            });
            return true;
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


        private async void TapGestureRecognizer_Tapped_4(object sender, EventArgs e)
        {
            try
            {
                var confirmed = await DisplayAlert("Logout",
                    "Are you sure you want to logout?", "Yes", "No");
                if (!confirmed) return;

                SessionManager.Instance.StopSession();
                await SecureStorageService.ClearCredentialsAsync();

                Device.BeginInvokeOnMainThread(() => App.SetRoot(new MainPage()));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Haulage:Logout] {ex.Message}");
            }
        }
    }
}