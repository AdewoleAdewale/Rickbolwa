using Android.Views;
using Rickbolwa.Services;
using System;
using System.Threading.Tasks;
using Xamarin.Forms;

namespace Rickbolwa
{
    public partial class App : Application
    {
        /// <summary>
        /// Backed by the persistent session instead of a plain static, so it survives
        /// process death. Existing assignments (`App.IsUserLoggedIn = false` in the
        /// dashboards' logout paths) still work and now clear the stored session too.
        /// </summary>
        public static bool IsUserLoggedIn
        {
            get => SessionManager.IsAuthenticated;
            set
            {
                try
                {
                    if (value) SessionManager.Instance.StartSession();
                    else SessionManager.Instance.StopSession();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App:IsUserLoggedIn] {ex.Message}");
                }
            }
        }

        public static string PrinterFooter { get; set; }
        public static string PrinterforWaterPayment { get; set; }
        public static string RevenueServiceName { get; set; }
        public static string CentralPortalURL { get; set; }
        public static string CentralPortalURLkeke { get; set; }
        public static string ThankYouMessage { get; set; }
        public static string ThankYouMessage2 { get; set; }

        /// <summary>
        /// Shared <see cref="IPrinterService"/> instance used across all pages.
        /// Use this for availability checks and direct print calls.
        /// </summary>
        public static IPrinterService Printer { get; private set; }

        /// <summary>
        /// Durable job queue that persists receipts to disk so they can be
        /// retried automatically after a crash, Bluetooth drop, or app restart.
        /// All pages must use <c>App.PrintJobManager</c> rather than calling
        /// <c>Printer</c> directly.
        /// </summary>
        public static PrintJobManager PrintJobManager { get; private set; }

        public App()
        {
            InitializeComponent();

            CentralPortalURL = "https://rickbolwa.osoftpay.net/api/SingleCollections/PostCollect/NewCollect";
            RevenueServiceName = "RICKBOLWA LIMITED";
            PrinterFooter = "POWERED BY OSOFTPAY";
            ThankYouMessage = "THANK YOU FOR MAKING YOUR PAYMENT!";
            ThankYouMessage2 = "THANK YOU FOR ENUMERATION!";

            Rickbolwa.Services.SslHandler.ConfigureSSL();
            Printer = new BluetoothPrinterService(use80mm: false);
            PrintJobManager = new PrintJobManager(Printer);

            // Shown for the few milliseconds it takes to read the session off disk.
            // Routing happens in BootstrapAsync — never here, because the session
            // has not been restored yet at this point.
            MainPage = BuildSplash();

            _ = BootstrapAsync();
        }

        /// <summary>
        /// Restores the persisted session, then sends the agent either to their module
        /// dashboard or to the login page. Runs once per process start.
        /// </summary>
        private async Task BootstrapAsync()
        {
            try
            {
                await SessionManager.RestoreAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App:Bootstrap] {ex.Message}");
            }

            Device.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    SetRoot(ResolveLandingPage());
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[App:Route] {ex.Message}");
                    SetRoot(new MainPage());
                }
            });
        }

        /// <summary>
        /// Mirrors the category switch in MainPage.NavigateToAppropriatePageAsync so a
        /// restored session lands on the same dashboard the agent logged into.
        /// </summary>
        public static Page ResolveLandingPage()
        {
            try
            {
                if (!SessionManager.IsAuthenticated)
                    return new MainPage();

                switch ((SessionManager.Category ?? string.Empty).Trim().ToLowerInvariant())
                {

                    case "default": return new Views.DashBoard();
                    default:
                        // Unknown or missing category — the session is not usable, so drop
                        // it rather than stranding the agent on a blank shell.
                        System.Diagnostics.Debug.WriteLine(
                            $"[App:Route] Unknown category '{SessionManager.Category}' — clearing session");
                        SessionManager.Instance.StopSession();
                        return new MainPage();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App:ResolveLandingPage] {ex.Message}");
                return new MainPage();
            }
        }

        /// <summary>Single place that builds the navigation shell, so styling stays consistent.</summary>
        public static void SetRoot(Page page)
        {
            try
            {
                Current.MainPage = new NavigationPage(page)
                {
                    BarBackgroundColor = Color.FromHex("#004225"),
                    BarTextColor = Color.White
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App:SetRoot] {ex.Message}");
            }
        }

        private static Page BuildSplash() => new ContentPage
        {
            BackgroundColor = Color.FromHex("#004225"),
            Content = new StackLayout
            {
                Spacing = 20,
                VerticalOptions = LayoutOptions.CenterAndExpand,
                HorizontalOptions = LayoutOptions.CenterAndExpand,
                Children =
                {
                    new Image { Source = "Logo", HeightRequest = 90, HorizontalOptions = LayoutOptions.Center },
                    new ActivityIndicator { IsRunning = true, Color = Color.White }
                }
            }
        };

        // Lifecycle: stamp activity only. Nothing here may clear or rebuild the session —
        // that is what caused the app to ask for login again after being backgrounded.
        protected override void OnStart() => SessionManager.Instance.UpdateActivity();

        protected override void OnSleep() => SessionManager.Instance.UpdateActivity();

        protected override void OnResume() => SessionManager.Instance.UpdateActivity();
    }
}