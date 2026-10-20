using Acr.UserDialogs;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Forms;

namespace Rickbolwa.Services
{
    /// <summary>
    /// Maps an agent category to its module dashboard. Used by MainPage after
    /// a manual login and by App on startup when a persisted session is
    /// restored (auto-login).
    /// </summary>
    public static class NavigationRouter
    {
        public static async Task GoToCategoryDashboardAsync(string agentCategory, string agentName, bool showWelcome = true)
        {
            Page destination;
            string displayName;
            string icon;

            switch ((agentCategory ?? string.Empty).Trim().ToLower())
            {
              
                case "hulage":
                case "haulage":
                    displayName = "Haulage"; icon = "🚛";
                    destination = new Views.DashBoard(); break;

                default:
                    displayName = "Agent"; icon = "👤";
                    destination = new Views.DashBoard(); break;
            }

            if (showWelcome && !string.IsNullOrEmpty(agentName))
            {
                UserDialogs.Instance.Toast(
                    string.Format("{0} Welcome {1}!\n{2} Portal", icon, agentName, displayName),
                    TimeSpan.FromSeconds(3));
            }

            Device.BeginInvokeOnMainThread(() =>
            {
                Application.Current.MainPage = new NavigationPage(destination)
                {
                    BarBackgroundColor = (Color)Application.Current.Resources["PrimaryDarkColor"],
                    BarTextColor = Color.White
                };
            });

            await Task.CompletedTask;
        }
    }
}
