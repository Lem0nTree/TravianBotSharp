using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace MainCore.Services
{
    public interface ITwilioCallClient
    {
        Task<string> CallAsync(IReadOnlyCollection<VillageDto> villages, CancellationToken cancellationToken);
    }

    public sealed class TwilioCallClient(HttpClient httpClient, TwilioAttackAlertSettings settings) : ITwilioCallClient, IDisposable
    {
        public async Task<string> CallAsync(IReadOnlyCollection<VillageDto> villages, CancellationToken cancellationToken)
        {
            if (!settings.IsConfigured) throw new InvalidOperationException("Twilio credentials or phone numbers are missing or invalid.");

            var names = string.Join(", ", villages.Take(5).Select(x =>
                $"{new string((x.Name ?? "Village").Where(ch => !char.IsControl(ch)).Take(80).ToArray())}, coordinates {x.X}, {x.Y}"));
            var message = $"Travian attack alert. {villages.Count} villages are under attack. {names}. Open Travian now.";
            var twiml = new XElement("Response", new XElement("Say", new XAttribute("loop", "2"), message)).ToString(SaveOptions.DisableFormatting);

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.twilio.com/2010-04-01/Accounts/{settings.AccountSid}/Calls.json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.AccountSid}:{settings.AuthToken}")));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = settings.ToNumber,
                ["From"] = settings.FromNumber,
                ["Twiml"] = twiml,
                ["Timeout"] = "30",
                ["TimeLimit"] = "60",
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Do not log the response body: it may contain phone numbers or account details.
                throw new HttpRequestException($"Twilio rejected the call (HTTP {(int)response.StatusCode}). Check Twilio Console Voice logs, credentials, verified recipient and geographic permissions.");
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.GetProperty("sid").GetString() ?? "";
        }

        public void Dispose() => httpClient.Dispose();
    }
}
