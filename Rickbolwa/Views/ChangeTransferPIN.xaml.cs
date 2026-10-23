using Acr.UserDialogs;
using Newtonsoft.Json;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

        private async void OnUpdatePinClicked(object sender, EventArgs e)
        {
            SessionManager.Instance.UpdateActivity();

            if (string.IsNullOrWhiteSpace(ConfirmPIN.Text) || string.IsNullOrWhiteSpace(OldPINEntry.Text))
            {
                await Navigation.PushModalAsync(new WaterFailureSheet("Validation Error", "Please fill in all PIN fields."));
                return;
            }

            using (UserDialogs.Instance.Loading("Verifying...", null, null, true))
            {
                await Task.Delay(1000);

                if (OldPINEntry.Text == MainPage.Pin)
                {
                    string url = $"https://rickbolwa.osoftpay.net/api/TaskPayers/ChangePin?UserName={MainPage.ValidUserMail}&NewPin={ConfirmPIN.Text}";

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

                                        await Navigation.PushModalAsync(new WaterSuccessSheet(
                                            "PIN Updated",
                                            "Transaction PIN changed successfully. Please login again.",
                                            "PIN-UPDATE",
                                            "Successful",
                                            async () =>
                                            {
                                                Application.Current.MainPage = new NavigationPage(new MainPage());
                                            }));
                                    }
                                    else
                                    {
                                        await Navigation.PushModalAsync(new WaterFailureSheet("Update Failed", "Error, PIN was not changed!"));
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
                else
                {
                    await Navigation.PushModalAsync(new WaterFailureSheet("Verification Failed", "Can't confirm your old PIN. Please try again."));
                }
            }
        }
    }
}