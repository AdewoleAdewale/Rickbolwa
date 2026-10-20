using Rickbolwa.Helper;
using Rickbolwa.Services;
using System;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa
{
    public partial class App : Application
    {
        public static bool IsUserLoggedIn
        {
            get { return SessionService.IsLoggedIn; }
            set { /* no-op: SessionService owns login state now */ }
        }

        public static string PrinterFooter => BrandConfig.ReceiptFooterLine2;
        public static string RevenueServiceName => BrandConfig.OrganisationName + " (" + BrandConfig.OrganisationAbbr + ") ";
        public static string CentralPortalURL => BrandConfig.CentralCollectUrl;
        public static string CentralPortalURLkeke { get; set; }
        public static string ThankYouMessage => "CONTACT US : " + BrandConfig.SupportPhone1 + "," + BrandConfig.SupportPhone2;

        /// <summary>Shared printer service. Prefer <see cref="ReceiptPrinter"/> over calling this directly.</summary>
        public static IPrinterService Printer { get; private set; }

        /// <summary>Durable print job queue. Prefer <see cref="ReceiptPrinter"/>.</summary>
        public static PrintJobManager PrintJobManager { get; private set; }

        /// <summary>
        /// Called by each platform head (MainActivity on Android) BEFORE
        /// LoadApplication to inject the platform printer implementation.
        /// The shared project no longer references Android assemblies.
        /// </summary>
        public static void InitializePrinting(IPrinterService printer)
        {
            Printer = printer ?? new MockPrinterService();
            PrintJobManager = new PrintJobManager(Printer);
        }

        public App()
        {
            InitializeComponent();

            // Safety net for previews/tests where the platform head did not
            // inject a printer; Android replaces this in MainActivity.
            if (Printer == null)
                InitializePrinting(new MockPrinterService());

            // Always start on the login screen; OnStart will fast-forward to
            // the correct dashboard if a valid persisted session exists.
            MainPage = new MainPage();
        }

        protected override async void OnStart()
        {
            try
            {
                // ── Session restore (auto-login) ─────────────────
                var session = await SessionService.TryRestoreAsync();
                if (session != null)
                {
                    await NavigationRouter.GoToCategoryDashboardAsync(
                        session.Category, session.Name, showWelcome: false);
                }

                // ── Durable print recovery ───────────────────────
                // Any receipt interrupted by a crash / Bluetooth drop
                // last run is retried now.
                await ReceiptPrinter.RetryPendingAsync();
            }
            catch
            {
                // Never let startup recovery crash the app; the login
                // page is already displayed as a safe fallback.
            }
        }

        protected override void OnSleep()
        {
            // Stamp last activity so the inactivity timeout counts
            // time spent backgrounded.
            if (SessionService.IsLoggedIn)
                SessionService.Touch();
            _sleptAtUtc = DateTime.UtcNow;
        }

        protected override async void OnResume()
        {
            try
            {
                // ── Inactivity timeout enforcement ───────────────
                if (SessionService.IsLoggedIn && _sleptAtUtc.HasValue)
                {
                    var away = DateTime.UtcNow - _sleptAtUtc.Value;
                    if (away > TimeSpan.FromMinutes(BrandConfig.SessionInactivityTimeoutMinutes))
                    {
                        await SessionService.LogoutToLoginAsync();
                        return;
                    }
                    SessionService.Touch();
                }

                await ReceiptPrinter.RetryPendingAsync();
            }
            catch { }
        }

        private DateTime? _sleptAtUtc;
    }
}
