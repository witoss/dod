using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MimeKit;

namespace Dod.Api.Notifications;

public sealed class CloudflareEmailException(string code) : Exception("Cloudflare email submission failed.")
{
    public string Code { get; } = code;
}

public sealed class CloudflareEmailTransport(IConfiguration config, IHostEnvironment environment, HttpClient client) : IEmailTransport
{
    public bool Available => config.GetValue<bool>("Email:Enabled")
        && config["Email:Cloudflare:AccountId"] is { Length: 32 } account && account.All(Uri.IsHexDigit)
        && !string.IsNullOrWhiteSpace(config["Email:Cloudflare:ApiToken"])
        && MailboxAddress.TryParse(config["Email:From"] ?? "", out var sender) && sender.Address.Contains('@')
        && Uri.TryCreate(config["Email:PublicUrl"], UriKind.Absolute, out var url)
        && (url.Scheme == "https" || (environment.IsDevelopment() && url.Scheme == "http"))
        && url.AbsolutePath == "/" && url.Query.Length == 0 && url.Fragment.Length == 0 && url.UserInfo.Length == 0;

    public async Task Send(EmailMessage message, string id, CancellationToken cancellationToken)
    {
        var stage = "prepare";
        try
        {
            if (!Available) throw new InvalidOperationException("Cloudflare email sending is not configured.");
            var sender = MailboxAddress.Parse(config["Email:From"]!);
            var recipient = MailboxAddress.Parse(message.To).Address;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.cloudflare.com/client/v4/accounts/{config["Email:Cloudflare:AccountId"]}/email/sending/send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Email:Cloudflare:ApiToken"]);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                from = new { address = sender.Address, name = sender.Name },
                to = recipient, subject = message.Subject, html = message.Html, text = message.Text
            }), Encoding.UTF8, "application/json");
            stage = "send";
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new CloudflareEmailException($"cloudflare-http-{(int)response.StatusCode}");
            // Never log the response body: it can contain recipient addresses and provider messages.
            // A malformed provider response must follow transport retry handling, not payload-decode handling.
            try
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success)
                    || success.ValueKind != JsonValueKind.True)
                    throw new CloudflareEmailException("cloudflare-api-rejected");
                if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                    throw new CloudflareEmailException("cloudflare-invalid-response");
                if (ContainsRecipient(result, "permanent_bounces", recipient))
                    throw new CloudflareEmailException("cloudflare-permanent-bounce");
                if (!ContainsRecipient(result, "delivered", recipient) && !ContainsRecipient(result, "queued", recipient))
                    throw new CloudflareEmailException("cloudflare-recipient-not-accepted");
            }
            catch (JsonException) { throw new CloudflareEmailException("cloudflare-invalid-response"); }
        }
        catch (Exception error)
        {
            EmailFailure.SetStage(error, stage);
            throw;
        }
    }

    private static bool ContainsRecipient(JsonElement result, string property, string recipient) =>
        result.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
        && list.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
            && string.Equals(item.GetString(), recipient, StringComparison.OrdinalIgnoreCase));
}
