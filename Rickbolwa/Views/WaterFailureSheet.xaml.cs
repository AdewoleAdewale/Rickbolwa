using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Rickbolwa.Views
{
    [XamlCompilation(XamlCompilationOptions.Compile)]
    public partial class WaterFailureSheet : ContentPage
    {
        public WaterFailureSheet(string title, string reason)
        {
            InitializeComponent();
            TitleLabel.Text = title;
            ReasonLabel.Text = reason;
        }

        private async void OnDismissClicked(object sender, EventArgs e)
        {
            await Navigation.PopModalAsync();
        }
    }
}