using System.Net.Sockets;
using MailKit.Net.Smtp;
using Microsoft.Data.Sqlite;

namespace Dod.Api.Notifications;

public static class EmailFailure
{
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
                _ => null
            };
            if (code is not null) return code;
        }
        return error is IOException ? "connection-io" : error is SmtpProtocolException ? "smtp-protocol" : "unexpected";
    }
}
