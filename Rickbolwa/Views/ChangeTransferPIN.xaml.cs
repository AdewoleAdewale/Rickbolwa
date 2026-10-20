using Acr.UserDialogs;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class ChangeTransferPIN : ContentPage
    {
        public ChangeTransferPIN()
        {
            InitializeComponent();
        }
        private void TapGestureRecognizer_Tapped(object sender, EventArgs e)
               => NavigateToDashboard();

        protected override bool OnBackButtonPressed()
        {
            Device.BeginInvokeOnMainThread(NavigateToDashboard);
            return true;
        }

        private void NavigateToDashboard()
        {
            ContentPage dest;

            switch (MainPage.category)
            {
           

                default:
                    dest = new Views.DashBoard();
                    break;
            }

            Application.Current.MainPage = new NavigationPage(dest);
        }

        // ── Change PIN ───────────────────────────────────────────────────────
        private async void TapGestureRecognizer_Tapped1(object sender, EventArgs e)
        {
            string oldPin = OldPINEntry.Text?.Trim();
            string newPin = ConfirmPIN.Text?.Trim();

            // Validate old PIN
            if (string.IsNullOrWhiteSpace(oldPin) || oldPin.Length < 4)
            {
                await DisplayAlert("Missing PIN", "Please enter your current 4-digit PIN.", "OK");
                return;
            }

            // Confirm old PIN matches locally stored PIN
            if (oldPin != MainPage.Pin)
            {
                await DisplayAlert("Incorrect PIN",
                    "The current PIN you entered is incorrect. Please try again.", "OK");
                return;
            }

            // Validate new PIN
            if (string.IsNullOrWhiteSpace(newPin) || newPin.Length != 4)
            {
                await DisplayAlert("Invalid PIN",
                    "New PIN must be exactly 4 digits.", "OK");
                return;
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(newPin, @"^\d{4}$"))
            {
                await DisplayAlert("Invalid PIN",
                    "PIN must contain only numbers (0–9).", "OK");
                return;
            }

            if (oldPin == newPin)
            {
                await DisplayAlert("Same PIN",
                    "New PIN cannot be the same as your current PIN.", "OK");
                return;
            }

            using (UserDialogs.Instance.Loading("Updating PIN…", null, null, true))
            {
                await Task.Delay(1000);
                await ChangePinAsync(newPin);
            }
        }

        private async Task ChangePinAsync(string newPin)
        {
            string url = $"https://rickbolwa.osoftpay.net/api/taskpayers/ChangePin"
                       + $"?UserName={Uri.EscapeDataString(MainPage.ValidUserMail)}"
                       + $"&NewPin={Uri.EscapeDataString(newPin)}";

            try
            {
                using (var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (a, b, c, d) => Rickbolwa.Services.ApiClient.ValidateCertificate(a, b, c, d),
                    SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                               | System.Security.Authentication.SslProtocols.Tls11
                })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
                {
                    var response = await client.GetAsync(url).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var result = JsonConvert.DeserializeObject<InterfacePass>(json);

                    await Device.InvokeOnMainThreadAsync(async () =>
                    {
                        if (result?.status == "00")
                        {
                            App.IsUserLoggedIn = false;
                            await DisplayAlert("✅ PIN Updated",
                                "Your PIN has been changed successfully. Please log in again.",
                                "OK");
                            Application.Current.MainPage = new NavigationPage(new MainPage());
                        }
                        else
                        {
                            await DisplayAlert("Update Failed",
                                result?.Message ?? "PIN could not be changed. Please try again.",
                                "OK");
                        }
                    });
                }
            }
            catch (HttpRequestException httpEx)
            {
                await Device.InvokeOnMainThreadAsync(() =>
                    DisplayAlert("Connection Error",
                        $"Unable to update PIN.\n\n{httpEx.Message}", "Retry"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChangeTransferPIN] Error: {ex}");
                await Device.InvokeOnMainThreadAsync(() =>
                    DisplayAlert("Error",
                        "An unexpected error occurred. Check your internet and try again.", "OK"));
            }
        }
    }

    internal class InterfacePass
    {
        public string MerchantSubUser { get; set; }

        public string status { get; set; }

        public string statusCode { get; set; }
        public string statusMessage { get; set; }


        public string Password { get; set; }

        public string PhoneNumber { get; set; }

        public string FullName { get; set; }

        public string Message { get; set; }
    }
}