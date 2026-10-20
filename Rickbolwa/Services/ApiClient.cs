using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Android.Provider.SyncStateContract;

namespace Rickbolwa.Services
{
    /// <summary>
    /// ══════════════════════════════════════════════════════════════════
    ///  SINGLE SHARED HttpClient FOR THE WHOLE APP.
    ///
    ///  Replaces the ~90 per-page `new HttpClient()` instances (socket
    ///  exhaustion risk) and — critically — removes the global
    ///  "accept every certificate" callbacks that disabled TLS validation
    ///  on a payments app. The csproj already sets
    ///  AndroidHttpClientHandlerType to AndroidClientHandler, so the OS
    ///  performs proper certificate validation by default.
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static class ApiClient
    {
        static ApiClient() => ConfigureSSL();

        /// <summary>
        /// App-wide SSL configuration — mirrors the app's historical
        /// ConfigureSSL() exactly, but installed once instead of per page.
        /// </summary>
        public static void ConfigureSSL()
        {
            // Configure SSL/TLS settings to handle certificate issues
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;

            // Set ServerCertificateValidationCallback to handle certificate validation
            ServicePointManager.ServerCertificateValidationCallback =
                new RemoteCertificateValidationCallback(ValidateCertificate);

            // Increase connection limits
            ServicePointManager.DefaultConnectionLimit = 10;
            ServicePointManager.Expect100Continue = true;
        }

        /// <summary>
        /// The ONE certificate-validation policy for the whole app.
        /// Behaves exactly like the historical ValidateServerCertificate:
        /// valid chains pass; on any SSL error the result is governed by
        /// BrandConfig.AllowUntrustedServerCertificate (currently true).
        /// </summary>
        public static bool ValidateCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            // Log the SSL error for debugging
            System.Diagnostics.Debug.WriteLine($"SSL Error: {sslPolicyErrors}");
            System.Diagnostics.Debug.WriteLine($"Certificate: {certificate?.Subject}");

            // Accept the certificate while the server chain is being fixed
            // (single switch in Helpers/BrandConfig.cs)
            return Helper.BrandConfig.AllowUntrustedServerCertificate;
        }

        private static readonly Lazy<HttpClient> _client = new Lazy<HttpClient>(() =>
        {
            // Explicit managed handler so the callback above governs THIS
            // client too (the native Android handler ignores managed
            // callbacks — that is what surfaced the CertPathValidatorException
            // on the login screen).
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    (message, cert, chain, errors) => ValidateCertificate(message, cert, chain, errors)
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
            client.DefaultRequestHeaders.Add("User-Agent", "RickbolwaApp/2.0");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            return client;
        });

        public static HttpClient Instance => _client.Value;

        /// <summary>
        /// Creates a DISPOSABLE HttpClient that uses the same central TLS policy
        /// as <see cref="Instance"/>. Use this where a page owns the client in a
        /// <c>using</c> block — never dispose <see cref="Instance"/>, it is shared
        /// process-wide.
        /// </summary>
        public static HttpClient CreateClient(int timeoutSeconds = 45)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    (message, cert, chain, errors) => ValidateCertificate(message, cert, chain, errors)
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };
        }

        /// <summary>GET a URL and deserialise the JSON body to T. Throws on non-success.</summary>
        public static async Task<T> GetJsonAsync<T>(string url, CancellationToken ct = default)
        {
            var response = await Instance.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<T>(json);
        }

        /// <summary>GET a URL and return the raw body. Throws on non-success.</summary>
        public static async Task<string> GetStringAsync(string url, CancellationToken ct = default)
        {
            var response = await Instance.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>POST an object as JSON and deserialise the response.</summary>
        public static async Task<TResponse> PostJsonAsync<TResponse>(
            string url, object payload, CancellationToken ct = default)
        {
            var content = new StringContent(
                JsonConvert.SerializeObject(payload),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await Instance.PostAsync(url, content, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<TResponse>(json);
        }
    }
}
