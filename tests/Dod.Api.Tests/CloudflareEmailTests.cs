using System.Net;
using System.Text.Json;
using Dod.Api.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Dod.Api.Tests;

public sealed class CloudflareEmailTests
{
    private sealed class EnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Email:Enabled"] = "true", ["Email:Cloudflare:AccountId"] = new string('a', 32),
        ["Email:Cloudflare:ApiToken"] = "test-token", ["Email:From"] = "dodo <summaries@dodojournal.com>",
        ["Email:PublicUrl"] = "https://dodop.duckdns.org"
    }).Build();
    private static readonly EmailMessage Message = new("alice@example.com", "Weekly summary", "<p>Hello</p>", "Hello");

    [Theory]
    [InlineData("delivered")]
    [InlineData("queued")]
    public async Task SendsAuthenticatedJsonWithBothBodiesAndAcceptsRecipient(string status)
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"https://api.cloudflare.com/client/v4/accounts/{new string('a', 32)}/email/sending/send", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization.Parameter);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.True(request.Content.Headers.ContentLength > 0);
            using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            var body = payload.RootElement;
            Assert.Equal("summaries@dodojournal.com", body.GetProperty("from").GetProperty("address").GetString());
            Assert.Equal("dodo", body.GetProperty("from").GetProperty("name").GetString());
            Assert.Equal(Message.To, body.GetProperty("to").GetString());
            Assert.Equal(Message.Subject, body.GetProperty("subject").GetString());
            Assert.Equal(Message.Html, body.GetProperty("html").GetString());
            Assert.Equal(Message.Text, body.GetProperty("text").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { success = true, result = new Dictionary<string, string[]> { [status] = [Message.To] } })) };
        }));
        var transport = new CloudflareEmailTransport(Config(), new EnvironmentStub(), client);
        Assert.True(transport.Available);
        await transport.Send(Message, "outbox-id", CancellationToken.None);
    }

    [Theory]
    [InlineData(401, "private-token alice@example.com", "cloudflare-http-401")]
    [InlineData(403, "private-token alice@example.com", "cloudflare-http-403")]
    [InlineData(429, "private-token alice@example.com", "cloudflare-http-429")]
    [InlineData(503, "private-token alice@example.com", "cloudflare-http-503")]
    [InlineData(302, "", "cloudflare-http-302")]
    [InlineData(200, "{\"success\":false,\"errors\":[{\"message\":\"private-token alice@example.com\"}]}", "cloudflare-api-rejected")]
    [InlineData(200, "not json", "cloudflare-invalid-response")]
    [InlineData(200, "{\"success\":true,\"result\":null}", "cloudflare-invalid-response")]
    [InlineData(200, "{\"success\":true,\"result\":{\"permanent_bounces\":[\"alice@example.com\"]}}", "cloudflare-permanent-bounce")]
    [InlineData(200, "{\"success\":true,\"result\":{\"queued\":[\"someone-else@example.com\"]}}", "cloudflare-recipient-not-accepted")]
    public async Task RejectsFailedOrUnacceptedResponsesWithoutExposingProviderBody(int status, string body, string reason)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) })));
        var error = await Assert.ThrowsAsync<CloudflareEmailException>(() =>
            new CloudflareEmailTransport(Config(), new EnvironmentStub(), client).Send(Message, "id", CancellationToken.None));
        Assert.Equal(reason, EmailFailure.Code(error));
        Assert.Equal("send", EmailFailure.Stage(error));
        Assert.DoesNotContain("private-token", error.ToString());
        Assert.DoesNotContain(Message.To, error.ToString());
    }

    [Theory]
    [InlineData("Email:Enabled", "false")]
    [InlineData("Email:Cloudflare:AccountId", "token-id")]
    [InlineData("Email:Cloudflare:AccountId", "../../unsafe")]
    [InlineData("Email:Cloudflare:ApiToken", "")]
    [InlineData("Email:From", "invalid")]
    [InlineData("Email:PublicUrl", "http://journal.example")]
    [InlineData("Email:PublicUrl", "https://journal.example/path")]
    public async Task InvalidConfigurationPreventsRequests(string key, string value)
    {
        var config = Config(); config[key] = value;
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("Must not send")));
        var transport = new CloudflareEmailTransport(config, new EnvironmentStub(), client);
        Assert.False(transport.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.Send(Message, "id", CancellationToken.None));
    }

    [Fact]
    public async Task CancellationReachesHttpRequest()
    {
        using var timeout = new CancellationTokenSource();
        using var client = new HttpClient(new Handler(async (_, ct) =>
        {
            timeout.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CloudflareEmailTransport(Config(), new EnvironmentStub(), client).Send(Message, "id", timeout.Token));
    }
}
