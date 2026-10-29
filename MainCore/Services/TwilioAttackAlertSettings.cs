using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MainCore.Services
{
    public sealed class TwilioAttackAlertSettings
    {
        public bool Enabled { get; init; }
        public string ToNumber { get; init; } = "";
        public string AccountSid { get; init; } = "";
        public string AuthToken { get; init; } = "";
        public string FromNumber { get; init; } = "";

        public static TwilioAttackAlertSettings Load(string? directory = null, bool useEnvironment = true)
        {
            directory ??= AppContext.BaseDirectory;
            var path = Path.Combine(directory, "twilio-alerts.local.json");
            if (!File.Exists(path)) return new();
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                return new()
                {
                    Enabled = json.RootElement.GetProperty("Enabled").GetBoolean(),
                    ToNumber = json.RootElement.GetProperty("ToNumber").GetString() ?? "",
                    AccountSid = (useEnvironment ? Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID") : null)
                        ?? (json.RootElement.TryGetProperty("AccountSid", out var sid) ? sid.GetString() : "") ?? "",
                    FromNumber = (useEnvironment ? Environment.GetEnvironmentVariable("TWILIO_FROM_NUMBER") : null)
                        ?? (json.RootElement.TryGetProperty("FromNumber", out var from) ? from.GetString() : "") ?? "",
                    AuthToken = ReadToken(directory, useEnvironment),
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException)
            {
                Serilog.Log.Warning("Twilio alerts disabled: local settings or encrypted token could not be read ({ErrorType}).", exception.GetType().Name);
                return new();
            }
        }

        private static string ReadToken(string directory, bool useEnvironment)
        {
            var token = useEnvironment ? Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") : null;
            if (!string.IsNullOrWhiteSpace(token)) return token;
            var path = Path.Combine(directory, "twilio-auth.local.dpapi");
            if (!File.Exists(path)) return "";
            var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path),
                Encoding.UTF8.GetBytes("TravianBotSharp-Twilio-v1"), DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        public bool IsConfigured => Regex.IsMatch(AccountSid, "\\AAC[0-9a-fA-F]{32}\\z")
            && !string.IsNullOrWhiteSpace(AuthToken) && IsPhone(FromNumber) && IsPhone(ToNumber);

        private static bool IsPhone(string value) => Regex.IsMatch(value, "\\A\\+[1-9][0-9]{7,14}\\z");
    }
}
