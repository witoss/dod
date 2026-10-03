using System.Globalization;
using System.Net;
using System.Text;

namespace Dod.Api.Notifications;

public static class EmailTemplates
{
    public static EmailMessage PasswordReset(string email, string url) => new(email, "Reset your dodo password",
        Shell($"<h1>Reset your password</h1><p><a href=\"{E(url)}\">Choose a new password</a></p><p>This link expires in 30 minutes and works once. If you did not request this, ignore this email; your password has not changed.</p>"),
        $"Reset your dodo password: {url}\nThis link expires in 30 minutes and works once. If you did not request it, ignore this email; your password has not changed.");
    public static EmailMessage PasswordChanged(string email) => new(email, "Your dodo password was changed",
        Shell("<h1>Password changed</h1><p>Your password was reset and existing sessions were signed out. If you did not do this, request another password reset from the sign-in page.</p>"),
        "Your dodo password was reset and existing sessions were signed out. If you did not do this, request another password reset from the sign-in page.");
    private static string E(string value) => WebUtility.HtmlEncode(value);
    private static string Number(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Shell(string body) => "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"></head>"
        + "<body style=\"margin:0;background:#f3f6ef;font-family:Arial,sans-serif;color:#22382b\"><div style=\"max-width:720px;margin:24px auto;padding:24px;background:white;border-radius:18px\">"
        + "<p style=\"font-size:28px;font-weight:bold;margin:0\">dodo</p><p>discipline over dopamine</p>" + body + "</div></body></html>";
    public static EmailMessage Verification(string email, string url) => new(email, "Confirm your dodo email",
        Shell($"<h1>One small step to your weekly recap</h1><p>Confirm this email address to receive the weekly summaries you requested.</p><p><a href=\"{E(url)}\" style=\"display:inline-block;background:#285b3c;color:white;padding:14px 22px;border-radius:8px\">Confirm email</a></p><p>This link expires in 24 hours. If you did not request it, ignore this email.</p>"),
        $"Confirm your dodo email: {url}\nThis link expires in 24 hours. If you did not request it, ignore this email.");
    public static EmailMessage Weekly(string email, string nickname, WeeklySummary week, string home, string unsubscribe)
    {
        var weight = week.KgLost is null ? "Not enough measurements to calculate weight change."
            : week.KgLost > 0 ? $"{Number(week.KgLost.Value)} kg lost" : week.KgLost < 0 ? $"{Number(-week.KgLost.Value)} kg gained" : "Weight stayed the same";
        var complete = week.Activities.Sum(a => a.Days.Count(d => d.Completed == true));
        var total = week.Activities.Count * 7;
        var text = new StringBuilder($"Hi {nickname}!\nYour week: {week.WeekStart} – {week.WeekEnd}\n{weight}\n{complete}/{total} weekly goals completed.\n{week.ActivityXp} activity XP + {week.BonusXp} bonus XP. Total: {week.Experience.TotalXp} XP, level {week.Experience.Level}.\n");
        var html = new StringBuilder($"<h1>Your week, one day at a time</h1><p>Hi {E(nickname)}! Here is your recap for <strong>{week.WeekStart} – {week.WeekEnd}</strong>.</p><h2>{E(weight)}</h2><p>First to last measurement within this week ({week.WeightMeasurements} measurements).</p><p><strong>{complete}/{total}</strong> weekly goals completed · <strong>{week.ActivityXp} activity XP + {week.BonusXp} bonus XP</strong></p><p>Level {week.Experience.Level} · {week.Experience.TotalXp} total XP · {week.Experience.XpToNextLevel} XP to your next level</p>");
        html.Append(week.BonusXp == 50 ? "<p style=\"background:#d7efdf;padding:14px;border-radius:8px\"><strong>All green! You earned the 50 XP weekly bonus.</strong></p>" : "<p>Keep building your routine. Complete every weekly goal Monday–Sunday to earn an extra 50 XP.</p>");
        html.Append("<h2>Your weekly activity grid</h2><table style=\"width:100%;border-collapse:separate;border-spacing:3px;font-size:12px\"><caption style=\"text-align:left;padding:8px\">Green ✓ = completed; red × = not completed or not reported</caption><thead><tr><th scope=\"col\">Activity</th>");
        foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }) html.Append($"<th scope=\"col\">{day}</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var activity in week.Activities)
        {
            html.Append($"<tr><th scope=\"row\" style=\"text-align:left;padding:8px\">{E(activity.Name)}</th>");
            text.Append(activity.Name + ": ");
            foreach (var day in activity.Days)
            {
                var yes = day.Completed == true; var label = yes ? "Completed" : day.Completed == false ? "Not completed" : "Not reported";
                html.Append($"<td title=\"{day.Date}: {label}, {day.Xp} XP\" style=\"text-align:center;padding:10px 4px;border-radius:5px;background:{(yes ? "#d7efdf" : "#fce0dc")};color:{(yes ? "#17472b" : "#852d24")}\">{(yes ? "✓" : "×")}<br>{day.Xp}</td>");
                text.Append($"{day.Date} {label} ({day.Xp} XP); ");
            }
            html.Append("</tr>"); text.AppendLine();
        }
        html.Append("</tbody></table><p>Numbers in cells are daily XP. The grid uses the activity list fixed for this week. Other activities reported during the week also count toward activity XP.</p><h2>Your challenges</h2>");
        if (week.Challenges.Count == 0) { html.Append("<p>No challenges for this period. Invite a friend to your next one!</p>"); text.AppendLine("No challenges for this period."); }
        foreach (var challenge in week.Challenges)
        {
            var rank = challenge.Rank is null ? "Not ranked yet" : $"Rank {challenge.Rank} of {challenge.Participants}";
            var line = $"{challenge.Name}: {challenge.Status} · {rank}" + (challenge.KgLost is null ? "" : $" · {Number(challenge.KgLost.Value)} kg lost since challenge start");
            html.Append($"<p>{E(line)}</p>"); text.AppendLine(line);
        }
        html.Append($"<p>Standings use measurements through Sunday. XP and account totals reflect when this email was prepared; later corrections appear in the app.</p><p><a href=\"{E(home)}\">Open your journal</a></p><p style=\"font-size:12px\">You enabled weekly summaries in your profile. <a href=\"{E(unsubscribe)}\">Stop weekly emails</a>.</p>");
        text.AppendLine($"Open your journal: {home}\nStop weekly emails: {unsubscribe}\nLater corrections appear in the app.");
        return new(email, $"Your dodo week · {week.WeekStart} – {week.WeekEnd}", Shell(html.ToString()), text.ToString());
    }
}
