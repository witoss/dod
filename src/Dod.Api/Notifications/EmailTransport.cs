using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Dod.Api.Notifications;

public record EmailMessage(string To, string Subject, string Html, string Text);
public interface IEmailTransport
{
    bool Available { get; }
    Task Send(EmailMessage message, string id, CancellationToken cancellationToken);
}
public sealed class SmtpEmailTransport(IConfiguration config, IHostEnvironment environment) : IEmailTransport
{
    public bool Available
    {
        get
        {
            var security = config["Email:Security"] ?? "StartTls";
            return config.GetValue<bool>("Email:Enabled") && !string.IsNullOrWhiteSpace(config["Email:Host"])
                && MailboxAddress.TryParse(config["Email:From"] ?? "", out _)
                && config.GetValue("Email:Port", 587) is > 0 and <= 65535
                && (security is "StartTls" or "SslOnConnect" || (security == "None" && environment.IsDevelopment()))
                && Uri.TryCreate(config["Email:PublicUrl"], UriKind.Absolute, out var url)
                && (url.Scheme == "https" || (environment.IsDevelopment() && url.Scheme == "http"))
                && url.AbsolutePath == "/" && url.Query.Length == 0 && url.Fragment.Length == 0 && url.UserInfo.Length == 0;
        }
    }
    public async Task Send(EmailMessage message, string id, CancellationToken cancellationToken)
    {
        var stage = "prepare";
        try
        {
            if (!Available) throw new InvalidOperationException("Email sending is not configured.");
            var security = config["Email:Security"] ?? "StartTls";
            var options = security switch
            {
                "StartTls" => SecureSocketOptions.StartTls,
                "SslOnConnect" => SecureSocketOptions.SslOnConnect,
                "None" when environment.IsDevelopment() => SecureSocketOptions.None,
                _ => throw new InvalidOperationException("Use StartTls or SslOnConnect for production email.")
            };
            using var mime = new MimeMessage();
            mime.From.Add(MailboxAddress.Parse(config["Email:From"]!));
            mime.To.Add(MailboxAddress.Parse(message.To));
            mime.Subject = message.Subject; mime.MessageId = $"{id}@{new Uri(config["Email:PublicUrl"]!).Host}";
            mime.Body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text }.ToMessageBody();
            using var client = new SmtpClient(); client.Timeout = 30000;
            stage = "connect";
            await client.ConnectAsync(config["Email:Host"]!, config.GetValue("Email:Port", 587), options, cancellationToken);
            if (!string.IsNullOrWhiteSpace(config["Email:Username"]))
            {
                stage = "authenticate";
                await client.AuthenticateAsync(config["Email:Username"]!, config["Email:Password"] ?? "", cancellationToken);
            }
            stage = "send";
            await client.SendAsync(mime, cancellationToken);
            stage = "disconnect";
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception error)
        {
            EmailFailure.SetStage(error, stage);
            throw;
        }
    }
}
