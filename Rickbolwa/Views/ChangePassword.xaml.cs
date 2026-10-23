using Acr.UserDialogs;
using Newtonsoft.Json;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

        private void TrackUserActivity()
        {
            // Add tap gesture to main grid to track any user interaction
            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += (s, e) => SessionManager.Instance.UpdateActivity();
            this.Content.GestureRecognizers.Add(tapGesture);
        }


        private async void OnCancelClicked(object sender, EventArgs e)
        {
            try
            {
                // This page is opened with PushAsync (DashBoard), so the modal stack is empty.
                // Pop whichever way the page was actually presented.
                var modals = Navigation.ModalStack;
                if (modals.Count > 0 && modals[modals.Count - 1] == this)
                    await Navigation.PopModalAsync();
                else if (Navigation.NavigationStack.Count > 1)
                    await Navigation.PopAsync();
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Cancel] {ex.Message}"); }
        }

        private async void OnUpdatePasswordClicked(object sender, EventArgs e)
        {
            SessionManager.Instance.UpdateActivity();

            if (string.IsNullOrWhiteSpace(ConfirmPassword.Text) || string.IsNullOrWhiteSpace(OldPasswordEntry.Text))
            {
                await Navigation.PushModalAsync(new WaterFailureSheet("Validation Error", "Please fill in all password fields."));
                return;
            }

            using (UserDialogs.Instance.Loading("Verifying...", null, null, true))
            {
                await Task.Delay(1000);

                string url = $"https://rickbolwa.osoftpay.net/api/TaskPayers/ChangePassword?UserName={MainPage.ValidUserMail}&NewPassword={ConfirmPassword.Text}";

                try
                {
                    using (var httpClientHandler = new HttpClientHandler())
                    {
                        httpClientHandler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;

                        using (HttpClient client = new HttpClient(httpClientHandler))
                        {
                            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                            using (HttpResponseMessage response = await client.GetAsync(url))
                            {
                                var json = await response.Content.ReadAsStringAsync();
                                var result = JsonConvert.DeserializeObject<InterfacePass>(json);

                                if (result != null && result.status == "00")
                                {
                                    App.IsUserLoggedIn = false;

                                    // Show Success Sheet and redirect to Login on close
                                    await Navigation.PushModalAsync(new WaterSuccessSheet(
                                        "Password Updated",
                                        "Your password was changed successfully. Please login again.",
                                        "PWD-UPDATE",
                                        "Successful",
                                        async () =>
                                        {
                                            // Action when user clicks "Done" on success sheet
                                            Application.Current.MainPage = new NavigationPage(new MainPage());
                                        }));
                                }
                                else
                                {
                                    await Navigation.PushModalAsync(new WaterFailureSheet("Update Failed", "Error, Password was not changed!"));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    await Navigation.PushModalAsync(new WaterFailureSheet("Network Error", "Check your internet connection."));
                }
            }
        }
    }



    internal class InterfacePass
    {
        public string MerchantSubUser { get; set; }

        public string status { get; set; }

        public string Password { get; set; }

        public string PhoneNumber { get; set; }

        public string FullName { get; set; }
    }
}