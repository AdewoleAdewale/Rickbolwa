using Acr.UserDialogs;
using Android.Accounts;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class Verify : ContentPage
    {
  
            public static string nameofDriverss { get; set; }
            public static string plateNumberss { get; set; }
            public static string ownerPhoness { get; set; }
            public static string statess { get; set; }
            public static string lgass { get; set; }
            public static string vehicleTypess { get; set; }
            public static string messagess { get; set; }
            public static string statusCodess { get; set; }
            public static string amountss { get; set; }
            public static string recordedByss { get; set; }
            public static string dateRecordedss { get; set; }

            public Verify()
            {
                InitializeComponent();

                nameofDrivers.Text = Verify.nameofDriverss;
                plateNumbers.Text = Verify.plateNumberss;
                lga.Text = Verify.lgass;
                Amount.Text = Verify.amountss;
                vehicleTypes.Text = Verify.vehicleTypess;
                messages.Text = Verify.messagess;
                DateRecorded.Text = Verify.dateRecordedss;
                ownerPhones.Text = Verify.ownerPhoness;

                if (Verify.plateNumberss != null)
                {
                    make.IsVisible = true;
                }
                else
                {
                    make.IsVisible = false;
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




            protected override bool OnBackButtonPressed()
            {
                Device.BeginInvokeOnMainThread(async () =>
                {

                    using (UserDialogs.Instance.Loading("Connecting to Service, Please Wait...", null, null, true))
                    {
                        await Task.Delay(10);

                        await Navigation.PushModalAsync(new Views.DashBoard());
                    }

                });
                return true;
            }
            private async void TapGestureRecognizer_Tapped_1(object sender, EventArgs e)
            {
                using (UserDialogs.Instance.Loading("Connecting to Service, Please Wait...", null, null, true))
                {
                    await Task.Delay(1000);

                    string url = "https://rickbolwa.osoftpay.net/api/HulageVehicles/verify?PlateNumber=" + Search.Text.Trim();
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
                            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                            using (HttpResponseMessage response = client.GetAsync(url).Result)
                            {
                                using (HttpContent content = response.Content)
                                {
                                    var json = content.ReadAsStringAsync().Result;
                                    HaulageVerifyResponse result = JsonConvert.DeserializeObject<HaulageVerifyResponse>
                                        (json);
                                    if (result != null)
                                    {
                                        nameofDriverss = result.nameofDriver;
                                        plateNumberss = result.plateNumber;
                                        lgass = result.lga;
                                        amountss = result.amount;
                                        vehicleTypess = result.vehicleType;
                                        ownerPhoness = result.ownerPhone;
                                        messagess = result.message;
                                        statusCodess = result.statusCode;

                                        recordedByss = result.recordedBy;
                                        dateRecordedss = result.dateRecorded;

                                        await Application.Current.SavePropertiesAsync();
                                        await Navigation.PushAsync(new Verify());



                                    }
                                    else
                                    {
                                        await DisplayAlert("NOTIFICATION", "Can't Verify VECHICLE Plate Number", "TRY AGAIN");

                                        await Navigation.PushAsync(new Verify());

                                    }
                                }

                            }
                        }

                    }
                    catch (Exception exe)
                    {
                        UserDialogs.Instance.Toast("Network Error, Cant Verify Now Try Again", TimeSpan.FromSeconds(10));

                        exe.ToString();
                    }
                }
            }

            private async void make_Clicked(object sender, EventArgs e)
            {
                await Navigation.PushAsync(new  Payment());
            }

            internal class HaulageVerifyResponse
            {
                public string nameofDriver { get; set; }
                public string plateNumber { get; set; }
                public string ownerPhone { get; set; }
                public string state { get; set; }
                public string lga { get; set; }
                public string vehicleType { get; set; }
                public string message { get; set; }
                public string statusCode { get; set; }
                public string amount { get; set; }
                public string recordedBy { get; set; }
                public string dateRecorded { get; set; }


            }





        }
    }
