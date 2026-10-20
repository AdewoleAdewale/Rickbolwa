using Acr.UserDialogs;
using Newtonsoft.Json;
using Rickbolwa.Helper;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Rickbolwa
{
    [DesignTimeVisible(false)]
    public partial class MainPage : ContentPage
    {
        private bool _isPasswordHidden = true;
        private bool _rememberMe = true;

        private const string RememberFlagKey = "login_remember_me";
        private const string RememberedUserKey = "login_remembered_user";

        // Legacy compatibility static accessors forwarded to SessionService
        public static string Name => SessionService.Current?.Name ?? string.Empty;
        public static string ValidUserMail => SessionService.Current?.Email ?? string.Empty;
        public static string Pin => SessionService.Pin;
        public static string Super_Agent => SessionService.Current?.SuperAgent ?? string.Empty;
        public static string Message => SessionService.Current?.Message ?? string.Empty;
        public static string category => SessionService.Current?.Category ?? string.Empty;
        public static string CollectionPoint => SessionService.Current?.CollectionPoint ?? string.Empty;
        public static string Passwords => string.Empty;

        public string OrganisationAbbr => BrandConfig.OrganisationAbbr;
        public string OrganisationName => BrandConfig.OrganisationName;
        public string SessionNote =>
            string.Format("Sessions auto-lock after {0} minutes of inactivity",
                BrandConfig.SessionInactivityTimeoutMinutes);

        public MainPage()
        {
            InitializeComponent();
            BindingContext = this;
            SupportLabel.Text = $"Need help? {BrandConfig.SupportLine}";

            RestoreRememberedUser();
        }

        /// <summary>
        /// Restores the "Remember me" preference and pre-fills the username.
        /// Only the username is ever stored — never the password.
        /// </summary>
        private void RestoreRememberedUser()
        {
            _rememberMe = Preferences.Get(RememberFlagKey, true);
            RenderRememberState();

            if (!_rememberMe) return;

            var saved = Preferences.Get(RememberedUserKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                Email.Text = saved;
                Password.Focus();
            }
        }

        private void RenderRememberState()
        {
            RememberTick.IsVisible = _rememberMe;

            if (_rememberMe)
            {
                RememberBox.BackgroundColor = (Color)Application.Current.Resources["PrimaryColor"];
                RememberBox.BorderThickness = 0;
            }
            else
            {
                RememberBox.BackgroundColor = Color.Transparent;
                RememberBox.BorderColor = (Color)Application.Current.Resources["TextSecondaryColor"];
                RememberBox.BorderThickness = 1;
            }
        }

        private void OnRememberToggled(object sender, EventArgs e)
        {
            _rememberMe = !_rememberMe;
            RenderRememberState();

            Preferences.Set(RememberFlagKey, _rememberMe);
            if (!_rememberMe) Preferences.Remove(RememberedUserKey);
        }

        private void OnTogglePassword(object sender, EventArgs e)
        {
            _isPasswordHidden = !_isPasswordHidden;
            Password.IsPassword = _isPasswordHidden;
            TogglePasswordIcon.Opacity = _isPasswordHidden ? 1.0 : 0.45;
        }

        private async void OnTermsTapped(object sender, EventArgs e)
        {
            await DisplayAlert("Terms & Privacy",
                $"This application is provided by {BrandConfig.OrganisationName} for authorised " +
                "revenue-collection agents.\n\n" +
                "By signing in you agree that all collection activity performed on this device is " +
                "recorded against your agent account, and that transaction data is processed in " +
                "line with the agency's data-protection policy.\n\n" +
                $"Questions: {BrandConfig.SupportLine}", "Close");
        }

        private async void OnRegisterTapped(object sender, EventArgs e)
        {
            // Agent accounts are provisioned by the agency — not self-registered.
            await DisplayAlert("Agent Registration",
                $"Collection-agent accounts are created and approved by {BrandConfig.OrganisationAbbr} " +
                "administration and cannot be self-registered from the app.\n\n" +
                $"To request an account, contact:\n{BrandConfig.SupportLine}", "OK");
        }

        private async void OnForgotPassword(object sender, EventArgs e)
        {
            await DisplayAlert("Reset Password",
                "For security, passwords are reset by the support desk after your agent identity " +
                "has been verified.\n\n" +
                $"Call or message:\n{BrandConfig.SupportLine}\n\n" +
                "Please have your agent name and collection point ready.", "OK");
        }

        private async void OnSignInClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(Email?.Text) || string.IsNullOrWhiteSpace(Password?.Text))
            {
                await DisplayAlert("⚠️ Required Information",
                    "Please fill in all details before proceeding.", "Try Again");
                return;
            }

            var userName = Email.Text.Trim();
            var password = Password.Text.Trim();

            SignInButton.IsEnabled = false;

            try
            {
                using (UserDialogs.Instance.Loading("🔐 Authenticating…", null, null, true, MaskType.Black))
                {
                    var url = $"{BrandConfig.LoginUrl}?UserName={Uri.EscapeDataString(userName)}&Password={Uri.EscapeDataString(password)}";

                    LoginResponse result;
                    try
                    {
                        result = await ApiClient.GetJsonAsync<LoginResponse>(url);
                    }
                    catch (TaskCanceledException)
                    {
                        await DisplayAlert("⏱️ Connection Timeout",
                            "The request timed out. Please verify your connection and try again.", "Retry");
                        return;
                    }
                    catch (HttpRequestException)
                    {
                        await DisplayAlert("🌐 Network Error",
                            $"Unable to connect to the server. Please check your internet connection or contact support:\n\n{BrandConfig.SupportLine}", "OK");
                        return;
                    }
                    catch (JsonException)
                    {
                        await DisplayAlert("📄 Data Format Error",
                            "Unexpected server response format. Please contact technical support.", "OK");
                        return;
                    }

                    if (result == null || result.responseCode != "00" || result.agent == null)
                    {
                        var msg = !string.IsNullOrEmpty(result?.message)
                            ? result.message
                            : "Authentication failed. Please check your credentials.";

                        await DisplayAlert("🔴 Login Failed",
                            $"{msg}\n\nSupport Line: {BrandConfig.SupportLine}", "Try Again");
                        return;
                    }

                    // Secure session persistence
                    await SessionService.SaveAsync(new AgentSession
                    {
                        Email = userName,
                        Name = result.agent.name ?? "Unknown",
                        Category = result.agent.category ?? "Default",
                        CollectionPoint = result.agent.collectionPoint ?? string.Empty,
                        SuperAgent = result.agent.superAgent ?? string.Empty,
                        Message = result.message ?? string.Empty
                    }, result.agent.pin ?? string.Empty);

                    // Remember the username for next launch (never the password)
                    if (_rememberMe) Preferences.Set(RememberedUserKey, userName);
                    else Preferences.Remove(RememberedUserKey);

                    UserDialogs.Instance.Toast("✅ Authentication Successful!", TimeSpan.FromSeconds(2));
                    await Task.Delay(250);

                    await NavigationRouter.GoToCategoryDashboardAsync(
                        result.agent.category, result.agent.name);
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("❌ Unexpected Error",
                    $"An error occurred: {ex.Message}\n\nContact support if issue persists:\n{BrandConfig.SupportLine}", "OK");
            }
            finally
            {
                SignInButton.IsEnabled = true;
            }
        }
    }

    internal class LoginResponse
    {
        public string responseCode { get; set; }
        public string message { get; set; }
        public Agent agent { get; set; }
    }

    internal class Agent
    {
        public string name { get; set; }
        public string password { get; set; }
        public string email { get; set; }
        public string category { get; set; }
        public string collectionPoint { get; set; }
        public string pin { get; set; }
        public string superAgent { get; set; }
    }
}