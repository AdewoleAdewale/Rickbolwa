using Acr.UserDialogs;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class ChangePassword : ContentPage
    {
        public ChangePassword()
        {
            InitializeComponent();
        }

        // ── Navigation ───────────────────────────────────────────────────────
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

        // ── Change password ───────────────────────────────────────────────────
        private async void TapGestureRecognizer_Tapped_1(object sender, EventArgs e)
        {
            string newPass = ConfirmPassword.Text?.Trim();

            // Client-side validation
            if (string.IsNullOrWhiteSpace(OldPasswordEntry.Text))
            {
                await DisplayAlert("Missing Field", "Please enter your current password.", "OK");
                return;
            }
            if (string.IsNullOrWhiteSpace(newPass) || newPass.Length < 8)
            {
                await DisplayAlert("Weak Password",
                    "New password must be at least 8 characters long.", "OK");
                return;
            }
            if (!Regex.IsMatch(newPass, @"[A-Z]"))
            {
                await DisplayAlert("Weak Password",
                    "New password must contain at least one uppercase letter.", "OK");
                return;
            }
            if (!Regex.IsMatch(newPass, @"[!@#$%^&*(),.?\"":{}|<>]"))
            {
                await DisplayAlert("Weak Password",
                    "New password must contain at least one special character.", "OK");
                return;
            }

            using (UserDialogs.Instance.Loading("Updating password…", null, null, true))
            {
                await Task.Delay(1000);
                await ChangePasswordAsync(newPass);
            }
        }

        private async Task ChangePasswordAsync(string newPassword)
        {
            string url = $"https://rickbolwa.osoftpay.net/api/TaskPayers/ChangePassword"
                       + $"?UserName={Uri.EscapeDataString(MainPage.ValidUserMail)}"
                       + $"&NewPassword={Uri.EscapeDataString(newPassword)}";

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
                            await DisplayAlert("✅ Password Updated",
                                "Your password has been changed successfully. Please log in again.",
                                "OK");
                            Application.Current.MainPage = new NavigationPage(new MainPage());
                        }
                        else
                        {
                            await DisplayAlert("Update Failed",
                                result?.Message ?? "Password could not be changed. Please try again.",
                                "OK");
                        }
                    });
                }
            }
            catch (HttpRequestException httpEx)
            {
                await Device.InvokeOnMainThreadAsync(() =>
                    DisplayAlert("Connection Error",
                        $"Unable to update password.\n\n{httpEx.Message}", "Retry"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChangePassword] Error: {ex}");
                await Device.InvokeOnMainThreadAsync(() =>
                    DisplayAlert("Error",
                        "An unexpected error occurred. Please check your internet and try again.",
                        "OK"));
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
}