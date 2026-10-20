using System;
using System.Collections.Generic;
using System.Text;

namespace Rickbolwa.Helper
{
    public static class BrandConfig
    {
        // ═══════════════════════════════════════════════════════════════
        //  TLS POLICY SWITCH
        //  true  = accept the API server's certificate even when the device
        //          cannot validate its chain (matches the app's historical
        //          behaviour; required while borno.osoftpay.net serves an
        //          incomplete/untrusted certificate chain).
        //  false = strict validation (flip this once the server chain or a
        //          pinned certificate is in place — no other code changes).
        // ═══════════════════════════════════════════════════════════════
        public static bool AllowUntrustedServerCertificate = true;

        // ── Identity ─────────────────────────────────────────────
        public const string AppDisplayName = "Rickbolwa Limited";
        public const string OrganisationName = "RICKBOLWA LIMITED";
        public const string OrganisationAbbr = "RICKBOLWA";
        public const string OrganisationTagline = "Secure Revenue Collection";

        // ── Support / contact ────────────────────────────────────
        public const string SupportPhone1 = "08027229331";
        public const string SupportPhone2 = "08165932680";
        public static string SupportLine => $"📞 {SupportPhone1} | 📞 {SupportPhone2}";

        // ── Receipt branding ─────────────────────────────────────
        public const string ReceiptStoreName = OrganisationName;
        public const string ReceiptAddress = "Rickbolwa Limited";
        public static string ReceiptPhone => $"Contact us: {SupportPhone1}, {SupportPhone2}";
        public const string ReceiptFooterLine1 = "Thank You!";
        public const string ReceiptFooterLine2 = "POWERED BY OSOFTPAY";
        public const string ReceiptWatermark = OrganisationAbbr;
        public const string ReceiptLogoAsset = "Logo.png";

        // ── API endpoints ────────────────────────────────────────
        public const string ApiBaseUrl = "https://rickbolwa.osoftpay.net";
        public static string LoginUrl => ApiBaseUrl + "/api/taskpayers/v1/AgentLogin";
        public static string CentralCollectUrl => ApiBaseUrl + "/api/SingleCollections/PostCollect/NewCollect";
        public static string VerifyReceiptUrl => ApiBaseUrl + "/singlecollections/verify?TransactId=";

        // ── Session policy ───────────────────────────────────────
        /// <summary>Minutes of inactivity before the agent must log in again.</summary>
        public const int SessionInactivityTimeoutMinutes = 10;

        /// <summary>Days after which a persisted login is discarded outright.</summary>
        public const int SessionAbsoluteExpiryDays = 1;
    }
}
