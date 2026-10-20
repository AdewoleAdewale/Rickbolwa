using Acr.UserDialogs;
using DIPS.Xamarin.UI.Controls.Sheet;
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
using Xamarin.Forms.PlatformConfiguration.iOSSpecific;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class Registeration : ContentPage
    {
        List<VehicleCatData> enums;
        List<LGACatData> enums2;
        public Registeration()
        {
            InitializeComponent();
            sheetBehavior.IsOpen = false;
            failedenumeration.IsOpen = false;
            LoadVehicleCategory();
            LoadLGACategory();

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

        private void TapGestureRecognizer_Tapped_1(object sender, EventArgs e)
        {

            Xamarin.Forms.Application.Current.MainPage = new Xamarin.Forms.NavigationPage(new Views.DashBoard());


        }

        private async void TapGestureRecognizer_Tapped(object sender, EventArgs e)
        {
            using (UserDialogs.Instance.Loading("Connecting to Service, Please Wait...", null, null, true))
            {
                await Task.Delay(2500);

                int indexSelected = picker.SelectedIndex;
                indexSelected = indexSelected + 1;
                int count = 0;
                string CatNameSelected = "";
                foreach (var item in enums.ToList())
                {
                    count = count + 1;
                    if (count == indexSelected)
                    {
                        CatNameSelected = item.vehicleType;
                    }
                }

                int indexSelected2 = picker2.SelectedIndex;
                indexSelected2 = indexSelected2 + 1;
                int count2 = 0;
                string CatNameSelected2 = "";
                foreach (var item in enums2.ToList())
                {
                    count2 = count2 + 1;
                    if (count2 == indexSelected2)
                    {
                        CatNameSelected2 = item.lgaName;
                    }
                }


                if (drivername.Text == null || Platenumber.Text == null || Ownernumber.Text == null || CatNameSelected2 == null || CatNameSelected == null)
                {

                    await DisplayAlert("NOTIFICATION", "Kindly fill in all details before you proceed", "TRYAGAIN");
                    await Navigation.PushAsync(new Views.Registeration());

                }



                else if (drivername.Text != null && Platenumber.Text != null && Ownernumber.Text != null && CatNameSelected2 != null && CatNameSelected != null)
                {
                    string url = "https://rickbolwa.osoftpay.net/api/HulageVehicles/VehicleReg";

                    VechicleRegistrationObject vechicleRegistrationObject = new VechicleRegistrationObject()
                    {
                        NameofDriver = drivername.Text,
                        PlateNumber = Platenumber.Text,
                        OwnerPhone = Ownernumber.Text,
                        LGA = CatNameSelected2,
                        VehicleType = CatNameSelected,
                        RecordedBy = MainPage.ValidUserMail,

                    };
                    var httpClientHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => Rickbolwa.Services.ApiClient.ValidateCertificate(message, cert, chain, errors),
                        SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls11
                    };
                    var nvc = new List<KeyValuePair<string, string>>();
                    nvc.Add(new KeyValuePair<string, string>("NameofDriver", vechicleRegistrationObject.NameofDriver));
                    nvc.Add(new KeyValuePair<string, string>("PlateNumber", vechicleRegistrationObject.PlateNumber));
                    nvc.Add(new KeyValuePair<string, string>("OwnerPhone", vechicleRegistrationObject.OwnerPhone));
                    nvc.Add(new KeyValuePair<string, string>("LGA", vechicleRegistrationObject.LGA));
                    nvc.Add(new KeyValuePair<string, string>("VehicleType", vechicleRegistrationObject.VehicleType));
                    nvc.Add(new KeyValuePair<string, string>("RecordedBy", vechicleRegistrationObject.RecordedBy));

                    var client = new HttpClient(httpClientHandler);
                    var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(nvc) };
                    var res = await client.SendAsync(req);

                    var resultString = await res.Content.ReadAsStringAsync();
                    var VechicleRegisterationResponse = JsonConvert.DeserializeObject<VechicleRegisterationResponse>(resultString);
                    if (VechicleRegisterationResponse.statusCode == "oo")
                    {
                        sheetBehavior.IsOpen = true;
                        failedenumeration.IsOpen = false;


                    }
                    else
                    {
                        sheetBehavior.IsOpen = false;
                        failedenumeration.IsOpen = true;


                    }

                }
            }
        }

        protected override bool OnBackButtonPressed()
        {
            Device.BeginInvokeOnMainThread(async () =>
            {

                using (UserDialogs.Instance.Loading("Connecting to Service, Please Wait...", null, null, true))
                {
                    await Task.Delay(10);

                    Application.Current.MainPage = new NavigationPage(new Views.DashBoard());

                }

            });
            return true;
        }

        private void LoadVehicleCategory()
        {
            try
            {
                string url = "https://borno.osoftpay.net/api/HulageVehicles/VehicleType";

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
                            List<VehicleCatData> items = JsonConvert.DeserializeObject<List<VehicleCatData>>(text);

                            picker.ItemDisplayBinding = new Binding("vehicleType");
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

        private void LoadLGACategory()
        {
            try
            {
                string url = "https://borno.osoftpay.net/api/HulageVehicles/getlga";

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
                            List<LGACatData> items = JsonConvert.DeserializeObject<List<LGACatData>>(text);

                            picker2.ItemDisplayBinding = new Binding("lgaName");
                            picker2.ItemsSource = items.ToList();
                            enums2 = items.ToList();

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

        ////[Obsolete]

        private void failedenumeration_ActionClicked(object sender, EventArgs e)
        {
            failedenumeration.Opened += failedenumeration_ActionClicked;
        }

        private async void tryagain_Clicked(object sender, EventArgs e)
        {
            await Navigation.PushAsync(new Views.Registeration());
        }

    }

    internal class VehicleCatData
    {
        public string vehicleType { get; set; }
    }

    internal class LGACatData
    {
        public string lgaName { get; set; }
    }



    internal class VechicleRegisterationResponse
    {
        public string nameofDriver { get; set; }
        public string plateNumber { get; set; }
        public string ownerPhone { get; set; }
        public string state { get; set; }
        public string totalAmount { get; set; }
        public string lga { get; set; }
        public string vehicleType { get; set; }
        public string message { get; set; }
        public string statusCode { get; set; }
        public string amount { get; set; }
        public string recordedBy { get; set; }
        public string dateRecorded { get; set; }


    }
    internal class VechicleRegistrationObject
    {
        public string NameofDriver { get; set; }
        public string PlateNumber { get; set; }
        public string OwnerPhone { get; set; }
        public string LGA { get; set; }
        public string VehicleType { get; set; }
        public string RecordedBy { get; set; }
    }

}