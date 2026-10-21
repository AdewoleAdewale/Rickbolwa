using Acr.UserDialogs;
using Plugin.Toasts;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Rickbolwa.Helper
{
    public class BaseViewModel : INotifyPropertyChanged
    {
        const int RefreshDuration = 2;
        int itemNumber = 1;
        readonly Random random;
        bool isRefreshing;

        public bool IsRefreshing
        {
            get { return isRefreshing; }
            set
            {
                isRefreshing = value;
                OnPropertyChanged();
            }
        }
        public bool IsNotConnected { get; set; }
        public bool IsConnected { get; set; }
        public BaseViewModel()
        {
            Connectivity.ConnectivityChanged += Connectivity_ConnectivityChanged;
            IsNotConnected = Connectivity.NetworkAccess != NetworkAccess.Internet;
            IsConnected = Connectivity.NetworkAccess == NetworkAccess.Internet;
        }


        public ICommand RefreshCommand => new Command(async () => await RefreshItemsAsync());

        private async Task RefreshItemsAsync()
        {
            IsRefreshing = true;
            await Task.Delay(TimeSpan.FromSeconds(RefreshDuration));

            IsRefreshing = false;
        }

        public ICommand Expand1OpenedCommand { get; set; }
        public ICommand Expand2OpenedCommand { get; set; }

        ~BaseViewModel()
        {
            Connectivity.ConnectivityChanged -= Connectivity_ConnectivityChanged;
        }
        public event PropertyChangedEventHandler PropertyChanged;
        void Connectivity_ConnectivityChanged(object sender, ConnectivityChangedEventArgs e)
        {




            if (e.NetworkAccess != NetworkAccess.Internet)
            {
                IsNotConnected = Connectivity.NetworkAccess != NetworkAccess.Internet;
                DependencyService.Get<IToastNotificator>().Notify(new NotificationOptions()
                {
                    Description = "Oops, You dont have internet Kindly switchon Your Mobile Data",
                    Title = "Connection lost"
                });
                UserDialogs.Instance.Toast("Notification, You don't have internet connection");
            }

            else if (e.NetworkAccess == NetworkAccess.Internet)
            {
                IsConnected = Connectivity.NetworkAccess == NetworkAccess.Internet;
                DependencyService.Get<IToastNotificator>().Notify(new NotificationOptions()
                {
                    Description = "Oops,looks like you internet connection is back,Now Go Back to the App",
                    Title = "Internet Connection "
                });
                UserDialogs.Instance.Toast("Notification, Your internet connection is back");
            }


        }


        void OnPropertyChanged()
        {

        }


    }
}
