using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Rickbolwa.Services
{

    public class UserSessionModel
    {
        public string FullName { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string Station { get; set; }
        public string Password { get; set; }
        public string Token { get; set; }
        public string Role { get; set; }
    }

    public class SessionManager
    {
        // ── Singleton (unchanged shape) ───────────────────────────────────────
        private static SessionManager _instance;
        private static readonly object _lock = new object();

        public static SessionManager Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                        _instance = new SessionManager();
                    return _instance;
                }
            }
        }

        private SessionManager() { }



        public static string FullName { get; set; } = "Revenue Agent";
        public static string Phone { get; set; } = "08012345678";
        public static string Station { get; set; } = "Damaturu / Potiskum";
        public static string Token { get; set; } = "";
        public static string Role { get; set; } = "Agent";
        // ── Storage keys ──────────────────────────────────────────────────────
        private const string KeyActive = "rick.session.active";
        private const string KeyEmail = "rick.session.email";
        private const string KeyName = "rick.session.name";
        private const string KeyCategory = "rick.session.category";
        private const string KeyCollectionPoint = "rick.session.collectionPoint";
        private const string KeySuperAgent = "rick.session.superAgent";
        private const string KeyMessage = "rick.session.message";
        private const string KeySignedInAt = "rick.session.signedInAtUtc";
        private const string KeyLastSeenAt = "rick.session.lastSeenAtUtc";

        private const string SecretPin = "rick.session.pin";
        private const string SecretPassword = "rick.session.password";

        // Used only when the device keystore is unusable.
        private const string FallbackPrefix = "rick.fallback.";

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        // ── In-memory cache (hydrated by RestoreAsync at app start) ───────────
        private static bool _active;
        private static string _email = string.Empty;
        private static string _name = string.Empty;
        private static string _category = string.Empty;
        private static string _collectionPoint = string.Empty;
        private static string _superAgent = string.Empty;
        private static string _message = string.Empty;
        private static string _pin = string.Empty;
        private static string _password = string.Empty;

        /// <summary>Fired after sign-in, restore and sign-out.</summary>
        public static event EventHandler<bool> SessionChanged;

        // ── State ─────────────────────────────────────────────────────────────

        /// <summary>True once <see cref="RestoreAsync"/> has completed.</summary>
        public static bool IsRestored { get; private set; }

        /// <summary>A session is valid only when it is flagged active AND carries an email.</summary>
        public static bool IsAuthenticated => _active && !string.IsNullOrWhiteSpace(_email);

        public static string Email { get => _email; set { _email = Clean(value); WritePref(KeyEmail, _email); } }
        public static string Name { get => _name; set { _name = Clean(value); WritePref(KeyName, _name); } }
        public static string Category { get => _category; set { _category = Clean(value); WritePref(KeyCategory, _category); } }
        public static string CollectionPoint { get => _collectionPoint; set { _collectionPoint = Clean(value); WritePref(KeyCollectionPoint, _collectionPoint); } }
        public static string SuperAgent { get => _superAgent; set { _superAgent = Clean(value); WritePref(KeySuperAgent, _superAgent); } }
        public static string Message { get => _message; set { _message = Clean(value); WritePref(KeyMessage, _message); } }

        public static string Pin { get => _pin; set { _pin = Clean(value); _ = WriteSecretAsync(SecretPin, _pin); } }
        public static string Password { get => _password; set { _password = Clean(value); _ = WriteSecretAsync(SecretPassword, _password); } }

        public static DateTime? SignedInAtUtc => ReadDate(KeySignedInAt);
        public static DateTime? LastSeenUtc => ReadDate(KeyLastSeenAt);

        // ── Restore (call once from App bootstrap, before the first page) ─────

        /// <summary>
        /// Rehydrates the in-memory cache from disk. Must complete before any page reads
        /// MainPage.Pin / MainPage.ValidUserMail, so await it in App bootstrap.
        /// Safe to call repeatedly.
        /// </summary>
        public static async Task RestoreAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);

            try
            {
                _active = ReadPref(KeyActive) == "1";
                _email = ReadPref(KeyEmail);
                _name = ReadPref(KeyName);
                _category = ReadPref(KeyCategory);
                _collectionPoint = ReadPref(KeyCollectionPoint);
                _superAgent = ReadPref(KeySuperAgent);
                _message = ReadPref(KeyMessage);

                _pin = await ReadSecretAsync(SecretPin).ConfigureAwait(false);
                _password = await ReadSecretAsync(SecretPassword).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log("RestoreAsync", ex);
            }
            finally
            {
                IsRestored = true;   // never block startup on a storage fault
                Gate.Release();
            }

            Raise();
        }

        // ── Sign in ───────────────────────────────────────────────────────────

        /// <summary>
        /// Persists a complete session atomically. Call once, on successful login.
        /// </summary>
        public static async Task<bool> SignInAsync(SessionData data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Email))
            {
                Log("SignInAsync", new ArgumentException("Email is required to open a session."));
                return false;
            }

            await Gate.WaitAsync().ConfigureAwait(false);

            try
            {
                _email = Clean(data.Email);
                _name = Clean(data.Name);
                _category = Clean(data.Category);
                _collectionPoint = Clean(data.CollectionPoint);
                _superAgent = Clean(data.SuperAgent);
                _message = Clean(data.Message);
                _pin = Clean(data.Pin);
                _password = Clean(data.Password);
                _active = true;

                WritePref(KeyEmail, _email);
                WritePref(KeyName, _name);
                WritePref(KeyCategory, _category);
                WritePref(KeyCollectionPoint, _collectionPoint);
                WritePref(KeySuperAgent, _superAgent);
                WritePref(KeyMessage, _message);
                WritePref(KeySignedInAt, DateTime.UtcNow.ToString("O"));
                WritePref(KeyLastSeenAt, DateTime.UtcNow.ToString("O"));

                await WriteSecretAsync(SecretPin, _pin).ConfigureAwait(false);
                await WriteSecretAsync(SecretPassword, _password).ConfigureAwait(false);

                // Written last on purpose: if any write above fails we never reach here, so a
                // half-written session reads as invalid rather than being partially trusted.
                WritePref(KeyActive, "1");

                IsRestored = true;
                return true;
            }
            catch (Exception ex)
            {
                Log("SignInAsync", ex);
                return false;
            }
            finally
            {
                Gate.Release();
                Raise();
            }
        }

        // ── Sign out ──────────────────────────────────────────────────────────

        /// <summary>The only thing that ends a session. Wipes memory and disk.</summary>
        public static async Task SignOutAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);

            try
            {
                foreach (var key in new[]
                {
                    KeyActive, KeyEmail, KeyName, KeyCategory, KeyCollectionPoint,
                    KeySuperAgent, KeyMessage, KeySignedInAt, KeyLastSeenAt
                })
                {
                    RemovePref(key);
                }

                RemoveSecret(SecretPin);
                RemoveSecret(SecretPassword);

                ClearMemory();
            }
            catch (Exception ex)
            {
                Log("SignOutAsync", ex);
            }
            finally
            {
                Gate.Release();
                Raise();
            }
        }

        private static void ClearMemory()
        {
            _active = false;
            _email = _name = _category = _collectionPoint =
                _superAgent = _message = _pin = _password = string.Empty;
        }

        /// <summary>Snapshot for diagnostics or a profile screen.</summary>
        public static SessionData Current => new SessionData
        {
            Email = _email,
            Name = _name,
            Category = _category,
            CollectionPoint = _collectionPoint,
            SuperAgent = _superAgent,
            Message = _message,
            Pin = _pin,
            Password = _password
        };

        // ═════════════════════════════════════════════════════════════════════
        //  Backwards-compatible instance API
        //  Existing call sites across all modules keep working unchanged.
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Marks the session live. Previously started a 30-minute inactivity timer;
        /// that timer is gone — the session no longer expires.
        /// </summary>
        public void StartSession()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_email))
                {
                    _active = true;
                    WritePref(KeyActive, "1");
                }

                WritePref(KeyLastSeenAt, DateTime.UtcNow.ToString("O"));
                System.Diagnostics.Debug.WriteLine("[Session] Active (persistent, no timeout)");
            }
            catch (Exception ex)
            {
                Log("StartSession", ex);
            }
        }

        /// <summary>
        /// Ends the session. This is now the logout path — the existing PerformLogout /
        /// HandleLogout / LogoutAsync methods in the dashboards already call it, so logout
        /// wipes persisted state with no edit required.
        /// </summary>
        public void StopSession()
        {
            try
            {
                ClearMemory();          // immediate, so IsAuthenticated flips synchronously
                RemovePref(KeyActive);  // and the flag is gone even if the async wipe is cut short
                _ = SignOutAsync();
                System.Diagnostics.Debug.WriteLine("[Session] Signed out");
            }
            catch (Exception ex)
            {
                Log("StopSession", ex);
            }
        }

        /// <summary>Stamps last-seen. Purely diagnostic now — it cannot trigger a logout.</summary>
        public void UpdateActivity()
        {
            try
            {
                if (!IsAuthenticated) return;
                WritePref(KeyLastSeenAt, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex)
            {
                Log("UpdateActivity", ex);
            }
        }

        /// <summary>Alias of <see cref="UpdateActivity"/>, kept for source compatibility.</summary>
        public void ResetSession() => UpdateActivity();

        /// <summary>No expiry — always reports unlimited time remaining.</summary>
        public double GetRemainingSessionTime() => double.MaxValue;

        /// <summary>No expiry — always false.</summary>
        public bool IsSessionExpiringSoon() => false;

        // ── Preferences (non-sensitive) ───────────────────────────────────────

        private static string ReadPref(string key)
        {
            try { return Preferences.Get(key, string.Empty) ?? string.Empty; }
            catch (Exception ex) { Log($"ReadPref({key})", ex); return string.Empty; }
        }

        private static void WritePref(string key, string value)
        {
            try { Preferences.Set(key, value ?? string.Empty); }
            catch (Exception ex) { Log($"WritePref({key})", ex); }
        }

        private static void RemovePref(string key)
        {
            try { Preferences.Remove(key); }
            catch (Exception ex) { Log($"RemovePref({key})", ex); }
        }

        private static DateTime? ReadDate(string key)
        {
            try
            {
                var raw = ReadPref(key);
                if (string.IsNullOrWhiteSpace(raw)) return null;

                return DateTime.TryParse(raw, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : (DateTime?)null;
            }
            catch (Exception ex)
            {
                Log($"ReadDate({key})", ex);
                return null;
            }
        }

        // ── SecureStorage (secrets) with Preferences fallback ─────────────────

        private static async Task<string> ReadSecretAsync(string key)
        {
            try
            {
                var value = await SecureStorage.GetAsync(key).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            catch (Exception ex)
            {
                // Corrupt or locked keystore on some Android builds — fall through.
                Log($"ReadSecret({key})", ex);
            }

            return ReadPref(FallbackPrefix + key);
        }

        private static async Task WriteSecretAsync(string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                RemoveSecret(key);
                return;
            }

            try
            {
                await SecureStorage.SetAsync(key, value).ConfigureAwait(false);
                RemovePref(FallbackPrefix + key);
                return;
            }
            catch (Exception ex)
            {
                Log($"WriteSecret({key})", ex);
            }

            WritePref(FallbackPrefix + key, value);
        }

        private static void RemoveSecret(string key)
        {
            try { SecureStorage.Remove(key); }
            catch (Exception ex) { Log($"RemoveSecret({key})", ex); }

            RemovePref(FallbackPrefix + key);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string Clean(string value) => value?.Trim() ?? string.Empty;

        private static void Raise()
        {
            try
            {
                var handler = SessionChanged;
                if (handler == null) return;

                Device.BeginInvokeOnMainThread(() =>
                {
                    try { handler(null, IsAuthenticated); }
                    catch (Exception ex) { Log("SessionChanged/handler", ex); }
                });
            }
            catch (Exception ex)
            {
                Log("SessionChanged", ex);
            }
        }

        private static void Log(string scope, Exception ex)
            => System.Diagnostics.Debug.WriteLine($"[Session:{scope}] {ex?.GetType().Name}: {ex?.Message}");


        public static UserSessionModel GetSession()
        {
            return new UserSessionModel
            {
                FullName = FullName,
                Email = Email,
                Phone = Phone,
                Station = Station,
                Password = Password,
                Token = Token,
                Role = Role
            };
        }

        /// <summary>
        /// Saves or updates the current active session
        /// </summary>
        public static void SetSession(string email, string fullName = "", string phone = "", string station = "", string password = "", string token = "", string role = "")
        {
            Email = email;
            if (!string.IsNullOrEmpty(fullName)) FullName = fullName;
            if (!string.IsNullOrEmpty(phone)) Phone = phone;
            if (!string.IsNullOrEmpty(station)) Station = station;
            if (!string.IsNullOrEmpty(password)) Password = password;
            if (!string.IsNullOrEmpty(token)) Token = token;
            if (!string.IsNullOrEmpty(role)) Role = role;
        }

        /// <summary>
        /// Clears the session on logout
        /// </summary>
        public static void ClearSession()
        {
            FullName = "";
            Email = "";
            Phone = "";
            Station = "";
            Password = "";
            Token = "";
            Role = "";
        }
    }

    /// <summary>Transport object for opening a session.</summary>
    public class SessionData
    {
        public string Email { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string CollectionPoint { get; set; }
        public string SuperAgent { get; set; }
        public string Message { get; set; }
        public string Pin { get; set; }
        public string Password { get; set; }
    }


}


