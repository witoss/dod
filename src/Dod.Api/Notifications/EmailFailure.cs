using System.Net.Sockets;
using MailKit.Net.Smtp;
using Microsoft.Data.Sqlite;

namespace Dod.Api.Notifications;

public static class EmailFailure
{
    private const string StageKey = "dodo.smtp.stage";
    internal static void SetStage(Exception error, string stage) => error.Data[StageKey] = stage;
    public static string? Stage(Exception error) => error.Data[StageKey] is string stage
        && stage is "prepare" or "connect" or "authenticate" or "send" or "disconnect" ? stage : null;

    // Never expose exception messages: SMTP servers may echo addresses or credentials.
    public static string Code(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            var code = cause switch
            {
                SmtpCommandException smtp => $"smtp-{(int)smtp.StatusCode}-{smtp.ErrorCode}",
                MailKit.Security.AuthenticationException => "smtp-authentication",
                MailKit.Security.SslHandshakeException => "tls-handshake",
                System.Security.Authentication.AuthenticationException => "tls-authentication",
                SocketException socket => $"connection-{socket.SocketErrorCode}",
                SqliteException sqlite => $"database-{sqlite.SqliteErrorCode}",
                OperationCanceledException => "timeout-or-shutdown",
                SmtpProtocolException => "smtp-protocol",
                IOException => "connection-io",
                _ => null
            };
            if (code is not null) return code;
        }
        return $"unexpected-{error.GetBaseException().GetType().FullName}";
    }
}
