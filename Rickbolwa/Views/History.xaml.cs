using Acr.UserDialogs;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
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
        class HistoryData
        {
            public string transactionId { get; set; }
            public string businessName { get; set; }

            public string serviceName { get; set; }

            public string payerId { get; set; }
            public decimal amount { get; set; }

            public string dateRecorded { get; set; }


        }
        class HistoryDataHeaderFooter
        {
            public List<HistoryData> HD { get; set; }
            public string Intro { get { return " You have Performed a total of " + HD.Count + " transactions within your search dates"; } }
            public string Summary { get { return " You have Performed a total of " + HD.Count + " transactions"; } }
            public decimal Size { get { return HD.Count; } }

        }
        public History()
        {
            InitializeComponent();

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
        private void TapGestureRecognizer_Tapped(object sender, EventArgs e)
        {
            Application.Current.MainPage = new NavigationPage(new Views.DashBoard());
        }

        private async void Button_Clicked(object sender, EventArgs e)
        {
            using (UserDialogs.Instance.Loading("Connecting to Service, Please Wait...", null, null, true))
            {
                await Task.Delay(1500);

                string SearchStringFrom = Convert.ToString(startDatePicker.Date.ToString("MM/dd/yyyy"));
                string SearchStringTo = Convert.ToString(endDatePicker.Date.ToString("MM/dd/yyyy"));

                //call osoftpay for agent list
                string url = "https://rickbolwa.osoftpay.net/api/TaskPayers/gettransaction?Email=" + MainPage.ValidUserMail + "&SearchFrom=" + SearchStringFrom + "&SearchTo=" + SearchStringTo;
                try
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => Rickbolwa.Services.ApiClient.ValidateCertificate(message, cert, chain, errors),
                        SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls11
                    };

                    using (HttpClient client = new HttpClient(handler))
                    {
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        using (HttpResponseMessage response = client.GetAsync(url).Result)
                        {
                            using (HttpContent content = response.Content)
                            {
                                var json = content.ReadAsStringAsync().Result;
                                MemoryStream memStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                                // convert to string
                                StreamReader reader = new StreamReader(memStream);
                                string text = reader.ReadToEnd();
                                List<HistoryData> items = JsonConvert.DeserializeObject<List<HistoryData>>(text);

                                BindingContext = new HistoryDataHeaderFooter { HD = items };

                            }
                        }
                    }

                }
                catch (Exception exe)
                {

                    exe.ToString();
                }
            }

        }

    }
}