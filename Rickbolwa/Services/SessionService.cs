using Newtonsoft.Json;
using Rickbolwa.Helper;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Xamarin.Essentials;

namespace Rickbolwa.Services
{
    /// <summary>
    /// The agent profile persisted for a logged-in session.
    /// Sensitive fields (PIN) live only in SecureStorage.
    /// </summary>
    public sealed class AgentSession
    {
        public string Email { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string CollectionPoint { get; set; }
        public string SuperAgent { get; set; }
        public string Message { get; set; }
        public DateTime LoggedInAtUtc { get; set; }
    }

    /// <summary>
    /// ══════════════════════════════════════════════════════════════════
    ///  CENTRAL SESSION MANAGER — replaces the scattered
    ///  `public static string` credential properties on MainPage.
    ///
    ///  • Login survives process death (SecureStorage / Preferences).
    ///  • Inactivity timeout (BrandConfig.SessionInactivityTimeoutMinutes):
    ///    every page interaction should call <see cref="Touch"/>; the App
    ///    checks <see cref="IsExpired"/> in OnResume and on startup.
    ///  • Absolute expiry (BrandConfig.SessionAbsoluteExpiryDays) forces a
    ///    fresh login daily regardless of activity.
    ///  • <see cref="LogoutAsync"/> wipes everything and returns to login.
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static class SessionService
    {
        private const string ProfileKey = "session.profile";       // Preferences (non-sensitive)
        private const string PinKey = "session.pin";           // SecureStorage
        private const string LastActivityKey = "session.lastActivity";  // Preferences (UTC ticks)

        /// <summary>The currently restored session, or null when logged out.</summary>
        public static AgentSession Current { get; private set; }

        public static bool IsLoggedIn => Current != null;

        // ── Save / restore ───────────────────────────────────────

        public static async Task SaveAsync(AgentSession session, string pin)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.LoggedInAtUtc = DateTime.UtcNow;

            Preferences.Set(ProfileKey, JsonConvert.SerializeObject(session));
            Touch();

            try { await SecureStorage.SetAsync(PinKey, pin ?? string.Empty); }
            catch
            {
                // SecureStorage can fail on devices without a lock screen /
                // keystore. Fall back to memory-only PIN: the agent will be
                // asked to log in again next launch, which is the safe option.
            }

            Current = session;
            _memoryPin = pin;
        }

        /// <summary>
        /// Attempts to restore a persisted session. Returns null when there is
        /// none, or when it has expired (in which case storage is cleared).
        /// </summary>
        public static async Task<AgentSession> TryRestoreAsync()
        {
            var json = Preferences.Get(ProfileKey, null);
            if (string.IsNullOrEmpty(json)) return null;

            AgentSession session;
            try { session = JsonConvert.DeserializeObject<AgentSession>(json); }
            catch { await LogoutAsync(); return null; }

            if (session == null) { await LogoutAsync(); return null; }

            // Absolute expiry
            if (DateTime.UtcNow - session.LoggedInAtUtc >
                TimeSpan.FromDays(BrandConfig.SessionAbsoluteExpiryDays))
            {
                await LogoutAsync();
                return null;
            }

            // Inactivity expiry
            if (IsExpired)
            {
                await LogoutAsync();
                return null;
            }

            try { _memoryPin = await SecureStorage.GetAsync(PinKey); }
            catch { _memoryPin = null; }

            Current = session;
            Touch();
            return session;
        }

        // ── Inactivity tracking ──────────────────────────────────

        /// <summary>Record user activity. Call from page OnAppearing / key actions.</summary>
        public static void Touch()
            => Preferences.Set(LastActivityKey, DateTime.UtcNow.Ticks);

        public static bool IsExpired
        {
            get
            {
                var ticks = Preferences.Get(LastActivityKey, 0L);
                if (ticks == 0L) return false;
                var idle = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
                return idle > TimeSpan.FromMinutes(BrandConfig.SessionInactivityTimeoutMinutes);
            }
        }

        // ── PIN access ───────────────────────────────────────────

        private static string _memoryPin;

        /// <summary>Transfer PIN for confirming transactions. Never log this.</summary>
        public static string Pin => _memoryPin ?? string.Empty;

        // ── Logout ───────────────────────────────────────────────

        public static Task LogoutAsync()
        {
            Current = null;
            _memoryPin = null;
            Preferences.Remove(ProfileKey);
            Preferences.Remove(LastActivityKey);
            SecureStorage.Remove(PinKey);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Ends the session and swaps the root page back to the login screen.
        /// Safe to call from any page:  await SessionService.LogoutToLoginAsync();
        /// </summary>
        public static async Task LogoutToLoginAsync()
        {
            await LogoutAsync();
            Xamarin.Forms.Device.BeginInvokeOnMainThread(() =>
            {
                Xamarin.Forms.Application.Current.MainPage = new MainPage();
            });
        }
    }
}
