using Acr.UserDialogs;
using Android.Accounts;
using DIPS.Xamarin.UI.Controls.Sheet;
using Newtonsoft.Json;
using Rickbolwa.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;
using Xamarin.Forms.PlatformConfiguration.iOSSpecific;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class Payment : ContentPage
    {
        private const int MAX_RETRY_ATTEMPTS = 3;
        List<GetSateCatData> enums;
        public Payment()
        {
            InitializeComponent();
            LoadSateCategory();
            sheetBehavior.IsOpen = false;
            makepaymentbutton.IsVisible = false;
            Servicename.Text = Verify.vehicleTypess;
            PayerId.Text = Verify.plateNumberss;



            if (Verify.amountss == "0" || Verify.amountss == "0.00" || Verify.amountss == "0.0" || Verify.amountss == "0.000")
            {
                Amount.IsEnabled = true;
                Amount.Text = Verify.amountss;
                Amount.IsReadOnly = false;
            }
            else
            {
                Amount.IsEnabled = false;
                Amount.Text = Verify.amountss;
                Amount.IsReadOnly = true;
            }

            ConfigureSSL();
        }

        private void ConfigureSSL()
        {
            // Configure SSL/TLS settings to handle certificate issues
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;

            // Set ServerCertificateValidationCallback to handle certificate validation
            ServicePointManager.ServerCertificateValidationCallback =
                new RemoteCertificateValidationCallback(ValidateServerCertificate);

            // Increase connection limits
            ServicePointManager.DefaultConnectionLimit = 10;
            ServicePointManager.Expect100Continue = true;
        }

        private bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // For production, you should implement proper certificate validation
            // This accepts all certificates - use with caution

            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            // Log the SSL error for debugging
            System.Diagnostics.Debug.WriteLine($"SSL Error: {sslPolicyErrors}");

            return Rickbolwa.Services.ApiClient.ValidateCertificate(sender, certificate, chain, sslPolicyErrors);
        }

        private void LoadSateCategory()
        {
            try
            {
                string url = "https://rickbolwa.osoftpay.net/api/taskpayers/getstate";

                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => Rickbolwa.Services.ApiClient.ValidateCertificate(message, cert, chain, errors),
                    SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls11
                };

                using (HttpClient client = new HttpClient(handler))
                {
                    using (HttpResponseMessage response = client.GetAsync(url).Result)
                    {
                        using (HttpContent content = response.Content)
                        {
                            var json = content.ReadAsStringAsync().Result;
                            MemoryStream memStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                            // convert to string
                            StreamReader reader = new StreamReader(memStream);
                            string text = reader.ReadToEnd();
                            List<GetSateCatData> items = JsonConvert.DeserializeObject<List<GetSateCatData>>(text);

                            picker.ItemDisplayBinding = new Binding("name");
                            picker.ItemsSource = items.ToList();
                            enums = items.ToList();

                        }
                    }
                }

            }
            catch (System.Exception exe)
            {

                exe.ToString();
            }
        }

        private void sheetBehavior_ActionClicked(object sender, EventArgs e)
        {
            sheetBehavior.Opened += sheetBehavior_ActionClicked;
        }

        private async void TapGestureRecognizer_Tapped(object sender, EventArgs e)
        {
            using (IProgressDialog progress = UserDialogs.Instance.Progress("Processing Vehicle Haulage Payment, Please Wait...", null, null, true, MaskType.Gradient))
            {
                for (int i = 0; i < 100; i++)
                {
                    progress.PercentComplete = i;
                    await Task.Delay(60);
                }

                int indexSelected = picker.SelectedIndex;
                indexSelected = indexSelected + 1;
                int count = 0;
                string CatNameSelected = "";
                foreach (var item in enums.ToList())
                {
                    count = count + 1;
                    if (count == indexSelected)
                    {
                        CatNameSelected = item.name;
                    }
                }

                decimal amount = Convert.ToDecimal(Amount.Text.Replace(",", ""));

                //PASSWORD FORCE CHANGE

                if (PIN.Text == null || CatNameSelected == null)
                {

                    await DisplayAlert("NOTIFICATION", "Kindly fill in all details before you proceed", "TRY AGAIN");

                }
                else if (PIN.Text != MainPage.Pin)
                {
                    await DisplayAlert("NOTIFICATION", "Incorrect Transaction Pin", "TRY AGAIN");
                }
                else if (PIN.Text == MainPage.Pin && CatNameSelected != null)
                {

                    try
                    {
                        if (amount > 0)
                        {
                            var httpClientHandler = new HttpClientHandler
                            {
                                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => Rickbolwa.Services.ApiClient.ValidateCertificate(message, cert, chain, errors),
                                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls11
                            };
                            string url = "https://rickbolwa.osoftpay.net/api/SingleCollections/Hulage";
                            var nvc = new List<KeyValuePair<string, string>>();
                            nvc.Add(new KeyValuePair<string, string>("ServiceName", Verify.vehicleTypess));
                            nvc.Add(new KeyValuePair<string, string>("Email", MainPage.ValidUserMail));
                            nvc.Add(new KeyValuePair<string, string>("Amount", Amount.Text));
                            nvc.Add(new KeyValuePair<string, string>("Pin", PIN.Text));
                            nvc.Add(new KeyValuePair<string, string>("Payer", Verify.plateNumberss));
                            nvc.Add(new KeyValuePair<string, string>("StateTo", CatNameSelected));
                            var client = new HttpClient(httpClientHandler);
                            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(nvc) };
                            var res = await client.SendAsync(req);
                            var resultString = await res.Content.ReadAsStringAsync();
                            var StateCollectionResponse = JsonConvert.DeserializeObject<StateCollectionResponseObject>(resultString);
                            if (StateCollectionResponse.RespondCode == "00")
                            {

                                sheetBehavior.IsOpen = true;
                                string receiptamount = "AMOUNT: N" + amount.ToString("###,###.00");

                                try
                                {

                                    await PrintHaulageReceiptAsync(StateCollectionResponse, amount, CatNameSelected);
                                }
                                catch (Exception ex)
                                {
                                    LogError(ex, "Failed to generate receipt");
                                    await DisplayAlert("Error", "Receipt generation failed", "OK");
                                }

                            }
                            else
                            {
                                sheetBehavior.IsOpen = false;
                                string action = await DisplayActionSheet(StateCollectionResponse.Message + ". Transaction Error. " + "Thank you for choosing IC!", null, null, "TRY AGAIN");

                            }
                        }
                        else
                        {
                            await DisplayAlert("NOTIFICATION", "Zero is an invalid amount to process", "OKAY");
                        }
                    }
                    catch (Exception exe)
                    {
                        await DisplayAlert("NOTIFICATION", "Check Your Internet, But Transaction Might Be Successful Kindly Go To History To Confirm", "OKAY");

                        exe.ToString();
                    }
                }
            }
        }

        private void LogError(Exception ex, string message)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR: {message}");
                if (ex != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Exception: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex.StackTrace}");
                    if (ex.InnerException != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                    }
                }
            }
            catch
            {
                // Fail silently if logging fails
            }
        }


        private void Pin_Unfocused(object sender, FocusEventArgs e)
        {
            makepaymentbutton.IsVisible = true;
        }

        private async void Button_Clicked(object sender, EventArgs e)
        {
            await Navigation.PushAsync(new Views.Verify());
        }

        private async System.Threading.Tasks.Task PrintHaulageReceiptAsync(
            StateCollectionResponseObject result, decimal amount, string stateTo)
        {
            var receipt = ReceiptPrinter.CreateBrandedReceipt();
            receipt.StoreSubTitle = "HAULAGE";
            receipt.ReceiptNumber = result.TransactionNo;
            receipt.AgentName = MainPage.Name;
            receipt.CollectionPoint = MainPage.CollectionPoint;

            receipt.Items.Add(new ReceiptItem
            { Description = "HAULAGE PAYMENT", Amount = 0m });
            receipt.Items.Add(new ReceiptItem
            { Description = "Vehicle", Amount = 0m, SubText = Verify.plateNumberss });
            receipt.Items.Add(new ReceiptItem
            { Description = "Destination", Amount = 0m, SubText = stateTo });
            receipt.Items.Add(new ReceiptItem
            { Description = "Fee", Amount = amount });

            receipt.TotalAmount = amount;
            receipt.AmountPaid = amount;

            await ReceiptPrinter.PrintAsync(receipt);
        }

    }

    internal class GetSateCatData
    {
        public string name { get; set; }
    }
    internal class StateCollectionResponseObject
    {
        public string RespondCode { get; set; }
        public string Message { get; set; }
        public AddSinglecollect addSinglecollect { get; set; }
        public string PrintCode { get; set; }

        public string TransactionNo { get; set; }


    }

    internal class AddSinglecollect
    {
        public string TransactionNo { get; set; }

    }


}