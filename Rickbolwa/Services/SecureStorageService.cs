using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Essentials;

namespace Rickbolwa.Services
{
    /// <summary>
    /// Service for securely storing and retrieving user credentials
    /// </summary>
    public class SecureStorageService
    {
        private const string KEY_USER_CREDENTIALS = "user_credentials";
        private const string KEY_REMEMBER_ME = "remember_me";
        private const string KEY_LAST_ACTIVITY = "last_activity";

        /// <summary>
        /// Save user credentials securely
        /// </summary>
        public static async Task SaveCredentialsAsync(string email, string password, bool rememberMe)
        {
            try
            {
                if (rememberMe)
                {
                    var credentials = new UserCredentials
                    {
                        Email = email,
                        Password = password,
                        SavedAt = DateTime.UtcNow
                    };

                    string json = JsonConvert.SerializeObject(credentials);
                    await SecureStorage.SetAsync(KEY_USER_CREDENTIALS, json);
                    await SecureStorage.SetAsync(KEY_REMEMBER_ME, "true");
                }
                else
                {
                    await ClearCredentialsAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving credentials: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieve saved credentials
        /// </summary>
        public static async Task<UserCredentials> GetCredentialsAsync()
        {
            try
            {
                string rememberMe = await SecureStorage.GetAsync(KEY_REMEMBER_ME);
                if (rememberMe != "true")
                    return null;

                string json = await SecureStorage.GetAsync(KEY_USER_CREDENTIALS);
                if (string.IsNullOrEmpty(json))
                    return null;

                var credentials = JsonConvert.DeserializeObject<UserCredentials>(json);

                // Check if credentials are older than 30 days
                if (credentials != null && (DateTime.UtcNow - credentials.SavedAt).TotalDays > 30)
                {
                    await ClearCredentialsAsync();
                    return null;
                }

                return credentials;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error retrieving credentials: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Check if user has saved credentials
        /// </summary>
        public static async Task<bool> HasSavedCredentialsAsync()
        {
            try
            {
                string rememberMe = await SecureStorage.GetAsync(KEY_REMEMBER_ME);
                return rememberMe == "true";
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Clear saved credentials
        /// </summary>
        public static async Task ClearCredentialsAsync()
        {
            try
            {
                SecureStorage.Remove(KEY_USER_CREDENTIALS);
                SecureStorage.Remove(KEY_REMEMBER_ME);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error clearing credentials: {ex.Message}");
            }
        }

        /// <summary>
        /// Update last activity time
        /// </summary>
        public static async Task UpdateLastActivityAsync()
        {
            try
            {
                await SecureStorage.SetAsync(KEY_LAST_ACTIVITY, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating last activity: {ex.Message}");
            }
        }

        /// <summary>
        /// Get last activity time
        /// </summary>
        public static async Task<DateTime?> GetLastActivityAsync()
        {
            try
            {
                string lastActivity = await SecureStorage.GetAsync(KEY_LAST_ACTIVITY);
                if (string.IsNullOrEmpty(lastActivity))
                    return null;

                return DateTime.Parse(lastActivity);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// User credentials model
    /// </summary>
    public class UserCredentials
    {
        public string Email { get; set; }
        public string Password { get; set; }
        public DateTime SavedAt { get; set; }
    }
}