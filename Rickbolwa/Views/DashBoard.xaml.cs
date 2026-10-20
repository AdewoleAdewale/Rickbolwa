using Acr.UserDialogs;
using Rickbolwa.Helper;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class DashBoard : ContentPage
    {
        public DashBoard()
        {
            InitializeComponent();
            var name = MainPage.Name;
            WelcomeMessage.Text = "Welcome, " + (string.IsNullOrEmpty(name) ? "Agent" : name);
            AvatarInitial.Text = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpper();
            BrandName.Text = BrandConfig.OrganisationName;
            FooterBrand.Text = BrandConfig.ReceiptFooterLine2;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            SessionService.Touch();
        }

        // ── Tiles ────────────────────────────────────────────────
        private async void TapGestureRecognizer_Tapped(object sender, EventArgs e)
            => await Navigation.PushAsync(new Verify());

        private async void TapGestureRecognizer_Tapped_5(object sender, EventArgs e)
            => await Navigation.PushAsync(new Registeration());

        private async void TapGestureRecognizer_Tapped_1(object sender, EventArgs e)
            => await Navigation.PushAsync(new History());

        // ── Account & tools ──────────────────────────────────────
        private void TapGestureRecognizer_Tapped_8(object sender, EventArgs e)
        {
            Device.BeginInvokeOnMainThread(async () =>
            {
                var action = await DisplayActionSheet("HI, WHAT DO YOU WANT TO DO?", "CANCEL", null,
                    "CHANGE PASSWORD", "CHANGE PIN", "TEST PRINTER", "RETRY PENDING RECEIPTS", "LOGOUT");

                switch (action)
                {
                    case "CHANGE PASSWORD":
                        Application.Current.MainPage = new NavigationPage(new ChangePassword());
                        break;

                    case "CHANGE PIN":
                        Application.Current.MainPage = new NavigationPage(new ChangeTransferPIN());
                        break;

                    case "TEST PRINTER":
                        await RunPrinterTestAsync();
                        break;

                    case "RETRY PENDING RECEIPTS":
                        UserDialogs.Instance.Toast("Retrying queued receipts…");
                        await ReceiptPrinter.RetryPendingAsync();
                        break;

                    case "LOGOUT":
                        TapGestureRecognizer_Tapped_7(sender, e);
                        break;
                }
            });
        }

        // Kept handler name; now a real session logout.
        private async void TapGestureRecognizer_Tapped_7(object sender, EventArgs e)
        {
            if (await DisplayAlert("Confirm Logout",
                    "End your session and return to the login screen?", "Yes, Logout", "Cancel"))
                await SessionService.LogoutToLoginAsync();
        }

        private void TapGestureRecognizer_Tapped_6(object sender, EventArgs e)
            => _ = RunPrinterTestAsync();

        // ── Back / hamburger ─────────────────────────────────────
        private void TapGestureRecognizer_Tapped_4(object sender, EventArgs e)
            => NavigateToDashboard();

        private void NavigateToDashboard()
        {
            ContentPage dest;
            switch (MainPage.category)
            {
              
                case "hulage":
                case "haulage": dest = null; break;   // this IS root
                default: dest = new DashBoard(); break;
            }

            if (dest == null) { _ = OfferLogoutAsync(); return; }
            Application.Current.MainPage = new NavigationPage(dest);
        }

        private async Task OfferLogoutAsync()
        {
            if (await DisplayAlert("Leave Dashboard?", "Do you want to log out?", "Logout", "Stay"))
                await SessionService.LogoutToLoginAsync();
        }

        protected override bool OnBackButtonPressed()
        {
            Device.BeginInvokeOnMainThread(NavigateToDashboard);
            return true;
        }

        // ── Printer test via shared App.Printer ──────────────────
        private async Task RunPrinterTestAsync()
        {
            if (!await BluetoothPermissionHelper.RequestAsync())
            {
                await DisplayAlert("Bluetooth Permission",
                    "Printing needs Bluetooth permission. Please allow it in Settings.", "OK");
                return;
            }

            try
            {
                using (UserDialogs.Instance.Loading("Connecting to printer…", null, null, true, MaskType.Black))
                {
                    var print = App.Printer.PrintTestPageAsync();
                    if (await Task.WhenAny(print, Task.Delay(TimeSpan.FromSeconds(30))) != print)
                    {
                        await DisplayAlert("Printer Timeout",
                            "Could not reach the printer. Ensure it is on and paired.", "OK");
                        return;
                    }
                    await print;
                }
                await DisplayAlert("Test Print", "Test page sent successfully.", "OK");
            }
            catch (PrinterException pex)
            {
                await DisplayAlert("Printer Error", pex.Message, "OK");
            }
            catch (Exception)
            {
                await DisplayAlert("Printer Error",
                    "Could not connect. Ensure the printer is on and paired.", "OK");
            }
        }
    }
}