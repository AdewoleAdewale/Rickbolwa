using Acr.UserDialogs;
using Newtonsoft.Json;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class History : ContentPage
    {
        #region Data Models
        public class HistoryData
        {
            public string transactionId { get; set; } = string.Empty;
            public string businessName { get; set; } = string.Empty;
            public string serviceName { get; set; } = string.Empty;
            public string payerId { get; set; } = string.Empty;
            public decimal amount { get; set; }

            private string _dateRecorded = string.Empty;
            public string dateRecorded
            {
                get => _dateRecorded;
                set
                {
                    _dateRecorded = value ?? string.Empty;
                    ParsedDate = ParseDate(value);
                }
            }

            public DateTime ParsedDate { get; private set; }

            private DateTime ParseDate(string dateString)
            {
                if (string.IsNullOrWhiteSpace(dateString))
                    return DateTime.Now;

                // Try multiple date formats
                string[] formats = new[]
                {
                    "yyyy-MM-ddTHH:mm:ss",
                    "yyyy-MM-ddTHH:mm:ss.fff",
                    "yyyy-MM-ddTHH:mm:ss.fffZ",
                    "MM/dd/yyyy HH:mm:ss",
                    "MM/dd/yyyy",
                    "dd/MM/yyyy HH:mm:ss",
                    "dd/MM/yyyy",
                    "yyyy-MM-dd HH:mm:ss",
                    "yyyy-MM-dd"
                };

                foreach (var format in formats)
                {
                    if (DateTime.TryParseExact(dateString, format, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime result))
                    {
                        return result;
                    }
                }

                // Try standard parse
                if (DateTime.TryParse(dateString, out DateTime parsedDate))
                {
                    return parsedDate;
                }

                return DateTime.Now;
            }

            public string FormattedDate => ParsedDate.ToString("MMM dd, yyyy h:mm tt");
            public string FormattedAmount => $"₦{amount:N2}";
        }

        public class HistoryDataHeaderFooter : INotifyPropertyChanged
        {
            private List<HistoryData> _hd = new List<HistoryData>();

            public List<HistoryData> HD
            {
                get => _hd;
                set
                {
                    _hd = value ?? new List<HistoryData>();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Size));
                    OnPropertyChanged(nameof(Summary));
                    OnPropertyChanged(nameof(TotalAmount));
                    OnPropertyChanged(nameof(FormattedTotalAmount));
                }
            }

            public string Summary => $"Total: {HD.Count} transaction{(HD.Count != 1 ? "s" : "")}";
            public double Size => Math.Max(HD.Count * 200, 400); // Increased for better visibility
            public decimal TotalAmount => HD.Sum(x => x.amount);
            public string FormattedTotalAmount => $"₦{TotalAmount:N2}";

            public event PropertyChangedEventHandler PropertyChanged;
            protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
        #endregion

        #region Private Fields
        private HttpClient _httpClient;
        private bool _isLoading = false;
        private const int REQUEST_TIMEOUT_SECONDS = 30;
        private HistoryDataHeaderFooter _dataContext;
        #endregion

        public History()
        {
            try
            {
                InitializeComponent();
                InitializePage();
                ConfigureSSL();
                TrackUserActivity();
                _httpClient = CreateHttpClient();
            }
            catch (Exception ex)
            {
                HandleException(ex, "Error initializing page");
            }
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
                    "YIRS", "Logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

                string logFile = Path.Combine(logDir, $"error_log_{DateTime.Now:yyyy-MM-dd}.txt");
                File.AppendAllText(logFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {method}: {ex?.Message}\n{ex?.StackTrace}\n\n");
            }
            catch { }
        }


        #region Initialization
        private void InitializePage()
        {
            try
            {
                // Set default dates
                endDatePicker.Date = DateTime.Now;
                startDatePicker.Date = DateTime.Now.AddDays(-30);

                // Initialize binding context
                _dataContext = new HistoryDataHeaderFooter();
                BindingContext = _dataContext;

                // Hide all sections initially
                HideAllSections();

                SessionManager.Instance.UpdateActivity();
                System.Diagnostics.Debug.WriteLine("Page initialized successfully");
            }
            catch (Exception ex)
            {
                HandleException(ex, "Error setting up initial page state");
            }
        }

        private void ConfigureSSL()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                ServicePointManager.ServerCertificateValidationCallback = ValidateServerCertificate;
                ServicePointManager.DefaultConnectionLimit = 10;
                ServicePointManager.Expect100Continue = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SSL Configuration Error: {ex.Message}");
            }
        }

        private bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            System.Diagnostics.Debug.WriteLine($"SSL Error: {sslPolicyErrors}");

            // For production, implement proper certificate validation
            // For now, accepting all certificates
            return true;
        }

        private HttpClient CreateHttpClient()
        {
            try
            {
                var httpClientHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
                    SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                   System.Security.Authentication.SslProtocols.Tls11
                };

                var client = new HttpClient(httpClientHandler)
                {
                    Timeout = TimeSpan.FromSeconds(REQUEST_TIMEOUT_SECONDS)
                };

                return client;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HttpClient Creation Error: {ex.Message}");
                // Fallback to basic HttpClient
                return new HttpClient { Timeout = TimeSpan.FromSeconds(REQUEST_TIMEOUT_SECONDS) };
            }
        }
        #endregion

        #region Event Handlers
        private async void Button_Clicked(object sender, EventArgs e)
        {
            if (_isLoading)
            {
                await DisplayAlert("Please Wait", "A search is already in progress.", "OK");
                return;
            }

            try
            {
                await SearchTransactions();

                SessionManager.Instance.UpdateActivity();
            }
            catch (Exception ex)
            {
                HandleException(ex, "Error during transaction search");
            }
        }
        #endregion

        #region Main Business Logic
        private async Task SearchTransactions()
        {
            _isLoading = true;

            try
            {
                System.Diagnostics.Debug.WriteLine("Starting transaction search...");

                // Validate inputs
                if (!ValidateInputs())
                {
                    System.Diagnostics.Debug.WriteLine("Input validation failed");
                    return;
                }

                // Show loading state
                ShowLoadingState();

                // Prepare search parameters
                var searchParams = PrepareSearchParameters();
                System.Diagnostics.Debug.WriteLine($"API URL: {searchParams.url}");

                // Make API call
                var transactions = await FetchTransactionsAsync(searchParams.url);
                System.Diagnostics.Debug.WriteLine($"Fetched {transactions?.Count ?? 0} transactions");

                // Process and display results
                await ProcessTransactionResults(transactions);
            }
            catch (HttpRequestException httpEx)
            {
                System.Diagnostics.Debug.WriteLine($"Network Error: {httpEx.Message}");
                HandleNetworkError(httpEx);
            }
            catch (TaskCanceledException timeoutEx)
            {
                System.Diagnostics.Debug.WriteLine($"Timeout Error: {timeoutEx.Message}");
                HandleTimeoutError(timeoutEx);
            }
            catch (JsonException jsonEx)
            {
                System.Diagnostics.Debug.WriteLine($"JSON Error: {jsonEx.Message}");
                HandleJsonError(jsonEx);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"General Error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex.StackTrace}");
                HandleGeneralError(ex);
            }
            finally
            {
                HideLoadingState();
                _isLoading = false;
            }
        }

        private void HandleGeneralError(Exception ex)
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                ShowErrorState($"An unexpected error occurred: {ex.Message}");
            });
        }

        private void HandleJsonError(JsonException jsonEx)
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                ShowErrorState("Invalid data format received from server. The server may be experiencing issues.");
            });
        }

        private void HandleTimeoutError(TaskCanceledException timeoutEx)
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                ShowErrorState("Request timed out. Please check your internet connection and try again.");
            });
        }

        private void HandleNetworkError(HttpRequestException httpEx)
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                ShowErrorState("Network connection error. Please check your internet connection and try again.");
            });
        }

        private bool ValidateInputs()
        {
            try
            {
                // Validate date range
                if (startDatePicker.Date > endDatePicker.Date)
                {
                    ShowValidationError("Start date cannot be later than end date.");
                    return false;
                }

                // Validate date range is not too large
                var daysDifference = (endDatePicker.Date - startDatePicker.Date).TotalDays;
                if (daysDifference > 365)
                {
                    ShowValidationError("Date range cannot exceed 365 days.");
                    return false;
                }

                // Validate user email
                if (string.IsNullOrWhiteSpace(MainPage.ValidUserMail))
                {
                    ShowValidationError("User email not found. Please log in again.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                HandleException(ex, "Error validating inputs");
                return false;
            }
        }

        private void ShowValidationError(string message)
        {
            Device.BeginInvokeOnMainThread(async () =>
            {
                await DisplayAlert("Validation Error", message, "OK");
            });
        }

        private (string url, string fromDate, string toDate) PrepareSearchParameters()
        {
            try
            {


                string SearchStringFrom = Convert.ToString(startDatePicker.Date.ToString("MM/dd/yyyy"));
                string SearchStringTo = Convert.ToString(endDatePicker.Date.ToString("MM/dd/yyyy"));

                //call osoftpay for agent list
                string url = "https://rickbolwa.osoftpay.net/api/TaskPayers/gettransaction?Email=" + MainPage.ValidUserMail + "&SearchFrom=" + SearchStringFrom + "&SearchTo=" + SearchStringTo;

                return (url, SearchStringFrom, SearchStringTo);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Error preparing search parameters");
                throw;
            }
        }

        private async Task<List<HistoryData>> FetchTransactionsAsync(string url)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Fetching from URL: {url}");

                using (var response = await _httpClient.GetAsync(url))
                {
                    System.Diagnostics.Debug.WriteLine($"Response Status: {response.StatusCode}");

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        System.Diagnostics.Debug.WriteLine($"Error Response: {errorContent}");
                        throw new HttpRequestException($"Server returned {response.StatusCode}: {response.ReasonPhrase}");
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"Response Length: {json?.Length ?? 0}");
                    System.Diagnostics.Debug.WriteLine($"Response Sample: {(json?.Length > 200 ? json.Substring(0, 200) : json)}");

                    if (string.IsNullOrWhiteSpace(json))
                    {
                        System.Diagnostics.Debug.WriteLine("Server returned empty response");
                        return new List<HistoryData>();
                    }

                    // Try to deserialize the JSON
                    List<HistoryData> transactions = null;

                    try
                    {
                        transactions = JsonConvert.DeserializeObject<List<HistoryData>>(json);
                        System.Diagnostics.Debug.WriteLine($"Deserialized {transactions?.Count ?? 0} transactions");
                    }
                    catch (JsonException jsonEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"JSON Deserialization Error: {jsonEx.Message}");
                        System.Diagnostics.Debug.WriteLine($"JSON Content: {json}");
                        throw;
                    }

                    if (transactions == null)
                    {
                        System.Diagnostics.Debug.WriteLine("Deserialization returned null");
                        return new List<HistoryData>();
                    }

                    // Validate and clean data
                    var cleanedTransactions = ValidateAndCleanTransactionData(transactions);
                    System.Diagnostics.Debug.WriteLine($"Cleaned transactions count: {cleanedTransactions.Count}");

                    return cleanedTransactions;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"API Error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex.StackTrace}");
                throw;
            }
        }

        private List<HistoryData> ValidateAndCleanTransactionData(List<HistoryData> transactions)
        {
            try
            {
                var cleanedTransactions = new List<HistoryData>();

                foreach (var transaction in transactions)
                {
                    if (transaction == null) continue;

                    // Ensure required fields are not null
                    transaction.transactionId = transaction.transactionId ?? "N/A";
                    transaction.businessName = transaction.businessName ?? "Unknown";
                    transaction.serviceName = transaction.serviceName ?? "Unknown Service";
                    transaction.payerId = transaction.payerId ?? "N/A";

                    if (string.IsNullOrWhiteSpace(transaction.dateRecorded))
                    {
                        transaction.dateRecorded = DateTime.Now.ToString();
                    }

                    cleanedTransactions.Add(transaction);
                }

                return cleanedTransactions;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error validating transaction data: {ex.Message}");
                return new List<HistoryData>();
            }
        }

        private async Task ProcessTransactionResults(List<HistoryData> transactions)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"Processing {transactions?.Count ?? 0} transactions");

                await Device.InvokeOnMainThreadAsync(() =>
                {
                    try
                    {
                        // Update the data context
                        _dataContext.HD = transactions ?? new List<HistoryData>();

                        // Force binding update
                        BindingContext = null;
                        BindingContext = _dataContext;

                        System.Diagnostics.Debug.WriteLine($"BindingContext updated with {_dataContext.HD.Count} items");

                        if (transactions != null && transactions.Count > 0)
                        {
                            ShowResultsState(transactions);
                        }
                        else
                        {
                            ShowEmptyState();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error updating UI: {ex.Message}");
                        HandleException(ex, "Error updating UI with results");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing transaction results: {ex.Message}");
                HandleException(ex, "Error processing transaction results");
            }
        }
        #endregion

        #region UI State Management
        private void ShowLoadingState()
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    loadingOverlay.IsVisible = true;
                    HideAllSections();
                    System.Diagnostics.Debug.WriteLine("Loading state shown");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error showing loading state: {ex.Message}");
                }
            });
        }

        private void HideLoadingState()
        {
            Device.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    loadingOverlay.IsVisible = false;
                    System.Diagnostics.Debug.WriteLine("Loading state hidden");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error hiding loading state: {ex.Message}");
                }
            });
        }

        private void ShowResultsState(List<HistoryData> transactions)
        {
            try
            {
                HideAllSections();
                resultsSection.IsVisible = true;

                var totalAmount = transactions.Sum(t => t.amount);
                summaryLabel.Text = $"Found {transactions.Count} transaction{(transactions.Count != 1 ? "s" : "")} • Total: ₦{totalAmount:N2}";

                // Force ListView to refresh
                listView.ItemsSource = null;
                listView.ItemsSource = transactions;

                System.Diagnostics.Debug.WriteLine($"Results state shown with {transactions.Count} items");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing results state: {ex.Message}");
                HandleException(ex, "Error showing results state");
            }
        }

        private void HideAllSections()
        {
            try
            {
                resultsSection.IsVisible = false;
                emptyStateSection.IsVisible = false;
                errorStateSection.IsVisible = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error hiding sections: {ex.Message}");
            }
        }

        private void ShowEmptyState()
        {
            try
            {
                HideAllSections();
                emptyStateSection.IsVisible = true;
                System.Diagnostics.Debug.WriteLine("Empty state shown");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing empty state: {ex.Message}");
                HandleException(ex, "Error showing empty state");
            }
        }

        private void ShowErrorState(string errorMessage = null)
        {
            try
            {
                HideAllSections();
                errorStateSection.IsVisible = true;

                if (!string.IsNullOrEmpty(errorMessage) && errorMessageLabel != null)
                {
                    errorMessageLabel.Text = errorMessage;
                }

                System.Diagnostics.Debug.WriteLine($"Error state shown: {errorMessage}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error showing error state: {ex.Message}");
            }
        }

        private void HandleException(Exception ex, string context)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"{context}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex.StackTrace}");

                Device.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        await DisplayAlert("Error", $"{context}\n\nDetails: {ex.Message}", "OK");
                    }
                    catch (Exception displayEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error showing alert: {displayEx.Message}");
                    }
                });
            }
            catch (Exception handleEx)
            {
                System.Diagnostics.Debug.WriteLine($"Error in HandleException: {handleEx.Message}");
            }
        }
        #endregion

        #region Cleanup
        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            try
            {
                _httpClient?.CancelPendingRequests();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during cleanup: {ex.Message}");
            }
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            System.Diagnostics.Debug.WriteLine("Page appeared");
        }

        public void Dispose()
        {
            try
            {
                _httpClient?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error disposing HttpClient: {ex.Message}");
            }
        }
        #endregion
    }
}