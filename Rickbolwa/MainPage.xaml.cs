using Acr.UserDialogs;
using Newtonsoft.Json;
using Rickbolwa.Helper;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xamarin.Essentials;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class MainPage : ContentPage
    {
        #region Static Properties
        // Backed by the persistent session. Every existing read across the five modules
        // (MainPage.ValidUserMail, MainPage.Pin, MainPage.Name, MainPage.Category, …)
        // keeps working and now survives process death.
        public static string Name
        {
            get => SessionManager.Name;
            set => SessionManager.Name = value;
        }

        public static string ValidUserMail
        {
            get => SessionManager.Email;
            set => SessionManager.Email = value;
        }

        public static string Passwords
        {
            get => SessionManager.Password;
            set => SessionManager.Password = value;
        }

        public static string Pin
        {
            get => SessionManager.Pin;
            set => SessionManager.Pin = value;
        }

        public static string Super_Agent
        {
            get => SessionManager.SuperAgent;
            set => SessionManager.SuperAgent = value;
        }

        public static string Message
        {
            get => SessionManager.Message;
            set => SessionManager.Message = value;
        }

        public static string Category
        {
            get => SessionManager.Category;
            set => SessionManager.Category = value;
        }

        public static string CollectionPoint
        {
            get => SessionManager.CollectionPoint;
            set => SessionManager.CollectionPoint = value;
        }
        #endregion

        #region Private Fields
        private const int MAX_LOGIN_ATTEMPTS = 5;
        private const int LOCKOUT_DURATION_MINUTES = 1;
        private const int REQUEST_TIMEOUT_SECONDS = 30;
        private readonly HttpClient _httpClient;
        private static int _loginAttempts = 0;
        private static DateTime? _lockoutUntil = null;
        private bool _isProcessing = false;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isAnimating = false;
        #endregion

        #region Constructor
        public MainPage()
        {
            try
            {
                InitializeComponent();
                this.BindingContext = new MainPageViewModel();

                // Initialize HTTP client with proper SSL configuration
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                    {
                        // WARNING: Accepting all certificates is insecure for production
                        // Only use this for development/testing
                        // For production, implement proper certificate validation
                        if (errors == System.Net.Security.SslPolicyErrors.None)
                            return true;

                        System.Diagnostics.Debug.WriteLine($"SSL Error: {errors}");
                        System.Diagnostics.Debug.WriteLine($"Certificate Subject: {cert?.Subject}");

                        // Accept all certificates for development
                        return true;
                    },
                    SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                   System.Security.Authentication.SslProtocols.Tls11
                };

                _httpClient = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(REQUEST_TIMEOUT_SECONDS)
                };

                // Set SecurityProtocol for older .NET Framework compatibility
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;

                CheckLockoutStatus();
            }
            catch (Exception ex)
            {
                LogError("Constructor", ex);
                ShowErrorToast("Failed to initialize. Please restart the app.");
            }
        }
        #endregion

        #region Lifecycle Methods
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            try
            {
                // A live session means this page should never be shown.
                if (SessionManager.IsAuthenticated)
                {
                    Device.BeginInvokeOnMainThread(() => App.SetRoot(App.ResolveLandingPage()));
                    return;
                }

                CheckLockoutStatus();
                await AnimatePageAppearance();
                await LoadSavedCredentials();
            }
            catch (Exception ex)
            {
                LogError("OnAppearing", ex);
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            try
            {
                _cancellationTokenSource?.Cancel();
            }
            catch (Exception ex)
            {
                LogError("OnDisappearing", ex);
            }
        }
        #endregion

        #region Credential Management
        /// <summary>
        /// Load saved credentials if "Remember Me" was checked
        /// </summary>
        private async Task LoadSavedCredentials()
        {
            try
            {
                bool hasSavedCredentials = await SecureStorageService.HasSavedCredentialsAsync();

                if (hasSavedCredentials)
                {
                    var credentials = await SecureStorageService.GetCredentialsAsync();

                    if (credentials != null)
                    {
                        Email.Text = credentials.Email;
                        Password.Text = credentials.Password;
                        RememberMeCheckbox.IsChecked = true;

                        // Show subtle animation to indicate auto-fill
                        await Task.WhenAll(
                            EmailBorder.FadeTo(0.7, 200),
                            PasswordBorder.FadeTo(0.7, 200)
                        );
                        await Task.WhenAll(
                            EmailBorder.FadeTo(1, 200),
                            PasswordBorder.FadeTo(1, 200)
                        );

                        System.Diagnostics.Debug.WriteLine("Credentials auto-filled from secure storage");
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("LoadSavedCredentials", ex);
            }
        }

        /// <summary>
        /// Handle Remember Me label tap
        /// </summary>
        private void OnRememberMeLabelTapped(object sender, EventArgs e)
        {
            RememberMeCheckbox.IsChecked = !RememberMeCheckbox.IsChecked;
        }
        #endregion

        #region Animation Methods
        private async Task AnimatePageAppearance()
        {
            if (_isAnimating) return;
            _isAnimating = true;

            try
            {
                LogoContainer.Scale = 0;
                LogoContainer.Opacity = 0;
                WelcomeText.TranslationY = -30;
                WelcomeText.Opacity = 0;
                SubtitleText.TranslationY = -20;
                SubtitleText.Opacity = 0;
                LoginCard.TranslationY = 50;
                LoginCard.Opacity = 0;
                FooterSection.Opacity = 0;

                var logoAnimation = LogoContainer.FadeTo(1, 300);
                var logoScale = LogoContainer.ScaleTo(1, 400, Easing.SpringOut);
                await Task.WhenAll(logoAnimation, logoScale);

                await Task.WhenAll(
                    WelcomeText.FadeTo(1, 400),
                    WelcomeText.TranslateTo(0, 0, 400, Easing.CubicOut)
                );

                await Task.Delay(100);

                await Task.WhenAll(
                    SubtitleText.FadeTo(1, 400),
                    SubtitleText.TranslateTo(0, 0, 400, Easing.CubicOut)
                );

                await Task.WhenAll(
                    LoginCard.FadeTo(1, 500),
                    LoginCard.TranslateTo(0, 0, 500, Easing.CubicOut)
                );

                await FooterSection.FadeTo(1, 400);

                _ = AnimateLogoPulse();
            }
            catch (Exception ex)
            {
                LogError("AnimatePageAppearance", ex);
            }
            finally
            {
                _isAnimating = false;
            }
        }

        private async Task AnimateLogoPulse()
        {
            try
            {
                while (true)
                {
                    await LogoContainer.ScaleTo(1.05, 1000, Easing.SinInOut);
                    await LogoContainer.ScaleTo(1.0, 1000, Easing.SinInOut);
                    await Task.Delay(2000);
                }
            }
            catch { }
        }

        private async Task AnimateInputFocus(View border, bool isFocused)
        {
            try
            {
                var borderView = border as Xamarin.Forms.PancakeView.PancakeView;
                if (borderView == null) return;

                if (isFocused)
                {
                    await Task.WhenAll(
                        borderView.ScaleTo(1.02, 150, Easing.CubicOut),
                        Task.Run(async () =>
                        {
                            await Task.Delay(50);
                            Device.BeginInvokeOnMainThread(() =>
                            {
                                borderView.BorderColor = Color.FromHex("#00D084");
                                borderView.BorderThickness = 2;
                            });
                        })
                    );
                }
                else
                {
                    await borderView.ScaleTo(1.0, 150, Easing.CubicIn);
                    borderView.BorderColor = Color.FromHex("#2C3E50");
                }
            }
            catch (Exception ex)
            {
                LogError("AnimateInputFocus", ex);
            }
        }

        private async Task AnimateButtonPress()
        {
            try
            {
                await SignInButton.ScaleTo(0.95, 100);
                await SignInButton.ScaleTo(1.0, 100, Easing.SpringOut);
            }
            catch (Exception ex)
            {
                LogError("AnimateButtonPress", ex);
            }
        }

        private async Task AnimateLoadingSpinner()
        {
            try
            {
                while (LoadingOverlay.IsVisible)
                {
                    await Task.WhenAll(
                        LoaderRing1.RotateTo(360, 2000, Easing.Linear),
                        LoaderRing2.RotateTo(-360, 1500, Easing.Linear)
                    );

                    LoaderRing1.Rotation = 0;
                    LoaderRing2.Rotation = 0;
                }
            }
            catch { }
        }

        private async Task ShakeAnimation(View view)
        {
            try
            {
                await view.TranslateTo(-15, 0, 50);
                await view.TranslateTo(15, 0, 50);
                await view.TranslateTo(-10, 0, 50);
                await view.TranslateTo(10, 0, 50);
                await view.TranslateTo(-5, 0, 50);
                await view.TranslateTo(5, 0, 50);
                await view.TranslateTo(0, 0, 50);
            }
            catch (Exception ex)
            {
                LogError("ShakeAnimation", ex);
            }
        }

        private async Task ShowSuccessAnimation()
        {
            try
            {
                await Device.InvokeOnMainThreadAsync(async () =>
                {
                    LoginCard.BackgroundColor = Color.FromHex("#1B5E20");
                    await Task.WhenAll(
                        LoginCard.ScaleTo(1.05, 200),
                        LoginCard.FadeTo(0.8, 200)
                    );
                    await Task.WhenAll(
                        LoginCard.ScaleTo(1.0, 200),
                        LoginCard.FadeTo(1.0, 200)
                    );
                });
            }
            catch (Exception ex)
            {
                LogError("ShowSuccessAnimation", ex);
            }
        }
        #endregion

        #region Focus Event Handlers
        private async void OnEmailFocused(object sender, FocusEventArgs e)
        {
            try
            {
                await AnimateInputFocus(EmailBorder, true);
            }
            catch (Exception ex)
            {
                LogError("OnEmailFocused", ex);
            }
        }

        private async void OnPasswordFocused(object sender, FocusEventArgs e)
        {
            try
            {
                await AnimateInputFocus(PasswordBorder, true);
            }
            catch (Exception ex)
            {
                LogError("OnPasswordFocused", ex);
            }
        }

        private void OnForgotPasswordTapped(object sender, EventArgs e)
        {
            try
            {
                Device.BeginInvokeOnMainThread(async () =>
                {
                    await DisplayAlert("Forgot Password",
                        "Please contact your administrator to reset your password.",
                        "OK");
                });
            }
            catch (Exception ex)
            {
                LogError("OnForgotPasswordTapped", ex);
            }
        }

        private async void OnDismissErrorToast(object sender, EventArgs e)
        {
            try
            {
                await HideErrorToast();
            }
            catch (Exception ex)
            {
                LogError("OnDismissErrorToast", ex);
            }
        }
        #endregion

        #region Validation Methods
        private void OnEmailTextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    ClearEmailError();
                }
            }
            catch (Exception ex)
            {
                LogError("OnEmailTextChanged", ex);
            }
        }

        private void OnPasswordTextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    ClearPasswordError();
                }
            }
            catch (Exception ex)
            {
                LogError("OnPasswordTextChanged", ex);
            }
        }

        private async void OnEmailUnfocused(object sender, FocusEventArgs e)
        {
            try
            {
                await AnimateInputFocus(EmailBorder, false);
                ValidateEmail(Email.Text);
            }
            catch (Exception ex)
            {
                LogError("OnEmailUnfocused", ex);
            }
        }

        private async void OnPasswordUnfocused(object sender, FocusEventArgs e)
        {
            try
            {
                await AnimateInputFocus(PasswordBorder, false);
                ValidatePassword(Password.Text);
            }
            catch (Exception ex)
            {
                LogError("OnPasswordUnfocused", ex);
            }
        }

        private bool ValidateEmail(string email)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(email))
                {
                    ShowEmailError("Email address is required");
                    return false;
                }

                string emailPattern = @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$";
                if (!Regex.IsMatch(email.Trim(), emailPattern))
                {
                    ShowEmailError("Please enter a valid email address");
                    return false;
                }

                if (email.Length > 254)
                {
                    ShowEmailError("Email address is too long");
                    return false;
                }

                ClearEmailError();
                return true;
            }
            catch (Exception ex)
            {
                LogError("ValidateEmail", ex);
                return false;
            }
        }

        private bool ValidatePassword(string password)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(password))
                {
                    ShowPasswordError("Password is required");
                    return false;
                }

                if (password.Length < 4)
                {
                    ShowPasswordError("Password must be at least 4 characters");
                    return false;
                }

                if (password.Length > 128)
                {
                    ShowPasswordError("Password is too long");
                    return false;
                }

                ClearPasswordError();
                return true;
            }
            catch (Exception ex)
            {
                LogError("ValidatePassword", ex);
                return false;
            }
        }

        private async void ShowEmailError(string message)
        {
            try
            {
                await Device.InvokeOnMainThreadAsync(async () =>
                {
                    EmailError.Text = message;
                    EmailError.Opacity = 0;
                    EmailError.IsVisible = true;
                    EmailBorder.BorderColor = Color.FromHex("#FF6B6B");

                    await Task.WhenAll(
                        EmailError.FadeTo(1, 200),
                        EmailBorder.ScaleTo(1.02, 100),
                        EmailBorder.ScaleTo(1.0, 100)
                    );
                });
            }
            catch (Exception ex)
            {
                LogError("ShowEmailError", ex);
            }
        }

        private async void ShowPasswordError(string message)
        {
            try
            {
                await Device.InvokeOnMainThreadAsync(async () =>
                {
                    PasswordError.Text = message;
                    PasswordError.Opacity = 0;
                    PasswordError.IsVisible = true;
                    PasswordBorder.BorderColor = Color.FromHex("#FF6B6B");

                    await Task.WhenAll(
                        PasswordError.FadeTo(1, 200),
                        PasswordBorder.ScaleTo(1.02, 100),
                        PasswordBorder.ScaleTo(1.0, 100)
                    );
                });
            }
            catch (Exception ex)
            {
                LogError("ShowPasswordError", ex);
            }
        }

        private void ClearEmailError()
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                EmailError.IsVisible = false;
                EmailBorder.BorderColor = Color.FromHex("#2C3E50");
            });
        }

        private void ClearPasswordError()
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                PasswordError.IsVisible = false;
                PasswordBorder.BorderColor = Color.FromHex("#2C3E50");
            });
        }
        #endregion

        #region Login Methods
        private async void LoginClick(object sender, EventArgs e)
        {
            try
            {
                if (_isProcessing)
                {
                    return;
                }

                await AnimateButtonPress();

                if (!CheckLockoutStatus())
                {
                    return;
                }

                string myEmail = Email?.Text?.Trim();
                string myPassword = Password?.Text;

                bool isEmailValid = ValidateEmail(myEmail);
                bool isPasswordValid = ValidatePassword(myPassword);

                if (!isEmailValid || !isPasswordValid)
                {
                    ShowErrorToast("Please correct the errors and try again");
                    await ShakeAnimation(LoginCard);
                    return;
                }

                await PerformLoginAsync(myEmail, myPassword);
            }
            catch (Exception ex)
            {
                LogError("LoginClick", ex);
                ShowErrorToast("An unexpected error occurred. Please try again.");
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private async Task PerformLoginAsync(string email, string password)
        {
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                _isProcessing = true;
                await ShowLoading(true, "Signing in...", "Verifying your credentials");
                DisableLoginButton();

                LoginResponse result = await LoginRequestAsync(email, password, _cancellationTokenSource.Token);

                if (result == null)
                {
                    throw new Exception("No response received from server");
                }

                await HandleLoginResponse(result, email, password);
            }
            catch (OperationCanceledException)
            {
                ShowErrorToast("Login request was cancelled");
            }
            catch (HttpRequestException ex)
            {
                LogError("PerformLoginAsync - HTTP", ex);
                ShowErrorToast("Unable to connect to server. Check your internet connection.");
            }
            catch (JsonException ex)
            {
                LogError("PerformLoginAsync - JSON", ex);
                ShowErrorToast("Received invalid data from server");
            }
            catch (Exception ex)
            {
                LogError("PerformLoginAsync", ex);
                ShowErrorToast($"Login failed: {ex.Message}");
            }
            finally
            {
                await ShowLoading(false);
                EnableLoginButton();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private async Task<LoginResponse> LoginRequestAsync(string email, string password, CancellationToken cancellationToken)
        {
            try
            {
                string sanitizedEmail = Uri.EscapeDataString(email);
                string sanitizedPassword = Uri.EscapeDataString(password);

                string url = $"https://rickbolwa.osoftpay.net/api/Taskpayers/v1/AgentLogin?UserName={sanitizedEmail}&Password={sanitizedPassword}";

                System.Diagnostics.Debug.WriteLine($"Attempting login to: {url}");

                using (var response = await _httpClient.GetAsync(url, cancellationToken))
                {
                    System.Diagnostics.Debug.WriteLine($"Response Status: {response.StatusCode}");

                    response.EnsureSuccessStatusCode();

                    string json = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"Response JSON: {json}");

                    if (string.IsNullOrWhiteSpace(json))
                    {
                        throw new Exception("Empty response received from server");
                    }

                    LoginResponse result = JsonConvert.DeserializeObject<LoginResponse>(json);

                    if (result == null)
                    {
                        throw new JsonException("Failed to deserialize response");
                    }

                    return result;
                }
            }
            catch (Exception ex)
            {
                LogError("LoginRequestAsync", ex);
                throw;
            }
        }

        private async Task HandleLoginResponse(LoginResponse result, string email, string password)
        {
            try
            {
                if (result.responseCode == "00" && result.agent != null)
                {
                    _loginAttempts = 0;
                    _lockoutUntil = null;

                    if (string.IsNullOrWhiteSpace(result.agent.category))
                    {
                        throw new Exception("Invalid agent category received");
                    }

                    // Save credentials if Remember Me is checked
                    bool rememberMe = RememberMeCheckbox.IsChecked;
                    await SecureStorageService.SaveCredentialsAsync(email, password, rememberMe);

                    bool sessionOpened = await SessionManager.SignInAsync(new SessionData
                    {
                        Email = email,
                        Name = result.agent.name ?? "Unknown",
                        Password = result.agent.password ?? string.Empty,
                        Pin = result.agent.pin ?? string.Empty,
                        Category = result.agent.category,
                        CollectionPoint = result.agent.collectionPoint ?? string.Empty,
                        SuperAgent = result.agent.SuperAgent ?? string.Empty,
                        Message = result.message ?? string.Empty
                    });

                    if (!sessionOpened)
                    {
                        ShowErrorToast("Signed in, but the session could not be saved on this device.");
                    }

                    await ShowSuccessAnimation();

                    await NavigateToAppropriatePageAsync(result.agent);
                }
                else
                {
                    _loginAttempts++;

                    string errorMessage = !string.IsNullOrWhiteSpace(result.message)
                        ? result.message
                        : "Invalid login credentials";

                    int attemptsRemaining = MAX_LOGIN_ATTEMPTS - _loginAttempts;

                    if (_loginAttempts >= MAX_LOGIN_ATTEMPTS)
                    {
                        _lockoutUntil = DateTime.Now.AddMinutes(LOCKOUT_DURATION_MINUTES);
                        ShowErrorToast($"Account locked for {LOCKOUT_DURATION_MINUTES} minutes");
                        await ShakeAnimation(LoginCard);
                    }
                    else
                    {
                        ShowErrorToast($"{errorMessage} ({attemptsRemaining} attempts left)");
                        await ShakeAnimation(LoginCard);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("HandleLoginResponse", ex);
                throw;
            }
        }

        private async Task NavigateToAppropriatePageAsync(Agent agent)
        {
            try
            {
                Page targetPage = null;
                string agentType = string.Empty;

                // Trim: the API returns "Damaturu " with a trailing space.
                // ToLowerInvariant: culture-independent, and the case labels below are all lowercase.
                switch ((agent.category ?? string.Empty).Trim().ToLowerInvariant())
                {
                   
                    case "hulage":
                        targetPage = new Views.DashBoard();
                        agentType = "Haulage Agent";
                        break;

             

                    default:
                        ShowErrorToast($"Unknown agent category: {agent.category}");
                        return;
                }

                if (targetPage != null)
                {
                    UserDialogs.Instance.Toast(
                        $"Welcome back, {agent.name}!\nSigned in as {agentType}",
                        TimeSpan.FromSeconds(3));

                    await Task.Delay(800);

                    Device.BeginInvokeOnMainThread(() =>
                    {
                        Application.Current.MainPage = new NavigationPage(targetPage)
                        {
                            BarBackgroundColor = Color.FromHex("#004225"),
                            BarTextColor = Color.White
                        };
                    });
                }
            }
            catch (Exception ex)
            {
                LogError("NavigateToAppropriatePageAsync", ex);
                throw;
            }
        }
        #endregion

        #region UI Helper Methods
        private bool CheckLockoutStatus()
        {
            try
            {
                if (_lockoutUntil.HasValue && DateTime.Now < _lockoutUntil.Value)
                {
                    TimeSpan remaining = _lockoutUntil.Value - DateTime.Now;
                    int minutesRemaining = (int)Math.Ceiling(remaining.TotalMinutes);

                    Device.BeginInvokeOnMainThread(() =>
                    {
                        SignInButton.IsEnabled = false;
                        SignInButton.Opacity = 0.5;
                    });

                    ShowErrorToast($"Account locked. Try again in {minutesRemaining} minute(s)");
                    return false;
                }
                else
                {
                    if (_lockoutUntil.HasValue)
                    {
                        _lockoutUntil = null;
                        _loginAttempts = 0;

                        Device.BeginInvokeOnMainThread(() =>
                        {
                            SignInButton.IsEnabled = true;
                            SignInButton.Opacity = 1.0;
                        });
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                LogError("CheckLockoutStatus", ex);
                return true;
            }
        }

        private async Task ShowLoading(bool show, string message = "Loading...", string subtext = "")
        {
            try
            {
                await Device.InvokeOnMainThreadAsync(async () =>
                {
                    if (show)
                    {
                        LoadingText.Text = message;
                        LoadingSubtext.Text = subtext;
                        LoadingOverlay.Opacity = 0;
                        LoadingOverlay.IsVisible = true;

                        await LoadingOverlay.FadeTo(1, 250);
                        _ = AnimateLoadingSpinner();
                    }
                    else
                    {
                        await LoadingOverlay.FadeTo(0, 250);
                        LoadingOverlay.IsVisible = false;
                    }
                });
            }
            catch (Exception ex)
            {
                LogError("ShowLoading", ex);
            }
        }

        private void DisableLoginButton()
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                SignInButtonText.Opacity = 0.5;
                SignInButtonLoader.IsVisible = true;
                SignInButtonLoader.IsRunning = true;
                SignInButton.IsEnabled = false;
            });
        }

        private void EnableLoginButton()
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                SignInButtonText.Opacity = 1.0;
                SignInButtonLoader.IsVisible = false;
                SignInButtonLoader.IsRunning = false;
                SignInButton.IsEnabled = true;
            });
        }

        private async void ShowErrorToast(string message)
        {
            try
            {
                await Device.InvokeOnMainThreadAsync(async () =>
                {
                    ErrorToastMessage.Text = message;
                    ErrorToast.TranslationY = -100;
                    ErrorToast.Opacity = 0;
                    ErrorToast.IsVisible = true;

                    await Task.WhenAll(
                        ErrorToast.TranslateTo(0, 0, 300, Easing.SpringOut),
                        ErrorToast.FadeTo(1, 300)
                    );

                    await Task.Delay(4000);

                    await HideErrorToast();
                });
            }
            catch (Exception ex)
            {
                LogError("ShowErrorToast", ex);
            }
        }

        private async Task HideErrorToast()
        {
            try
            {
                await Device.InvokeOnMainThreadAsync(async () =>
                {
                    if (ErrorToast.IsVisible)
                    {
                        await Task.WhenAll(
                            ErrorToast.TranslateTo(0, -100, 250, Easing.CubicIn),
                            ErrorToast.FadeTo(0, 250)
                        );
                        ErrorToast.IsVisible = false;
                    }
                });
            }
            catch (Exception ex)
            {
                LogError("HideErrorToast", ex);
            }
        }

        private void LogError(string method, Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ERROR] {method}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");

            // Log inner exceptions for SSL issues
            if (ex.InnerException != null)
            {
                System.Diagnostics.Debug.WriteLine($"[INNER ERROR]: {ex.InnerException.Message}");
            }
        }
        #endregion
    }

    #region Response Models
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
        public string SuperAgent { get; set; }
    }
    #endregion
}